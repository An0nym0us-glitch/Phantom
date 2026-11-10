using System.Runtime.Versioning;
using Phantom.Core.Abstractions;
using Phantom.Core.Models;
using Phantom.Core.Processes;
using Phantom.ProcessMonitoring;
using Phantom.SystemMonitoring;

namespace Phantom.Tests;

/// <summary>Exercise the real Windows collectors. Skipped on other platforms; run by the Windows CI job.</summary>
[SupportedOSPlatform("windows")]
public class WindowsIntegrationTests
{
    [WindowsFact]
    public void Process_table_contains_this_process_with_plausible_values()
    {
        using var source = new WindowsProcessDataSource();
        var processes = source.EnumerateProcesses();
        using var self = System.Diagnostics.Process.GetCurrentProcess();

        Assert.True(processes.Count > 10);
        Assert.Equal(processes.Count, processes.Select(p => p.Pid).Distinct().Count());
        Assert.Contains(processes, p => p.Pid == 0 && p.Name == "System Idle Process" && p.HandleCount is null);
        Assert.Contains(processes, p => p.Pid == 4 && p.Name == "System");

        var me = processes.Single(p => p.Pid == Environment.ProcessId);
        Assert.Contains(self.ProcessName, me.Name, StringComparison.OrdinalIgnoreCase);
        Assert.True(me.ThreadCount > 0);
        Assert.True(me.HandleCount > 0);
        Assert.True(me.WorkingSetBytes > 1_000_000);
        Assert.True(me.PrivateBytes > 1_000_000);
        Assert.True(me.TotalProcessorTime > TimeSpan.Zero);
        Assert.Equal(self.SessionId, me.SessionId);
        Assert.True(Math.Abs((me.StartTimeUtc!.Value - self.StartTime.ToUniversalTime()).TotalSeconds) < 2);
        Assert.Contains(processes, p => p.Pid == me.ParentPid);
    }

    [WindowsFact]
    public void Details_of_this_process_are_available_and_kernel_processes_are_not_applicable()
    {
        using var source = new WindowsProcessDataSource();
        var details = source.QueryDetails(Environment.ProcessId, null);

        Assert.Equal(Availability.Available, details.PathAvailability);
        Assert.Equal(Environment.ProcessPath, details.ExecutablePath, ignoreCase: true);
        Assert.Equal(ProcessOwnerKind.CurrentUser, details.Owner);
        Assert.NotEqual(ProcessArchitecture.Unknown, details.Architecture);

        var system = source.QueryDetails(4, 0);
        Assert.Equal(Availability.NotApplicable, system.PathAvailability);
        Assert.Equal(ProcessOwnerKind.System, system.Owner);
    }

    [WindowsFact]
    public void Querying_a_pid_that_does_not_exist_reports_unavailable_without_throwing()
    {
        using var source = new WindowsProcessDataSource();
        var details = source.QueryDetails(0x3FFFFFFC, null);
        Assert.Null(details.ExecutablePath);
        Assert.NotEqual(Availability.Available, details.PathAvailability);
    }

    [WindowsFact]
    public void Every_running_process_can_be_inspected_without_exceptions()
    {
        using var source = new WindowsProcessDataSource();
        foreach (var p in source.EnumerateProcesses())
        {
            var d = source.QueryDetails(p.Pid, p.SessionId);
            Assert.True(d.ExecutablePath is not null || d.PathAvailability != Availability.Available,
                $"PID {p.Pid} has no path but claims it is available.");
        }

        Assert.NotNull(source.GetPidsWithVisibleWindows());
    }

    [WindowsFact]
    public void Snapshot_and_hierarchy_work_end_to_end_and_measure_cpu()
    {
        using var source = new WindowsProcessDataSource();
        var builder = new ProcessSnapshotBuilder(source, new SystemClock(), Environment.ProcessorCount);
        builder.Build();

        // Burn some CPU so this process has a measurable share.
        var until = DateTime.UtcNow.AddMilliseconds(600);
        double x = 0;
        while (DateTime.UtcNow < until)
        {
            x += Math.Sqrt(x + 1);
        }

        var snapshot = builder.Build();
        var me = snapshot.FindByPid(Environment.ProcessId)!;
        Assert.NotNull(me.CpuPercent);
        Assert.InRange(me.CpuPercent!.Value, 0.01, 100);
        Assert.All(snapshot.Processes, p => Assert.True(p.CpuPercent is null or (>= 0 and <= 100)));

        var hierarchy = ProcessHierarchy.Build(snapshot.Processes);
        Assert.Equal(me.ParentPid, hierarchy.GetParent(me.Pid)!.Pid);
        Assert.NotEmpty(hierarchy.GetAncestors(me.Pid));
        Assert.Equal(snapshot.Count, hierarchy.Roots.Sum(r => 1 + hierarchy.GetDescendants(r.Pid).Count));
    }

    [WindowsFact]
    public void System_metrics_are_real_and_within_range()
    {
        using var source = new WindowsSystemMetricsSource();
        source.Sample();
        Thread.Sleep(1100);
        var s = source.Sample();

        Assert.NotNull(s.CpuPercent);
        Assert.InRange(s.CpuPercent!.Value, 0, 100);
        Assert.Equal(Environment.ProcessorCount, s.PerCoreCpuPercent!.Count);
        Assert.All(s.PerCoreCpuPercent, v => Assert.InRange(v, 0, 100));
        Assert.True(s.MemoryTotalBytes > 500_000_000);
        Assert.InRange(s.MemoryAvailableBytes!.Value, 1, s.MemoryTotalBytes!.Value);
        Assert.InRange(s.MemoryPercent!.Value, 1, 100);
        Assert.True(s.Uptime > TimeSpan.Zero);
        Assert.True(s.NetworkUploadBytesPerSec is null or >= 0);
        Assert.True(s.NetworkDownloadBytesPerSec is null or >= 0);

        var status = source.GetSensorStatus();
        var disk = status.Single(x => x.Sensor == "Disk");
        if (disk.IsAvailable)
        {
            Assert.NotNull(s.DiskReadBytesPerSec);
            Assert.True(s.DiskWriteBytesPerSec >= 0);
            Assert.InRange(s.DiskActivePercent!.Value, 0, 100);
        }
        else
        {
            Assert.Null(s.DiskReadBytesPerSec);
            Assert.False(string.IsNullOrEmpty(disk.Reason));
        }

        // GPU telemetry is optional hardware: either a valid value or explicitly unavailable.
        Assert.True(s.GpuPercent is null or (>= 0 and <= 100));
        Console.WriteLine($"CPU {s.CpuPercent:0.0}% | Mem {s.MemoryPercent:0.0}% | GPU {s.GpuPercent?.ToString("0.0") ?? "n/a"} | " +
                          $"Disk R {s.DiskReadBytesPerSec?.ToString("0") ?? "n/a"} W {s.DiskWriteBytesPerSec?.ToString("0") ?? "n/a"} | " +
                          string.Join("; ", status.Select(x => $"{x.Sensor}={(x.IsAvailable ? "ok" : x.Reason)}")));
    }

    [WindowsFact]
    public void Hardware_information_is_populated()
    {
        using var source = new WindowsSystemMetricsSource();
        var hw = source.GetHardwareInfo();

        Assert.False(string.IsNullOrWhiteSpace(hw.CpuName));
        Assert.Equal(Environment.ProcessorCount, hw.LogicalProcessors);
        Assert.True(hw.TotalMemoryBytes > 0);
        Assert.NotEmpty(hw.Drives);
        Assert.Contains("Windows", hw.OsDescription);
    }
}
