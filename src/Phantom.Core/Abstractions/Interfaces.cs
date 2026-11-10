using Phantom.Core.Models;

namespace Phantom.Core.Abstractions;

/// <summary>
/// Operating-system seam for process discovery. Implementations must be read-only,
/// must never elevate, and must not throw for individual inaccessible processes.
/// </summary>
public interface IProcessDataSource
{
    /// <summary>Enumerates every process visible to the current user in one pass.</summary>
    IReadOnlyList<RawProcessSample> EnumerateProcesses();

    /// <summary>Queries slow-changing details. Failures are reported through <see cref="Availability"/>.</summary>
    ProcessDetails QueryDetails(int pid, int? sessionId);

    /// <summary>PIDs that currently own at least one visible, titled top-level window.</summary>
    IReadOnlySet<int> GetPidsWithVisibleWindows();
}

/// <summary>Operating-system seam for system-wide telemetry.</summary>
public interface ISystemMetricsSource : IDisposable
{
    SystemMetricsSample Sample();
    HardwareInfo GetHardwareInfo();
    /// <summary>Availability of each telemetry family, for transparent reporting in the UI.</summary>
    IReadOnlyList<SensorStatus> GetSensorStatus();
}

/// <summary>Turns successive samples into activity events.</summary>
public interface IActivityAnalyzer
{
    IReadOnlyList<ActivityEvent> Analyze(ProcessSnapshot snapshot, SystemMetricsSample metrics);
    void Reset();
}

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>
/// Persistent history. Every member must be safe to call when the store is unavailable:
/// writes become no-ops and reads return empty results.
/// </summary>
public interface IHistoryStore : IDisposable
{
    bool IsAvailable { get; }
    /// <summary>Human-readable explanation when <see cref="IsAvailable"/> is false, or a recovery note.</summary>
    string? StatusMessage { get; }
    string? DatabasePath { get; }

    long BeginSession(DateTime startedUtc, string appVersion);
    void EndSession(long sessionId, DateTime endedUtc);

    void AppendMetrics(long sessionId, IReadOnlyList<SystemMetricsSample> samples);
    void AppendEvents(long sessionId, IReadOnlyList<ActivityEvent> events);

    IReadOnlyList<ActivityEvent> QueryEvents(ActivityQuery query);
    long CountEvents(ActivityQuery query);
    /// <summary>Returns samples in the range, averaged into at most <paramref name="maxPoints"/> buckets.</summary>
    IReadOnlyList<SystemMetricsSample> QueryMetrics(DateTime fromUtc, DateTime toUtc, int maxPoints);
    IReadOnlyList<MonitoringSession> GetSessions(int limit);
    HistoryStats GetStats();

    /// <summary>Deletes records older than the cutoff. Returns the number of rows removed.</summary>
    long ApplyRetention(DateTime cutoffUtc);
    void ClearAll();
    void Compact();
}

public sealed record MonitoringOptions
{
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Metric samples are averaged over this window before being persisted.</summary>
    public TimeSpan PersistInterval { get; init; } = TimeSpan.FromSeconds(10);
    public bool PersistHistory { get; init; } = true;
}
