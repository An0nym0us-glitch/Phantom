using Phantom.Core.Metrics;
using Phantom.Core.Models;
using Phantom.SystemMonitoring;

namespace Phantom.Tests;

public class MetricMathTests
{
    [Theory]
    [InlineData(100, 100, 0, 0)]     // kernel time is all idle
    [InlineData(50, 100, 100, 75)]   // 200 total, 50 idle
    [InlineData(0, 40, 60, 100)]
    public void Cpu_percent_treats_kernel_time_as_inclusive_of_idle(long idle, long kernel, long user, double expected)
        => Assert.Equal(expected, MetricMath.CpuPercentFromSystemTimes(idle, kernel, user)!.Value, 3);

    [Fact]
    public void Cpu_percent_is_unavailable_without_elapsed_time_or_with_negative_deltas()
    {
        Assert.Null(MetricMath.CpuPercentFromSystemTimes(0, 0, 0));
        Assert.Null(MetricMath.CpuPercentFromSystemTimes(-1, 10, 10));
    }

    [Fact]
    public void Process_cpu_is_a_share_of_total_capacity()
    {
        // One full core for one second on an 8-thread machine = 12.5%.
        Assert.Equal(12.5, MetricMath.ProcessCpuPercent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 8)!.Value, 3);
        Assert.Equal(100, MetricMath.ProcessCpuPercent(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1), 8)!.Value, 3);
    }

    [Fact]
    public void Process_cpu_is_unavailable_for_invalid_inputs()
    {
        Assert.Null(MetricMath.ProcessCpuPercent(TimeSpan.FromSeconds(1), TimeSpan.Zero, 8));
        Assert.Null(MetricMath.ProcessCpuPercent(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1), 8));
        Assert.Null(MetricMath.ProcessCpuPercent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0));
    }

    [Fact]
    public void Rate_handles_counter_resets()
    {
        Assert.Equal(500, MetricMath.Rate(1000, 2000, 2));
        Assert.Null(MetricMath.Rate(2000, 1000, 2));
        Assert.Null(MetricMath.Rate(0, 10, 0));
    }

    [Fact]
    public void Memory_figures_are_derived_and_null_when_inputs_are_missing()
    {
        var s = new SystemMetricsSample { TimestampUtc = DateTime.UtcNow, MemoryTotalBytes = 1000, MemoryAvailableBytes = 250 };
        Assert.Equal(750, s.MemoryUsedBytes);
        Assert.Equal(75, s.MemoryPercent!.Value, 3);

        var missing = new SystemMetricsSample { TimestampUtc = DateTime.UtcNow, MemoryTotalBytes = 1000 };
        Assert.Null(missing.MemoryUsedBytes);
        Assert.Null(missing.MemoryPercent);
    }

    [Fact]
    public void Average_ignores_missing_values_and_keeps_all_missing_as_null()
    {
        var t = DateTime.UtcNow;
        var avg = MetricMath.Average(
        [
            new SystemMetricsSample { TimestampUtc = t, CpuPercent = 10, DiskReadBytesPerSec = null },
            new SystemMetricsSample { TimestampUtc = t.AddSeconds(1), CpuPercent = null },
            new SystemMetricsSample { TimestampUtc = t.AddSeconds(2), CpuPercent = 30 },
        ])!;

        Assert.Equal(20, avg.CpuPercent!.Value, 3);
        Assert.Null(avg.DiskReadBytesPerSec);
        Assert.Null(avg.GpuPercent);
        Assert.Equal(t.AddSeconds(2), avg.TimestampUtc);
        Assert.Null(MetricMath.Average([]));
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(17179869184L, "16.0 GB")]
    [InlineData(214748364800L, "200 GB")]
    public void Bytes_are_formatted_with_binary_units(long bytes, string expected)
        => Assert.Equal(expected, Format.Bytes(bytes));

    [Fact]
    public void Missing_values_are_never_formatted_as_zero()
    {
        Assert.Equal(Format.Unavailable, Format.Bytes(null));
        Assert.Equal(Format.Unavailable, Format.Percent(null));
        Assert.Equal(Format.Unavailable, Format.BytesPerSec(null));
        Assert.Equal(Format.Unavailable, Format.Count(null));
        Assert.Equal("0%", Format.Percent(0));
        Assert.Contains("access denied", Format.Describe(Availability.AccessDenied));
    }

    [Fact]
    public void Ring_buffer_keeps_the_most_recent_items_in_order()
    {
        var ring = new RingBuffer<int>(3);
        for (int i = 1; i <= 5; i++)
        {
            ring.Add(i);
        }

        Assert.Equal(3, ring.Count);
        Assert.Equal([3, 4, 5], ring.ToArray());
        Assert.Equal(3, ring[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ring[3]);
        ring.Clear();
        Assert.Empty(ring.ToArray());
    }

    [Fact]
    public void Gpu_utilisation_sums_processes_per_engine_and_takes_the_busiest_engine()
    {
        var value = GpuCounterMath.AggregateUtilization(
        [
            ("pid_10_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 20),
            ("pid_11_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 15),
            ("pid_10_luid_0x0_0x1_phys_0_eng_1_engtype_VideoDecode", 30),
        ]);
        Assert.Equal(35, value!.Value, 3);
    }

    [Fact]
    public void Gpu_utilisation_is_unavailable_without_instances_and_capped_at_100()
    {
        Assert.Null(GpuCounterMath.AggregateUtilization([]));
        Assert.Equal(100, GpuCounterMath.AggregateUtilization(
        [
            ("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 80),
            ("pid_2_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 70),
        ]));
    }
}
