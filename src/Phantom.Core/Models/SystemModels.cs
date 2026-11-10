namespace Phantom.Core.Models;

/// <summary>
/// One sample of system-wide telemetry. Every metric is nullable: null means the value
/// could not be measured (unsupported counter, missing hardware, first sample of a rate),
/// never "zero".
/// </summary>
public sealed record SystemMetricsSample
{
    public required DateTime TimestampUtc { get; init; }

    public double? CpuPercent { get; init; }
    public IReadOnlyList<double>? PerCoreCpuPercent { get; init; }

    public long? MemoryTotalBytes { get; init; }
    public long? MemoryAvailableBytes { get; init; }
    public long? MemoryUsedBytes =>
        MemoryTotalBytes is { } t && MemoryAvailableBytes is { } a ? Math.Max(0, t - a) : null;
    public double? MemoryPercent =>
        MemoryTotalBytes is > 0 and var t && MemoryUsedBytes is { } u ? 100.0 * u / t : null;

    public double? GpuPercent { get; init; }
    public long? GpuDedicatedUsedBytes { get; init; }
    public long? GpuSharedUsedBytes { get; init; }

    public double? DiskReadBytesPerSec { get; init; }
    public double? DiskWriteBytesPerSec { get; init; }
    public double? DiskActivePercent { get; init; }

    public double? NetworkUploadBytesPerSec { get; init; }
    public double? NetworkDownloadBytesPerSec { get; init; }

    public TimeSpan? Uptime { get; init; }
    public int? ProcessCount { get; init; }
}

public sealed record GpuAdapterInfo(string Name, long? DedicatedMemoryBytes);

public sealed record NetworkAdapterInfo(string Name, string Description, string Type, long? SpeedBitsPerSec, bool IsUp);

public sealed record DriveSummary(string Name, string? Label, string? Format, long TotalBytes, long FreeBytes);

public sealed record HardwareInfo
{
    public string? CpuName { get; init; }
    public int LogicalProcessors { get; init; }
    public long? TotalMemoryBytes { get; init; }
    public string? OsDescription { get; init; }
    public string? OsArchitecture { get; init; }
    public IReadOnlyList<GpuAdapterInfo> Gpus { get; init; } = [];
    public IReadOnlyList<NetworkAdapterInfo> NetworkAdapters { get; init; } = [];
    public IReadOnlyList<DriveSummary> Drives { get; init; } = [];
}

/// <summary>Describes whether a telemetry family is usable on this machine and, if not, why.</summary>
public sealed record SensorStatus(string Sensor, bool IsAvailable, string? Reason);
