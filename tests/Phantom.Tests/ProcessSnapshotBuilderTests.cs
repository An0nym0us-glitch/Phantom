using Phantom.Core.Models;
using Phantom.Core.Processes;

namespace Phantom.Tests;

public class ProcessSnapshotBuilderTests
{
    [Fact]
    public void Cpu_is_unavailable_on_the_first_sample_and_computed_on_the_second()
    {
        var clock = new FakeClock();
        var source = new FakeProcessSource { Processes = [Make.Raw(10, 1, cpuSeconds: 5)] };
        var builder = new ProcessSnapshotBuilder(source, clock, logicalProcessors: 4);

        var first = builder.Build();
        Assert.Null(first.Processes[0].CpuPercent);

        clock.Advance(TimeSpan.FromSeconds(2));
        source.Processes = [Make.Raw(10, 1, cpuSeconds: 7)];
        var second = builder.Build();

        // 2 CPU-seconds over 2 seconds on 4 processors = 25%.
        Assert.Equal(25, second.Processes[0].CpuPercent!.Value, 3);
    }

    [Fact]
    public void A_reused_pid_does_not_inherit_the_previous_process_cpu_baseline()
    {
        var clock = new FakeClock();
        var source = new FakeProcessSource { Processes = [Make.Raw(10, 1, cpuSeconds: 500, startSeconds: 0)] };
        var builder = new ProcessSnapshotBuilder(source, clock, 4);
        builder.Build();

        clock.Advance(TimeSpan.FromSeconds(1));
        source.Processes = [Make.Raw(10, 1, cpuSeconds: 0.1, startSeconds: 50)];
        Assert.Null(builder.Build().Processes[0].CpuPercent);
    }

    [Fact]
    public void Details_are_queried_once_per_process_lifetime()
    {
        var clock = new FakeClock();
        var source = new FakeProcessSource { Processes = [Make.Raw(10, 1), Make.Raw(11, 1)] };
        var builder = new ProcessSnapshotBuilder(source, clock, 4);

        builder.Build();
        builder.Build();
        builder.Build();
        Assert.Equal(2, source.DetailQueries);

        source.Processes.Add(Make.Raw(12, 1));
        builder.Build();
        Assert.Equal(3, source.DetailQueries);
    }

    [Fact]
    public void A_failing_detail_query_degrades_that_process_only()
    {
        var source = new FakeProcessSource
        {
            Processes = [Make.Raw(10, 1), Make.Raw(11, 1)],
            Details = pid => pid == 10 ? throw new InvalidOperationException("boom") : new ProcessDetails { ExecutablePath = "ok" },
        };
        var snapshot = new ProcessSnapshotBuilder(source, new FakeClock(), 4).Build();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(Availability.Error, snapshot.FindByPid(10)!.Details.PathAvailability);
        Assert.Null(snapshot.FindByPid(10)!.ExecutablePath);
        Assert.Equal("ok", snapshot.FindByPid(11)!.ExecutablePath);
    }

    [Fact]
    public void Access_denied_is_reported_as_unavailable_not_as_empty()
    {
        var source = new FakeProcessSource
        {
            Processes = [Make.Raw(10, 1)],
            Details = _ => ProcessDetails.Unavailable(Availability.AccessDenied),
        };
        var p = new ProcessSnapshotBuilder(source, new FakeClock(), 4).Build().Processes[0];

        Assert.Null(p.ExecutablePath);
        Assert.Equal(Availability.AccessDenied, p.Details.PathAvailability);
        Assert.Equal(ProcessArchitecture.Unknown, p.Architecture);
    }

    [Fact]
    public void Visible_windows_are_attached_and_duplicate_identities_are_dropped()
    {
        var source = new FakeProcessSource { Processes = [Make.Raw(10, 1), Make.Raw(10, 1), Make.Raw(11, 1)] };
        source.WindowPids.Add(11);
        var snapshot = new ProcessSnapshotBuilder(source, new FakeClock(), 4).Build();

        Assert.Equal(2, snapshot.Count);
        Assert.False(snapshot.FindByPid(10)!.HasVisibleWindow);
        Assert.True(snapshot.FindByPid(11)!.HasVisibleWindow);
    }

    [Fact]
    public void Resetting_baselines_makes_cpu_unavailable_for_one_sample()
    {
        var clock = new FakeClock();
        var source = new FakeProcessSource { Processes = [Make.Raw(10, 1, cpuSeconds: 1)] };
        var builder = new ProcessSnapshotBuilder(source, clock, 4);
        builder.Build();
        builder.ResetBaselines();
        clock.Advance(TimeSpan.FromHours(8));
        source.Processes = [Make.Raw(10, 1, cpuSeconds: 100)];
        Assert.Null(builder.Build().Processes[0].CpuPercent);
    }
}
