using Phantom.Core.Abstractions;
using Phantom.Core.Models;

namespace Phantom.Tests;

/// <summary>Runs only on Windows; reported as skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows.";
        }
    }
}

internal sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    public void Advance(TimeSpan by) => UtcNow += by;
}

internal sealed class FakeProcessSource : IProcessDataSource
{
    public List<RawProcessSample> Processes { get; set; } = [];
    public HashSet<int> WindowPids { get; } = [];
    public Func<int, ProcessDetails>? Details { get; set; }
    public int DetailQueries { get; private set; }
    public Exception? EnumerateFailure { get; set; }

    public IReadOnlyList<RawProcessSample> EnumerateProcesses()
        => EnumerateFailure is null ? Processes.ToList() : throw EnumerateFailure;

    public ProcessDetails QueryDetails(int pid, int? sessionId)
    {
        DetailQueries++;
        return Details?.Invoke(pid) ?? new ProcessDetails { ExecutablePath = $@"C:\apps\{pid}.exe", Owner = ProcessOwnerKind.CurrentUser };
    }

    public IReadOnlySet<int> GetPidsWithVisibleWindows() => WindowPids;
}

internal sealed class FakeMetricsSource : ISystemMetricsSource
{
    public Func<SystemMetricsSample> Next { get; set; } = () => new SystemMetricsSample { TimestampUtc = DateTime.UtcNow, CpuPercent = 10 };
    public SystemMetricsSample Sample() => Next();
    public HardwareInfo GetHardwareInfo() => new();
    public IReadOnlyList<SensorStatus> GetSensorStatus() => [];
    public void Dispose() { }
}

internal static class Make
{
    public static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    public static ProcessInfo Process(int pid, int parent, string? name = null, int startSeconds = 0,
        double? cpu = 0, long memory = 10_000_000, ProcessOwnerKind owner = ProcessOwnerKind.CurrentUser,
        bool window = false, bool noStart = false)
    {
        DateTime? start = noStart ? null : T0.AddSeconds(startSeconds);
        return new ProcessInfo
        {
            Key = new ProcessKey(pid, start?.Ticks ?? 0),
            Pid = pid,
            ParentPid = parent,
            Name = name ?? $"proc{pid}.exe",
            StartTimeUtc = start,
            CpuPercent = cpu,
            WorkingSetBytes = memory,
            HasVisibleWindow = window,
            Details = new ProcessDetails { Owner = owner },
        };
    }

    public static RawProcessSample Raw(int pid, int parent, double cpuSeconds = 0, int startSeconds = 0, string? name = null)
        => new()
        {
            Pid = pid,
            ParentPid = parent,
            Name = name ?? $"proc{pid}.exe",
            StartTimeUtc = T0.AddSeconds(startSeconds),
            TotalProcessorTime = TimeSpan.FromSeconds(cpuSeconds),
            WorkingSetBytes = 1000,
            ThreadCount = 1,
            HandleCount = 5,
            SessionId = 1,
        };

    public static ProcessSnapshot Snapshot(DateTime at, params ProcessInfo[] processes) => new(at, processes);

    public static SystemMetricsSample Metrics(DateTime at, double? cpu = 5, double? memoryPercent = 40) => new()
    {
        TimestampUtc = at,
        CpuPercent = cpu,
        MemoryTotalBytes = memoryPercent is null ? null : 1000,
        MemoryAvailableBytes = memoryPercent is { } m ? (long)(1000 - (m * 10)) : null,
    };
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "phantom-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
