using Stopwatch = System.Diagnostics.Stopwatch;
using Microsoft.Extensions.Logging;
using Phantom.Core.Abstractions;
using Phantom.Core.Metrics;
using Phantom.Core.Models;
using Phantom.Core.Processes;

namespace Phantom.Core.Monitoring;

/// <summary>Everything collected in one sampling tick. Immutable; safe to hand to any thread.</summary>
public sealed record MonitoringFrame
{
    public required SystemMetricsSample Metrics { get; init; }
    public required ProcessSnapshot Snapshot { get; init; }
    public required ProcessHierarchy Hierarchy { get; init; }
    public required IReadOnlyList<ActivityEvent> NewEvents { get; init; }
    /// <summary>How long collection took, shown in the status bar as a self-cost indicator.</summary>
    public TimeSpan CollectionTime { get; init; }
    /// <summary>False when the process list could not be refreshed this tick and the previous one is being reused.</summary>
    public bool ProcessDataFresh { get; init; } = true;
}

/// <summary>
/// Owns the single background sampling loop. All collection, analysis and persistence
/// happens off the UI thread; consumers receive immutable frames through <see cref="FrameProduced"/>.
/// </summary>
public sealed class MonitoringEngine : IAsyncDisposable
{
    private readonly ISystemMetricsSource _metrics;
    private readonly IActivityAnalyzer _analyzer;
    private readonly IHistoryStore _history;
    private readonly IClock _clock;
    private readonly ILogger<MonitoringEngine> _logger;
    private readonly ProcessSnapshotBuilder _builder;
    private readonly string _appVersion;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly List<SystemMetricsSample> _pendingMetrics = [];
    private readonly List<ActivityEvent> _injected = [];

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile MonitoringOptions _options = new();
    private volatile bool _paused;
    private volatile MonitoringFrame? _latest;
    private long _sessionId;
    private DateTime _lastTickUtc;
    private DateTime _lastPersistUtc;
    private bool _processFailureReported;
    private bool _metricsFailureReported;
    private ProcessSnapshot _lastSnapshot = ProcessSnapshot.Empty;
    private ProcessHierarchy _lastHierarchy = ProcessHierarchy.Empty;

    public MonitoringEngine(
        IProcessDataSource processes,
        ISystemMetricsSource metrics,
        IActivityAnalyzer analyzer,
        IHistoryStore history,
        IClock clock,
        ILogger<MonitoringEngine> logger,
        string appVersion = "")
    {
        _metrics = metrics;
        _analyzer = analyzer;
        _history = history;
        _clock = clock;
        _logger = logger;
        _appVersion = appVersion;
        _builder = new ProcessSnapshotBuilder(processes, clock, Environment.ProcessorCount, logger);
    }

    /// <summary>Raised on the sampling thread after every tick. Handlers must be quick and must not throw.</summary>
    public event Action<MonitoringFrame>? FrameProduced;

    public MonitoringFrame? Latest => _latest;
    public bool IsRunning => _loop is { IsCompleted: false };
    public bool IsPaused => _paused;
    public long SessionId => Interlocked.Read(ref _sessionId);

    public MonitoringOptions Options
    {
        get => _options;
        set
        {
            _options = value;
            Wake();
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused)
        {
            return;
        }

        _paused = paused;
        Inject(new ActivityEvent
        {
            TimestampUtc = _clock.UtcNow,
            Category = ActivityCategory.Monitoring,
            Provenance = EventProvenance.Observed,
            Description = paused ? "Monitoring paused by the user." : "Monitoring resumed by the user.",
        });
        Wake();
    }

    /// <summary>Requests an immediate sample without waiting for the interval to elapse.</summary>
    public void RefreshNow() => Wake();

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // A concurrent wake already released it.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Inject(ActivityEvent e)
    {
        lock (_injected)
        {
            _injected.Add(e);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var started = _clock.UtcNow;
        try
        {
            Interlocked.Exchange(ref _sessionId, _history.BeginSession(started, _appVersion));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open a history session");
        }

        Inject(new ActivityEvent
        {
            TimestampUtc = started,
            Category = ActivityCategory.Monitoring,
            Provenance = EventProvenance.Observed,
            Description = "Monitoring session started.",
        });
        _lastPersistUtc = started;

        bool wasPaused = false;
        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            if (!_paused)
            {
                if (wasPaused)
                {
                    // Deltas across a pause are meaningless; start fresh.
                    _builder.ResetBaselines();
                    _lastTickUtc = default;
                }

                try
                {
                    Tick(sw);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Monitoring tick failed");
                }
            }
            else
            {
                FlushInjectedWhilePaused();
            }

            wasPaused = _paused;
            var delay = _options.SampleInterval - sw.Elapsed;
            if (delay < TimeSpan.FromMilliseconds(50))
            {
                delay = TimeSpan.FromMilliseconds(50);
            }

            try
            {
                await _wake.WaitAsync(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Shutdown();
    }

    private void FlushInjectedWhilePaused()
    {
        var events = TakeInjected();
        if (events.Count == 0 || _latest is not { } latest)
        {
            if (events.Count > 0)
            {
                Persist(events);
            }

            return;
        }

        Persist(events);
        Publish(latest with { NewEvents = events, ProcessDataFresh = false });
    }

    private List<ActivityEvent> TakeInjected()
    {
        lock (_injected)
        {
            var copy = new List<ActivityEvent>(_injected);
            _injected.Clear();
            return copy;
        }
    }

    private void Tick(Stopwatch sw)
    {
        var now = _clock.UtcNow;
        var events = TakeInjected();
        var options = _options;

        // A gap much longer than the interval means the machine slept or Phantom was starved.
        if (_lastTickUtc != default)
        {
            var gap = now - _lastTickUtc;
            var threshold = TimeSpan.FromTicks(Math.Max(options.SampleInterval.Ticks * 5, TimeSpan.FromSeconds(10).Ticks));
            if (gap > threshold)
            {
                events.Add(new ActivityEvent
                {
                    TimestampUtc = now,
                    Category = ActivityCategory.MonitoringInterruption,
                    Provenance = EventProvenance.Inferred,
                    Description = $"No samples were collected for {Format.Duration(gap)} (system sleep, hibernation or a stalled process). Activity in this gap was not recorded.",
                });
                _builder.ResetBaselines();
                _analyzer.Reset();
            }
        }

        _lastTickUtc = now;

        SystemMetricsSample metrics;
        try
        {
            metrics = _metrics.Sample();
            _metricsFailureReported = false;
        }
        catch (Exception ex)
        {
            metrics = new SystemMetricsSample { TimestampUtc = now };
            if (!_metricsFailureReported)
            {
                _metricsFailureReported = true;
                _logger.LogWarning(ex, "System metric collection failed");
                events.Add(Interruption(now, $"System metric collection failed ({ex.GetType().Name}). Values are shown as unavailable until it recovers."));
            }
        }

        bool fresh = true;
        try
        {
            _lastSnapshot = _builder.Build();
            _lastHierarchy = ProcessHierarchy.Build(_lastSnapshot.Processes);
            if (_processFailureReported)
            {
                _processFailureReported = false;
                events.Add(new ActivityEvent
                {
                    TimestampUtc = now,
                    Category = ActivityCategory.Monitoring,
                    Provenance = EventProvenance.Observed,
                    Description = "Process collection recovered.",
                });
                _analyzer.Reset();
            }
        }
        catch (Exception ex)
        {
            fresh = false;
            if (!_processFailureReported)
            {
                _processFailureReported = true;
                _logger.LogWarning(ex, "Process collection failed");
                events.Add(Interruption(now, $"Process collection failed ({ex.GetType().Name}). The process list is stale until it recovers."));
            }
        }

        metrics = metrics with { ProcessCount = fresh ? _lastSnapshot.Count : null };

        if (fresh)
        {
            try
            {
                events.AddRange(_analyzer.Analyze(_lastSnapshot, metrics));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Activity analysis failed");
            }
        }

        _pendingMetrics.Add(metrics);
        if (now - _lastPersistUtc >= options.PersistInterval || now < _lastPersistUtc)
        {
            PersistMetrics(options);
            _lastPersistUtc = now;
        }

        Persist(events);

        Publish(new MonitoringFrame
        {
            Metrics = metrics,
            Snapshot = _lastSnapshot,
            Hierarchy = _lastHierarchy,
            NewEvents = events,
            CollectionTime = sw.Elapsed,
            ProcessDataFresh = fresh,
        });
    }

    private static ActivityEvent Interruption(DateTime now, string description) => new()
    {
        TimestampUtc = now,
        Category = ActivityCategory.MonitoringInterruption,
        Provenance = EventProvenance.Observed,
        Description = description,
    };

    private void PersistMetrics(MonitoringOptions options)
    {
        var averaged = MetricMath.Average(_pendingMetrics);
        _pendingMetrics.Clear();
        if (averaged is null || !options.PersistHistory)
        {
            return;
        }

        try
        {
            _history.AppendMetrics(SessionId, [averaged]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist metric samples");
        }
    }

    private void Persist(IReadOnlyList<ActivityEvent> events)
    {
        if (events.Count == 0 || !_options.PersistHistory)
        {
            return;
        }

        try
        {
            _history.AppendEvents(SessionId, events);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist activity events");
        }
    }

    private void Publish(MonitoringFrame frame)
    {
        _latest = frame;
        var handlers = FrameProduced;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<MonitoringFrame>)handler)(frame);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A frame subscriber failed");
            }
        }
    }

    private void Shutdown()
    {
        var now = _clock.UtcNow;
        try
        {
            PersistMetrics(_options);
            var events = TakeInjected();
            events.Add(new ActivityEvent
            {
                TimestampUtc = now,
                Category = ActivityCategory.Monitoring,
                Provenance = EventProvenance.Observed,
                Description = "Monitoring session ended.",
            });
            Persist(events);
            _history.EndSession(SessionId, now);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not close the history session cleanly");
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            loop = _loop;
            _cts?.Cancel();
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Monitoring loop ended with an error");
            }
        }

        lock (_gate)
        {
            _loop = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _wake.Dispose();
    }
}
