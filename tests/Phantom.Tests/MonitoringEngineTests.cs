using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Activity;
using Phantom.Core.Abstractions;
using Phantom.Core.Models;
using Phantom.Core.Monitoring;
using Phantom.Storage;

namespace Phantom.Tests;

public class MonitoringEngineTests
{
    private static MonitoringEngine Create(FakeProcessSource processes, FakeMetricsSource metrics, IHistoryStore store)
        => new(processes, metrics, new ActivityAnalyzer(), store, new SystemClock(), NullLogger<MonitoringEngine>.Instance, "test")
        {
            Options = new MonitoringOptions { SampleInterval = TimeSpan.FromMilliseconds(50), PersistInterval = TimeSpan.Zero },
        };

    private static async Task<List<MonitoringFrame>> Collect(MonitoringEngine engine, int count, Action<int>? onFrame = null)
    {
        var frames = new List<MonitoringFrame>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.FrameProduced += f =>
        {
            lock (frames)
            {
                frames.Add(f);
                onFrame?.Invoke(frames.Count);
                if (frames.Count >= count)
                {
                    done.TrySetResult();
                }
            }
        };
        engine.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await engine.StopAsync();
        lock (frames)
        {
            return [.. frames];
        }
    }

    [Fact]
    public async Task Produces_frames_with_processes_hierarchy_and_metrics()
    {
        using var dir = new TempDirectory();
        using var store = new SqliteHistoryStore(dir.File("h.db"));
        var processes = new FakeProcessSource { Processes = [Make.Raw(1, 0), Make.Raw(2, 1, startSeconds: 5)] };
        await using var engine = Create(processes, new FakeMetricsSource(), store);

        var frames = await Collect(engine, 3);

        var last = frames[^1];
        Assert.Equal(2, last.Snapshot.Count);
        Assert.Equal(2, last.Metrics.ProcessCount);
        Assert.Equal(10, last.Metrics.CpuPercent);
        Assert.Equal(1, last.Hierarchy.GetParent(2)!.Pid);
        Assert.True(last.ProcessDataFresh);
        Assert.Contains(frames[0].NewEvents, e => e.Description.Contains("session started"));
    }

    [Fact]
    public async Task Persists_events_metrics_and_the_session()
    {
        using var dir = new TempDirectory();
        using var store = new SqliteHistoryStore(dir.File("h.db"));
        var processes = new FakeProcessSource { Processes = [Make.Raw(1, 0)] };
        await using var engine = Create(processes, new FakeMetricsSource(), store);

        await Collect(engine, 4, n =>
        {
            if (n == 2)
            {
                processes.Processes = [Make.Raw(1, 0), Make.Raw(77, 1, startSeconds: 60, name: "new.exe")];
            }
        });

        var events = store.QueryEvents(new ActivityQuery());
        Assert.Contains(events, e => e.Category == ActivityCategory.ProcessStarted && e.Pid == 77);
        Assert.Contains(events, e => e.Description.Contains("session ended"));
        Assert.True(store.GetStats().MetricSampleCount >= 1);
        var session = Assert.Single(store.GetSessions(5));
        Assert.NotNull(session.EndedUtc);
        Assert.Equal("test", session.AppVersion);
    }

    [Fact]
    public async Task Process_collection_failure_is_reported_once_and_recovers()
    {
        using var dir = new TempDirectory();
        using var store = new SqliteHistoryStore(dir.File("h.db"));
        var processes = new FakeProcessSource { Processes = [Make.Raw(1, 0)] };
        await using var engine = Create(processes, new FakeMetricsSource(), store);

        var frames = await Collect(engine, 8, n =>
        {
            processes.EnumerateFailure = n is >= 2 and < 5 ? new InvalidOperationException("denied") : null;
        });

        var events = frames.SelectMany(f => f.NewEvents).ToList();
        Assert.Single(events, e => e.Category == ActivityCategory.MonitoringInterruption);
        Assert.Contains(events, e => e.Description.Contains("recovered"));
        Assert.Contains(frames, f => !f.ProcessDataFresh && f.Snapshot.Count == 1 && f.Metrics.ProcessCount is null);
        Assert.True(frames[^1].ProcessDataFresh);
        // Recovery must not flood the timeline with "started" events for existing processes.
        Assert.DoesNotContain(events, e => e.Category == ActivityCategory.ProcessStarted);
    }

    [Fact]
    public async Task Metric_failure_yields_unavailable_values_not_a_crash()
    {
        using var dir = new TempDirectory();
        using var store = new SqliteHistoryStore(dir.File("h.db"));
        var metrics = new FakeMetricsSource { Next = () => throw new InvalidOperationException("sensor gone") };
        await using var engine = Create(new FakeProcessSource { Processes = [Make.Raw(1, 0)] }, metrics, store);

        var frames = await Collect(engine, 3);

        Assert.All(frames, f => Assert.Null(f.Metrics.CpuPercent));
        Assert.Single(frames.SelectMany(f => f.NewEvents), e => e.Category == ActivityCategory.MonitoringInterruption);
        Assert.Equal(1, frames[^1].Snapshot.Count);
    }

    [Fact]
    public async Task Keeps_running_when_history_is_unavailable_or_a_subscriber_throws()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.File("blocked.db"));
        using var store = new SqliteHistoryStore(dir.File("blocked.db"));
        Assert.False(store.IsAvailable);
        await using var engine = Create(new FakeProcessSource { Processes = [Make.Raw(1, 0)] }, new FakeMetricsSource(), store);
        engine.FrameProduced += _ => throw new InvalidOperationException("bad subscriber");

        var frames = await Collect(engine, 3);
        Assert.Equal(1, frames[^1].Snapshot.Count);
    }

    [Fact]
    public async Task Pausing_stops_sampling_and_is_recorded()
    {
        using var dir = new TempDirectory();
        using var store = new SqliteHistoryStore(dir.File("h.db"));
        int samples = 0;
        var metrics = new FakeMetricsSource
        {
            Next = () =>
            {
                Interlocked.Increment(ref samples);
                return new SystemMetricsSample { TimestampUtc = DateTime.UtcNow };
            },
        };
        await using var engine = Create(new FakeProcessSource { Processes = [Make.Raw(1, 0)] }, metrics, store);
        engine.Start();
        await Task.Delay(300);
        engine.SetPaused(true);
        await Task.Delay(200);
        int atPause = Volatile.Read(ref samples);
        await Task.Delay(400);
        Assert.Equal(atPause, Volatile.Read(ref samples));
        Assert.True(engine.IsPaused);

        engine.SetPaused(false);
        await Task.Delay(300);
        Assert.True(Volatile.Read(ref samples) > atPause);
        await engine.StopAsync();

        var events = store.QueryEvents(new ActivityQuery { Categories = [ActivityCategory.Monitoring] });
        Assert.Contains(events, e => e.Description.Contains("paused"));
        Assert.Contains(events, e => e.Description.Contains("resumed"));
    }
}
