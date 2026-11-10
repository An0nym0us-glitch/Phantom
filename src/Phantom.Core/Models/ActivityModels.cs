namespace Phantom.Core.Models;

public enum ActivityCategory
{
    ProcessStarted = 1,
    ProcessExited = 2,
    ApplicationLaunched = 3,
    ResourceSpike = 4,
    MonitoringInterruption = 5,
    Monitoring = 6,
}

/// <summary>How Phantom learned about an event.</summary>
public enum EventProvenance
{
    /// <summary>Reported directly by the operating system (e.g. a kernel creation timestamp or an ETW event).</summary>
    Observed = 1,
    /// <summary>Deduced by comparing successive samples; timing is accurate only to the sampling interval.</summary>
    Inferred = 2,
}

public sealed record ActivityEvent
{
    /// <summary>Database identity; 0 for events that have not been persisted.</summary>
    public long Id { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required ActivityCategory Category { get; init; }
    public required EventProvenance Provenance { get; init; }
    public int? Pid { get; init; }
    public string? ProcessName { get; init; }
    public required string Description { get; init; }
}

public sealed record ActivityQuery
{
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public IReadOnlyCollection<ActivityCategory>? Categories { get; init; }
    /// <summary>Case-insensitive substring matched against process name and description.</summary>
    public string? SearchText { get; init; }
    public int Limit { get; init; } = 500;
    public int Offset { get; init; }
}

public sealed record MonitoringSession
{
    public long Id { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime? EndedUtc { get; init; }
    public string AppVersion { get; init; } = "";
}

public sealed record HistoryStats
{
    public long MetricSampleCount { get; init; }
    public long EventCount { get; init; }
    public long SessionCount { get; init; }
    public long DatabaseSizeBytes { get; init; }
    public DateTime? OldestRecordUtc { get; init; }
}
