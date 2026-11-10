using System.Globalization;
using Phantom.Core.Models;

namespace Phantom.Core.Metrics;

/// <summary>Pure metric calculations, kept free of OS calls so they can be unit tested.</summary>
public static class MetricMath
{
    /// <summary>
    /// Computes system CPU utilisation from deltas of the Windows system times.
    /// Windows reports kernel time inclusive of idle time, so busy = kernel + user - idle.
    /// Returns null when no time elapsed or the deltas are inconsistent.
    /// </summary>
    public static double? CpuPercentFromSystemTimes(long idleDelta, long kernelDelta, long userDelta)
    {
        if (idleDelta < 0 || kernelDelta < 0 || userDelta < 0)
        {
            return null;
        }

        long total = kernelDelta + userDelta;
        if (total <= 0)
        {
            return null;
        }

        return Math.Clamp(100.0 * (total - idleDelta) / total, 0.0, 100.0);
    }

    /// <summary>
    /// Share of total machine CPU capacity used by one process (0-100), the same convention as Task Manager.
    /// </summary>
    public static double? ProcessCpuPercent(TimeSpan cpuTimeDelta, TimeSpan elapsed, int logicalProcessors)
    {
        if (elapsed <= TimeSpan.Zero || logicalProcessors <= 0 || cpuTimeDelta < TimeSpan.Zero)
        {
            return null;
        }

        double percent = 100.0 * cpuTimeDelta.Ticks / (elapsed.Ticks * (double)logicalProcessors);
        return Math.Clamp(percent, 0.0, 100.0);
    }

    /// <summary>Per-second rate of a monotonically increasing counter; null if the counter reset or no time elapsed.</summary>
    public static double? Rate(long previous, long current, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0 || current < previous)
        {
            return null;
        }

        return (current - previous) / elapsedSeconds;
    }

    private static double? Avg(IReadOnlyList<SystemMetricsSample> s, Func<SystemMetricsSample, double?> f)
    {
        double sum = 0;
        int n = 0;
        foreach (var x in s)
        {
            if (f(x) is { } v && !double.IsNaN(v))
            {
                sum += v;
                n++;
            }
        }

        return n == 0 ? null : sum / n;
    }

    private static long? AvgLong(IReadOnlyList<SystemMetricsSample> s, Func<SystemMetricsSample, long?> f)
        => Avg(s, x => f(x)) is { } v ? (long)Math.Round(v) : null;

    /// <summary>
    /// Averages a window of samples into one. Metrics missing from every sample stay null;
    /// metrics missing from some samples are averaged over the samples that have them.
    /// </summary>
    public static SystemMetricsSample? Average(IReadOnlyList<SystemMetricsSample> samples)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        var last = samples[^1];
        return new SystemMetricsSample
        {
            TimestampUtc = last.TimestampUtc,
            CpuPercent = Avg(samples, x => x.CpuPercent),
            MemoryTotalBytes = last.MemoryTotalBytes,
            MemoryAvailableBytes = AvgLong(samples, x => x.MemoryAvailableBytes),
            GpuPercent = Avg(samples, x => x.GpuPercent),
            GpuDedicatedUsedBytes = AvgLong(samples, x => x.GpuDedicatedUsedBytes),
            GpuSharedUsedBytes = AvgLong(samples, x => x.GpuSharedUsedBytes),
            DiskReadBytesPerSec = Avg(samples, x => x.DiskReadBytesPerSec),
            DiskWriteBytesPerSec = Avg(samples, x => x.DiskWriteBytesPerSec),
            DiskActivePercent = Avg(samples, x => x.DiskActivePercent),
            NetworkUploadBytesPerSec = Avg(samples, x => x.NetworkUploadBytesPerSec),
            NetworkDownloadBytesPerSec = Avg(samples, x => x.NetworkDownloadBytesPerSec),
            Uptime = last.Uptime,
            ProcessCount = Avg(samples, x => x.ProcessCount) is { } pc ? (int)Math.Round(pc) : null,
        };
    }
}

/// <summary>Human-readable formatting shared by the UI and exports.</summary>
public static class Format
{
    public const string Unavailable = "—";

    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long? bytes)
    {
        if (bytes is not { } b)
        {
            return Unavailable;
        }

        double v = Math.Abs((double)b);
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        string num = u == 0 ? v.ToString("0", CultureInfo.InvariantCulture)
            : v >= 100 ? v.ToString("0", CultureInfo.InvariantCulture)
            : v.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{(b < 0 ? "-" : "")}{num} {Units[u]}";
    }

    public static string BytesPerSec(double? rate)
        => rate is { } r ? Bytes((long)Math.Round(r)) + "/s" : Unavailable;

    public static string Percent(double? value, int decimals = 0)
        => value is { } v ? v.ToString(decimals == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%" : Unavailable;

    public static string Count(int? value)
        => value is { } v ? v.ToString("N0", CultureInfo.InvariantCulture) : Unavailable;

    public static string Duration(TimeSpan? value)
    {
        if (value is not { } t)
        {
            return Unavailable;
        }

        if (t.TotalDays >= 1)
        {
            return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        }

        return t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";
    }

    public static string Describe(Availability availability) => availability switch
    {
        Availability.Available => Unavailable,
        Availability.AccessDenied => "Unavailable (access denied)",
        Availability.ProcessExited => "Unavailable (process exited)",
        Availability.NotApplicable => "Not applicable",
        Availability.Unsupported => "Unsupported on this system",
        _ => "Unavailable",
    };
}

/// <summary>Fixed-capacity FIFO buffer for chart history. Not thread-safe.</summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private int _start;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count { get; private set; }

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count);
            return _items[(_start + index) % _items.Length];
        }
    }

    public void Add(T item)
    {
        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = item;
            Count++;
        }
        else
        {
            _items[_start] = item;
            _start = (_start + 1) % _items.Length;
        }
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        Count = 0;
    }

    public T[] ToArray()
    {
        var result = new T[Count];
        for (int i = 0; i < Count; i++)
        {
            result[i] = this[i];
        }

        return result;
    }
}
