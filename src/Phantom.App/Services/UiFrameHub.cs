using System.Windows.Threading;
using Phantom.Core.Metrics;
using Phantom.Core.Models;
using Phantom.Core.Monitoring;

namespace Phantom.App.Services;

/// <summary>
/// Bridges the background sampling thread and the UI thread. Frames are coalesced: if the UI
/// is busy when several frames arrive, only the newest is delivered, so the interface can never
/// fall behind the collector or queue up unbounded work.
/// </summary>
public sealed class UiFrameHub
{
    public const int HistoryCapacity = 120;
    private const int RecentEventCapacity = 2000;

    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private readonly List<ActivityEvent> _pendingEvents = [];
    private MonitoringFrame? _pending;
    private bool _scheduled;

    public UiFrameHub(MonitoringEngine engine, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        engine.FrameProduced += OnFrameProduced;
    }

    /// <summary>Raised on the UI thread with the newest frame and every event since the previous delivery.</summary>
    public event Action<MonitoringFrame, IReadOnlyList<ActivityEvent>>? FrameArrived;

    public MonitoringFrame? Latest { get; private set; }

    /// <summary>Recent metric samples for live charts (UI thread only).</summary>
    public RingBuffer<SystemMetricsSample> MetricHistory { get; } = new(HistoryCapacity);

    /// <summary>Events from this session, oldest first (UI thread only). Used when history is not persisted.</summary>
    public List<ActivityEvent> SessionEvents { get; } = [];

    private void OnFrameProduced(MonitoringFrame frame)
    {
        lock (_gate)
        {
            _pending = frame;
            _pendingEvents.AddRange(frame.NewEvents);
            if (_scheduled)
            {
                return;
            }

            _scheduled = true;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, Deliver);
    }

    private void Deliver()
    {
        MonitoringFrame? frame;
        List<ActivityEvent> events;
        lock (_gate)
        {
            frame = _pending;
            events = [.. _pendingEvents];
            _pending = null;
            _pendingEvents.Clear();
            _scheduled = false;
        }

        if (frame is null)
        {
            return;
        }

        Latest = frame;
        if (frame.ProcessDataFresh || MetricHistory.Count == 0)
        {
            MetricHistory.Add(frame.Metrics);
        }

        SessionEvents.AddRange(events);
        if (SessionEvents.Count > RecentEventCapacity)
        {
            SessionEvents.RemoveRange(0, SessionEvents.Count - RecentEventCapacity);
        }

        FrameArrived?.Invoke(frame, events);
    }

    /// <summary>Extracts one metric from the live history as chart input; missing samples become NaN gaps.</summary>
    public double[] Series(Func<SystemMetricsSample, double?> selector)
    {
        var result = new double[MetricHistory.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = selector(MetricHistory[i]) ?? double.NaN;
        }

        return result;
    }
}
