using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Phantom.Core.Abstractions;
using Phantom.Core.Metrics;
using Phantom.Core.Models;

namespace Phantom.SystemMonitoring;

/// <summary>
/// System-wide telemetry for Windows, read-only and unprivileged.
/// CPU comes from kernel processor times, memory from <c>GlobalMemoryStatusEx</c>, network from
/// interface byte counters, and disk and GPU from Windows performance counters via PDH.
/// Any family that is missing on a machine is reported as unavailable rather than as zero.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemMetricsSource : ISystemMetricsSource
{
    private const int SystemProcessorPerformanceInformation = 8;
    private const int ProcessorInfoSize = 48;
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly int _processorCount = Environment.ProcessorCount;
    private readonly Dictionary<string, SensorStatus> _status = new(StringComparer.Ordinal);

    private PdhQuery? _pdh;
    private PdhCounter? _diskRead, _diskWrite, _diskIdle, _gpuEngine, _gpuDedicated, _gpuShared;

    private long[]? _previousIdle, _previousKernel, _previousUser;
    private long _previousNetSent, _previousNetReceived;
    private long _previousNetTimestamp;
    private bool _hasNetBaseline;
    private bool _disposed;
    private HardwareInfo? _hardware;

    public WindowsSystemMetricsSource(ILogger<WindowsSystemMetricsSource>? logger = null)
    {
        _logger = logger ?? NullLogger<WindowsSystemMetricsSource>.Instance;
        SetStatus("CPU", true, null);
        SetStatus("Memory", true, null);
        SetStatus("Network", true, null);
        InitializePdh();
    }

    private void SetStatus(string sensor, bool available, string? reason)
        => _status[sensor] = new SensorStatus(sensor, available, reason);

    private void InitializePdh()
    {
        try
        {
            _pdh = new PdhQuery();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning("Performance counters are unavailable ({Error})", ex.Message);
            const string reason = "Windows performance counters could not be opened.";
            SetStatus("Disk", false, reason);
            SetStatus("GPU", false, reason);
            return;
        }

        _diskRead = _pdh.TryAdd(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec", out string? diskFailure);
        _diskWrite = _pdh.TryAdd(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec", out _);
        _diskIdle = _pdh.TryAdd(@"\PhysicalDisk(_Total)\% Idle Time", out _);
        SetStatus("Disk", _diskRead is not null && _diskWrite is not null,
            _diskRead is null ? $"Disk counters are unavailable: {diskFailure}." : null);

        _gpuEngine = _pdh.TryAdd(@"\GPU Engine(*)\Utilization Percentage", out string? gpuFailure);
        _gpuDedicated = _pdh.TryAdd(@"\GPU Adapter Memory(*)\Dedicated Usage", out _);
        _gpuShared = _pdh.TryAdd(@"\GPU Adapter Memory(*)\Shared Usage", out _);
        SetStatus("GPU", _gpuEngine is not null, _gpuEngine is null
            ? $"GPU counters are unavailable: {gpuFailure}. They require Windows 10 1709 or later and a WDDM 2.x graphics driver."
            : null);

        // Rate counters need a baseline; take it now so the first visible sample is real.
        _pdh.Collect();
    }

    public IReadOnlyList<SensorStatus> GetSensorStatus()
    {
        lock (_gate)
        {
            return [.. _status.Values];
        }
    }

    public SystemMetricsSample Sample()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var (cpu, perCore) = Guard("CPU", SampleCpu, (null, null));
            var memory = Guard("Memory", SampleMemory, (null, null));
            var (up, down) = Guard("Network", SampleNetwork, (null, null));

            double? diskRead = null, diskWrite = null, diskActive = null, gpu = null;
            long? gpuDedicated = null, gpuShared = null;
            if (_pdh is not null && _pdh.Collect())
            {
                diskRead = Single(_diskRead);
                diskWrite = Single(_diskWrite);
                diskActive = Single(_diskIdle) is { } idle ? Math.Clamp(100 - idle, 0, 100) : null;

                if (_gpuEngine is not null && _pdh.Read(_gpuEngine) is { } engines)
                {
                    gpu = GpuCounterMath.AggregateUtilization(engines);
                }

                gpuDedicated = SumBytes(_gpuDedicated);
                gpuShared = SumBytes(_gpuShared);
            }

            return new SystemMetricsSample
            {
                TimestampUtc = DateTime.UtcNow,
                CpuPercent = cpu,
                PerCoreCpuPercent = perCore,
                MemoryTotalBytes = memory.Item1,
                MemoryAvailableBytes = memory.Item2,
                GpuPercent = gpu,
                GpuDedicatedUsedBytes = gpuDedicated,
                GpuSharedUsedBytes = gpuShared,
                DiskReadBytesPerSec = diskRead,
                DiskWriteBytesPerSec = diskWrite,
                DiskActivePercent = diskActive,
                NetworkUploadBytesPerSec = up,
                NetworkDownloadBytesPerSec = down,
                Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            };
        }
    }

    /// <summary>Isolates one sensor family: its failure degrades that family only.</summary>
    private T Guard<T>(string sensor, Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            if (_status.TryGetValue(sensor, out var s) && s.IsAvailable)
            {
                _logger.LogWarning(ex, "{Sensor} sampling failed", sensor);
                SetStatus(sensor, false, $"{sensor} sampling failed ({ex.GetType().Name}).");
            }

            return fallback;
        }
    }

    private double? Single(PdhCounter? counter)
        => counter is not null && _pdh!.Read(counter) is { Count: > 0 } values ? Math.Max(0, values[0].Value) : null;

    private long? SumBytes(PdhCounter? counter)
        => counter is not null && _pdh!.Read(counter) is { Count: > 0 } values
            ? (long)values.Sum(v => Math.Max(0, v.Value))
            : null;

    private (double?, IReadOnlyList<double>?) SampleCpu()
    {
        int size = ProcessorInfoSize * _processorCount;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint status = NtQuerySystemInformation(SystemProcessorPerformanceInformation, buffer, size, out int returned);
            if (status != 0)
            {
                throw new InvalidOperationException($"Processor time query failed (0x{status:X8}).");
            }

            int count = Math.Min(_processorCount, returned / ProcessorInfoSize);
            var idle = new long[count];
            var kernel = new long[count];
            var user = new long[count];
            for (int i = 0; i < count; i++)
            {
                IntPtr entry = buffer + (i * ProcessorInfoSize);
                idle[i] = Marshal.ReadInt64(entry, 0);
                kernel[i] = Marshal.ReadInt64(entry, 8);
                user[i] = Marshal.ReadInt64(entry, 16);
            }

            double? total = null;
            double[]? perCore = null;
            if (_previousIdle is not null && _previousIdle.Length == count)
            {
                perCore = new double[count];
                long idleSum = 0, kernelSum = 0, userSum = 0;
                for (int i = 0; i < count; i++)
                {
                    long di = idle[i] - _previousIdle[i];
                    long dk = kernel[i] - _previousKernel![i];
                    long du = user[i] - _previousUser![i];
                    perCore[i] = MetricMath.CpuPercentFromSystemTimes(di, dk, du) ?? 0;
                    idleSum += di;
                    kernelSum += dk;
                    userSum += du;
                }

                total = MetricMath.CpuPercentFromSystemTimes(idleSum, kernelSum, userSum);
                if (total is null)
                {
                    perCore = null;
                }
            }

            _previousIdle = idle;
            _previousKernel = kernel;
            _previousUser = user;
            return (total, perCore);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (long?, long?) SampleMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException("GlobalMemoryStatusEx failed.");
        }

        return ((long)status.TotalPhys, (long)status.AvailPhys);
    }

    private (double?, double?) SampleNetwork()
    {
        long sent = 0, received = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                var stats = nic.GetIPStatistics();
                sent += stats.BytesSent;
                received += stats.BytesReceived;
            }
            catch (NetworkInformationException)
            {
                // An adapter can disappear mid-enumeration.
            }
        }

        long now = Stopwatch.GetTimestamp();
        double? up = null, down = null;
        if (_hasNetBaseline)
        {
            double seconds = Stopwatch.GetElapsedTime(_previousNetTimestamp, now).TotalSeconds;
            // Counters drop when an adapter goes away; Rate() reports that sample as unavailable.
            up = MetricMath.Rate(_previousNetSent, sent, seconds);
            down = MetricMath.Rate(_previousNetReceived, received, seconds);
        }

        _previousNetSent = sent;
        _previousNetReceived = received;
        _previousNetTimestamp = now;
        _hasNetBaseline = true;
        return (up, down);
    }

    public HardwareInfo GetHardwareInfo()
    {
        lock (_gate)
        {
            return _hardware ??= ReadHardwareInfo();
        }
    }

    private HardwareInfo ReadHardwareInfo()
    {
        long? totalMemory = null;
        try
        {
            totalMemory = SampleMemory().Item1;
        }
        catch (InvalidOperationException)
        {
        }

        return new HardwareInfo
        {
            CpuName = Try(() => (Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString", null) as string)?.Trim()),
            LogicalProcessors = _processorCount,
            TotalMemoryBytes = totalMemory,
            OsDescription = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            Gpus = Try(ReadGpus) ?? [],
            NetworkAdapters = Try(ReadNetworkAdapters) ?? [],
            Drives = Try(ReadDrives) ?? [],
        };
    }

    private T? Try<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hardware information query failed");
            return null;
        }
    }

    private static List<GpuAdapterInfo> ReadGpus()
    {
        var result = new List<GpuAdapterInfo>();
        using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
        if (classKey is null)
        {
            return result;
        }

        foreach (string name in classKey.GetSubKeyNames())
        {
            if (name.Length != 4 || !name.All(char.IsAsciiDigit))
            {
                continue;
            }

            try
            {
                using var adapter = classKey.OpenSubKey(name);
                if (adapter?.GetValue("DriverDesc") is not string description || string.IsNullOrWhiteSpace(description))
                {
                    continue;
                }

                long? memory = adapter.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long q when q > 0 => q,
                    _ => adapter.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int i when i > 0 => (uint)i,
                        byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0) is > 0 and var v ? v : null,
                        _ => null,
                    },
                };

                if (!result.Any(g => g.Name == description && g.DedicatedMemoryBytes == memory))
                {
                    result.Add(new GpuAdapterInfo(description, memory));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Some sub-keys are restricted; skip them.
            }
        }

        return result;
    }

    private static List<NetworkAdapterInfo> ReadNetworkAdapters()
    {
        var result = new List<NetworkAdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            long? speed = nic.Speed > 0 ? nic.Speed : null;
            result.Add(new NetworkAdapterInfo(nic.Name, nic.Description, nic.NetworkInterfaceType.ToString(), speed,
                nic.OperationalStatus == OperationalStatus.Up));
        }

        return result.OrderByDescending(a => a.IsUp).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<DriveSummary> ReadDrives()
    {
        var result = new List<DriveSummary>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    result.Add(new DriveSummary(drive.Name, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.TotalFreeSpace));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pdh?.Dispose();
            _pdh = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
