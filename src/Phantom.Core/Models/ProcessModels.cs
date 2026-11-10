namespace Phantom.Core.Models;

/// <summary>
/// Stable identity of a process. A PID alone is not an identity because Windows
/// recycles PIDs; the creation time disambiguates successive owners of the same PID.
/// </summary>
public readonly record struct ProcessKey(int Pid, long CreateTimeTicks)
{
    public override string ToString() => $"{Pid}@{CreateTimeTicks}";
}

public enum ProcessArchitecture
{
    /// <summary>The architecture could not be determined (not the same as "native").</summary>
    Unknown = 0,
    X86,
    X64,
    Arm,
    Arm64,
}

/// <summary>Who a process belongs to, from the point of view of the current user.</summary>
public enum ProcessOwnerKind
{
    /// <summary>Ownership could not be determined and no inference was possible.</summary>
    Unknown = 0,
    /// <summary>The process runs under the same account as Phantom.</summary>
    CurrentUser,
    /// <summary>The process runs under a system/service account or another user.</summary>
    System,
}

/// <summary>Why a piece of per-process information is missing.</summary>
public enum Availability
{
    Available = 0,
    /// <summary>Windows refused access. Typical for protected and other-user processes when not elevated.</summary>
    AccessDenied,
    /// <summary>The process exited before it could be inspected.</summary>
    ProcessExited,
    /// <summary>The concept does not apply (for example the Idle pseudo-process has no executable).</summary>
    NotApplicable,
    /// <summary>The platform or API does not support the query.</summary>
    Unsupported,
    /// <summary>An unexpected error occurred.</summary>
    Error,
}

/// <summary>
/// One row of volatile per-process counters as reported by the operating system in a single enumeration.
/// </summary>
public sealed record RawProcessSample
{
    public required int Pid { get; init; }
    public required int ParentPid { get; init; }
    public required string Name { get; init; }
    /// <summary>UTC creation time; null when the OS does not report one (e.g. the Idle process).</summary>
    public DateTime? StartTimeUtc { get; init; }
    /// <summary>Kernel + user time consumed since the process started.</summary>
    public TimeSpan TotalProcessorTime { get; init; }
    public long WorkingSetBytes { get; init; }
    public long? PrivateBytes { get; init; }
    public int ThreadCount { get; init; }
    public int? HandleCount { get; init; }
    public int? SessionId { get; init; }

    public ProcessKey Key => new(Pid, StartTimeUtc?.Ticks ?? 0);
}

/// <summary>Slow-changing details that require opening the process. Queried once and cached.</summary>
public sealed record ProcessDetails
{
    public string? ExecutablePath { get; init; }
    public Availability PathAvailability { get; init; } = Availability.Available;
    public ProcessArchitecture Architecture { get; init; } = ProcessArchitecture.Unknown;
    public Availability ArchitectureAvailability { get; init; } = Availability.Available;
    public ProcessOwnerKind Owner { get; init; } = ProcessOwnerKind.Unknown;

    public static ProcessDetails Unavailable(Availability reason) => new()
    {
        PathAvailability = reason,
        ArchitectureAvailability = reason,
    };
}

/// <summary>A fully assembled view of one process at one point in time.</summary>
public sealed record ProcessInfo
{
    public required ProcessKey Key { get; init; }
    public required int Pid { get; init; }
    public required int ParentPid { get; init; }
    public required string Name { get; init; }
    public DateTime? StartTimeUtc { get; init; }
    /// <summary>Share of total system CPU capacity (0-100). Null until two samples exist.</summary>
    public double? CpuPercent { get; init; }
    public TimeSpan TotalProcessorTime { get; init; }
    public long WorkingSetBytes { get; init; }
    public long? PrivateBytes { get; init; }
    public int ThreadCount { get; init; }
    public int? HandleCount { get; init; }
    public int? SessionId { get; init; }
    public bool HasVisibleWindow { get; init; }
    public ProcessDetails Details { get; init; } = new();

    public string? ExecutablePath => Details.ExecutablePath;
    public ProcessOwnerKind Owner => Details.Owner;
    public ProcessArchitecture Architecture => Details.Architecture;
}

public sealed class ProcessSnapshot
{
    public static readonly ProcessSnapshot Empty = new(DateTime.MinValue, []);

    private readonly Dictionary<int, ProcessInfo> _byPid;

    public ProcessSnapshot(DateTime timestampUtc, IReadOnlyList<ProcessInfo> processes)
    {
        TimestampUtc = timestampUtc;
        Processes = processes;
        _byPid = new Dictionary<int, ProcessInfo>(processes.Count);
        foreach (var p in processes)
        {
            _byPid[p.Pid] = p;
        }
    }

    public DateTime TimestampUtc { get; }
    public IReadOnlyList<ProcessInfo> Processes { get; }
    public int Count => Processes.Count;

    public ProcessInfo? FindByPid(int pid) => _byPid.GetValueOrDefault(pid);
}
