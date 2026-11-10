using Phantom.Activity;
using Phantom.Core.Models;

namespace Phantom.Tests;

public class ActivityAnalyzerTests
{
    private static readonly DateTime Now = Make.T0.AddHours(1);

    [Fact]
    public void First_snapshot_is_a_baseline_and_produces_no_events()
    {
        var analyzer = new ActivityAnalyzer();
        var events = analyzer.Analyze(Make.Snapshot(Now, Make.Process(1, 0), Make.Process(2, 1)), Make.Metrics(Now));
        Assert.Empty(events);
    }

    [Fact]
    public void New_process_is_reported_with_its_kernel_start_time_as_observed()
    {
        var analyzer = new ActivityAnalyzer();
        analyzer.Analyze(Make.Snapshot(Now, Make.Process(1, 0, "explorer.exe")), Make.Metrics(Now));

        var started = Make.Process(2, 1, "notepad.exe", startSeconds: 3601);
        var events = analyzer.Analyze(Make.Snapshot(Now.AddSeconds(2), Make.Process(1, 0, "explorer.exe"), started),
            Make.Metrics(Now.AddSeconds(2)));

        var e = Assert.Single(events);
        Assert.Equal(ActivityCategory.ProcessStarted, e.Category);
        Assert.Equal(EventProvenance.Observed, e.Provenance);
        Assert.Equal(started.StartTimeUtc, e.TimestampUtc);
        Assert.Equal(2, e.Pid);
        Assert.Equal("notepad.exe", e.ProcessName);
        Assert.Contains("explorer.exe (1)", e.Description);
    }

    [Fact]
    public void Exited_process_is_reported_as_inferred_at_the_sample_time()
    {
        var analyzer = new ActivityAnalyzer();
        analyzer.Analyze(Make.Snapshot(Now, Make.Process(1, 0), Make.Process(2, 1, "notepad.exe")), Make.Metrics(Now));
        var events = analyzer.Analyze(Make.Snapshot(Now.AddSeconds(1), Make.Process(1, 0)), Make.Metrics(Now.AddSeconds(1)));

        var e = Assert.Single(events);
        Assert.Equal(ActivityCategory.ProcessExited, e.Category);
        Assert.Equal(EventProvenance.Inferred, e.Provenance);
        Assert.Equal(Now.AddSeconds(1), e.TimestampUtc);
        Assert.Contains("notepad.exe exited", e.Description);
    }

    [Fact]
    public void Pid_reuse_is_reported_as_an_exit_and_a_start()
    {
        var analyzer = new ActivityAnalyzer();
        analyzer.Analyze(Make.Snapshot(Now, Make.Process(5, 0, "old.exe", startSeconds: 1)), Make.Metrics(Now));
        var events = analyzer.Analyze(Make.Snapshot(Now.AddSeconds(1), Make.Process(5, 0, "new.exe", startSeconds: 3600)),
            Make.Metrics(Now.AddSeconds(1)));

        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.Category == ActivityCategory.ProcessStarted && e.ProcessName == "new.exe");
        Assert.Contains(events, e => e.Category == ActivityCategory.ProcessExited && e.ProcessName == "old.exe");
    }

    [Fact]
    public void Application_launch_is_inferred_once_when_a_new_process_shows_a_window()
    {
        var analyzer = new ActivityAnalyzer();
        var existing = Make.Process(1, 0, "explorer.exe", window: true);
        analyzer.Analyze(Make.Snapshot(Now, existing), Make.Metrics(Now));

        var noWindow = Make.Process(2, 1, "app.exe", startSeconds: 3601);
        var first = analyzer.Analyze(Make.Snapshot(Now.AddSeconds(1), existing, noWindow), Make.Metrics(Now.AddSeconds(1)));
        Assert.DoesNotContain(first, e => e.Category == ActivityCategory.ApplicationLaunched);

        var withWindow = noWindow with { HasVisibleWindow = true };
        var second = analyzer.Analyze(Make.Snapshot(Now.AddSeconds(2), existing, withWindow), Make.Metrics(Now.AddSeconds(2)));
        var launch = Assert.Single(second);
        Assert.Equal(ActivityCategory.ApplicationLaunched, launch.Category);
        Assert.Equal(EventProvenance.Inferred, launch.Provenance);

        var third = analyzer.Analyze(Make.Snapshot(Now.AddSeconds(3), existing, withWindow), Make.Metrics(Now.AddSeconds(3)));
        Assert.Empty(third);
    }

    [Fact]
    public void Process_cpu_spike_requires_sustained_usage_and_is_reported_once()
    {
        var analyzer = new ActivityAnalyzer { Options = new ActivityOptions { ProcessCpuSpikePercent = 50, SustainSamples = 3 } };
        var all = new List<ActivityEvent>();
        for (int i = 0; i < 10; i++)
        {
            var at = Now.AddSeconds(i);
            all.AddRange(analyzer.Analyze(Make.Snapshot(at, Make.Process(7, 0, "busy.exe", cpu: 80)), Make.Metrics(at)));
        }

        var spike = Assert.Single(all);
        Assert.Equal(ActivityCategory.ResourceSpike, spike.Category);
        Assert.Equal(7, spike.Pid);
        Assert.Equal(Now.AddSeconds(2), spike.TimestampUtc);
    }

    [Fact]
    public void A_momentary_burst_is_not_a_spike()
    {
        var analyzer = new ActivityAnalyzer();
        var all = new List<ActivityEvent>();
        double[] cpu = [90, 90, 5, 90, 90, 5, 5];
        for (int i = 0; i < cpu.Length; i++)
        {
            var at = Now.AddSeconds(i);
            all.AddRange(analyzer.Analyze(Make.Snapshot(at, Make.Process(7, 0, cpu: cpu[i])), Make.Metrics(at)));
        }

        Assert.Empty(all);
    }

    [Fact]
    public void Spike_is_reported_again_after_usage_clearly_recovers()
    {
        var analyzer = new ActivityAnalyzer { Options = new ActivityOptions { SustainSamples = 2 } };
        var all = new List<ActivityEvent>();
        double[] cpu = [80, 80, 80, 10, 10, 10, 80, 80];
        for (int i = 0; i < cpu.Length; i++)
        {
            var at = Now.AddSeconds(i);
            all.AddRange(analyzer.Analyze(Make.Snapshot(at, Make.Process(7, 0, cpu: cpu[i])), Make.Metrics(at)));
        }

        Assert.Equal(2, all.Count(e => e.Category == ActivityCategory.ResourceSpike));
    }

    [Fact]
    public void System_cpu_and_memory_spikes_are_reported_and_idle_process_is_ignored()
    {
        var analyzer = new ActivityAnalyzer { Options = new ActivityOptions { SustainSamples = 2 } };
        var all = new List<ActivityEvent>();
        for (int i = 0; i < 4; i++)
        {
            var at = Now.AddSeconds(i);
            all.AddRange(analyzer.Analyze(
                Make.Snapshot(at, Make.Process(0, 0, "System Idle Process", cpu: 99, noStart: true), Make.Process(9, 0, "hog.exe", cpu: 40)),
                Make.Metrics(at, cpu: 97, memoryPercent: 95)));
        }

        Assert.Equal(2, all.Count);
        Assert.All(all, e => Assert.Null(e.Pid));
        Assert.Contains(all, e => e.Description.Contains("System CPU") && e.Description.Contains("hog.exe"));
        Assert.Contains(all, e => e.Description.Contains("Memory usage"));
    }

    [Fact]
    public void Unavailable_metrics_never_produce_spikes()
    {
        var analyzer = new ActivityAnalyzer { Options = new ActivityOptions { SustainSamples = 1 } };
        var events = analyzer.Analyze(Make.Snapshot(Now, Make.Process(7, 0, cpu: null)), Make.Metrics(Now, cpu: null, memoryPercent: null));
        Assert.Empty(events);
    }

    [Fact]
    public void Reset_establishes_a_new_baseline()
    {
        var analyzer = new ActivityAnalyzer();
        analyzer.Analyze(Make.Snapshot(Now, Make.Process(1, 0)), Make.Metrics(Now));
        analyzer.Reset();
        var events = analyzer.Analyze(Make.Snapshot(Now.AddHours(8), Make.Process(2, 0), Make.Process(3, 0)), Make.Metrics(Now.AddHours(8)));
        Assert.Empty(events);
    }
}
