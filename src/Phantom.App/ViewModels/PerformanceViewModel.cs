using System.Collections.ObjectModel;
using Phantom.App.Mvvm;
using Phantom.App.Services;
using Phantom.Core.Abstractions;
using Phantom.Core.Metrics;
using Phantom.Core.Models;
using Phantom.Core.Monitoring;

namespace Phantom.App.ViewModels;

public sealed class CoreViewModel(int index) : ObservableObject
{
    private double _percent;
    private string _text = Format.Unavailable;

    public string Label { get; } = $"CPU {index}";
    public double Percent { get => _percent; set => Set(ref _percent, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
}

public sealed class PerformanceViewModel : PageViewModel
{
    private readonly UiFrameHub _hub;
    private readonly ISystemMetricsSource _metrics;
    private bool _hardwareLoaded;

    private double[]? _cpuSeries, _memorySeries, _gpuSeries, _diskReadSeries, _diskWriteSeries, _diskActiveSeries, _netDownSeries, _netUpSeries;
    private string _cpuValue = Format.Unavailable, _cpuName = "", _cpuDetail = "";
    private string _memoryValue = Format.Unavailable, _memoryTotal = "", _memoryUsed = "", _memoryAvailable = "";
    private string _gpuValue = Format.Unavailable, _gpuDedicated = "", _gpuShared = "", _gpuAdapters = "", _gpuNotice = "";
    private string _diskValue = Format.Unavailable, _diskRead = "", _diskWrite = "", _diskNotice = "", _diskPeak = "";
    private string _netValue = Format.Unavailable, _netDown = "", _netUp = "", _netPeak = "";
    private IReadOnlyList<NetworkAdapterInfo> _adapters = [];
    private string _intervalNote = "";

    public PerformanceViewModel(UiFrameHub hub, ISystemMetricsSource metrics)
    {
        _hub = hub;
        _metrics = metrics;
        hub.FrameArrived += (frame, _) =>
        {
            if (IsActive)
            {
                Apply(frame);
            }
        };
    }

    public override string Title => "Performance";
    public override string Subtitle => "Detailed live resource monitoring";
    public override string Icon => "";

    public int ChartCapacity => UiFrameHub.HistoryCapacity;
    public ObservableCollection<CoreViewModel> Cores { get; } = [];

    public double[]? CpuSeries { get => _cpuSeries; private set => Set(ref _cpuSeries, value); }
    public double[]? MemorySeries { get => _memorySeries; private set => Set(ref _memorySeries, value); }
    public double[]? GpuSeries { get => _gpuSeries; private set => Set(ref _gpuSeries, value); }
    public double[]? DiskReadSeries { get => _diskReadSeries; private set => Set(ref _diskReadSeries, value); }
    public double[]? DiskWriteSeries { get => _diskWriteSeries; private set => Set(ref _diskWriteSeries, value); }
    public double[]? DiskActiveSeries { get => _diskActiveSeries; private set => Set(ref _diskActiveSeries, value); }
    public double[]? NetDownSeries { get => _netDownSeries; private set => Set(ref _netDownSeries, value); }
    public double[]? NetUpSeries { get => _netUpSeries; private set => Set(ref _netUpSeries, value); }

    public string CpuValue { get => _cpuValue; private set => Set(ref _cpuValue, value); }
    public string CpuName { get => _cpuName; private set => Set(ref _cpuName, value); }
    public string CpuDetail { get => _cpuDetail; private set => Set(ref _cpuDetail, value); }
    public string MemoryValue { get => _memoryValue; private set => Set(ref _memoryValue, value); }
    public string MemoryTotal { get => _memoryTotal; private set => Set(ref _memoryTotal, value); }
    public string MemoryUsed { get => _memoryUsed; private set => Set(ref _memoryUsed, value); }
    public string MemoryAvailable { get => _memoryAvailable; private set => Set(ref _memoryAvailable, value); }
    public string GpuValue { get => _gpuValue; private set => Set(ref _gpuValue, value); }
    public string GpuDedicated { get => _gpuDedicated; private set => Set(ref _gpuDedicated, value); }
    public string GpuShared { get => _gpuShared; private set => Set(ref _gpuShared, value); }
    public string GpuAdapters { get => _gpuAdapters; private set => Set(ref _gpuAdapters, value); }
    /// <summary>Non-empty when GPU telemetry is unsupported; explains why.</summary>
    public string GpuNotice { get => _gpuNotice; private set => Set(ref _gpuNotice, value); }
    public string DiskValue { get => _diskValue; private set => Set(ref _diskValue, value); }
    public string DiskRead { get => _diskRead; private set => Set(ref _diskRead, value); }
    public string DiskWrite { get => _diskWrite; private set => Set(ref _diskWrite, value); }
    public string DiskPeak { get => _diskPeak; private set => Set(ref _diskPeak, value); }
    public string DiskNotice { get => _diskNotice; private set => Set(ref _diskNotice, value); }
    public string NetValue { get => _netValue; private set => Set(ref _netValue, value); }
    public string NetDown { get => _netDown; private set => Set(ref _netDown, value); }
    public string NetUp { get => _netUp; private set => Set(ref _netUp, value); }
    public string NetPeak { get => _netPeak; private set => Set(ref _netPeak, value); }
    public IReadOnlyList<NetworkAdapterInfo> Adapters { get => _adapters; private set => Set(ref _adapters, value); }
    public string IntervalNote { get => _intervalNote; private set => Set(ref _intervalNote, value); }

    protected override void OnActivated()
    {
        if (!_hardwareLoaded)
        {
            _hardwareLoaded = true;
            LoadHardware();
        }

        if (_hub.Latest is { } frame)
        {
            Apply(frame);
        }
    }

    private async void LoadHardware()
    {
        try
        {
            var (hw, sensors) = await Task.Run(() => (_metrics.GetHardwareInfo(), _metrics.GetSensorStatus()));
            CpuName = hw.CpuName ?? "Processor name unavailable";
            GpuAdapters = hw.Gpus.Count == 0
                ? "No adapter information available"
                : string.Join("\n", hw.Gpus.Select(g => g.DedicatedMemoryBytes is { } m ? $"{g.Name} — {Format.Bytes(m)} dedicated" : g.Name));
            Adapters = hw.NetworkAdapters;
            GpuNotice = sensors.FirstOrDefault(s => s.Sensor == "GPU" && !s.IsAvailable)?.Reason ?? "";
            DiskNotice = sensors.FirstOrDefault(s => s.Sensor == "Disk" && !s.IsAvailable)?.Reason ?? "";
        }
        catch (Exception)
        {
            CpuName = "Hardware information could not be read";
        }
    }

    private static string Peak(double[] a, double[]? b = null)
    {
        double peak = 0;
        foreach (double v in b is null ? a : [.. a, .. b])
        {
            if (!double.IsNaN(v) && v > peak)
            {
                peak = v;
            }
        }

        return $"Scale: 0 – {Format.BytesPerSec(Math.Max(peak, 1) * 1.15)} (auto)";
    }

    private void Apply(MonitoringFrame frame)
    {
        var m = frame.Metrics;
        IntervalNote = $"Last {UiFrameHub.HistoryCapacity} samples";

        CpuValue = Format.Percent(m.CpuPercent);
        CpuDetail = $"{Environment.ProcessorCount} logical processors · {Format.Count(m.ProcessCount)} processes · up {Format.Duration(m.Uptime)}";
        CpuSeries = _hub.Series(s => s.CpuPercent);

        if (m.PerCoreCpuPercent is { } cores)
        {
            while (Cores.Count < cores.Count)
            {
                Cores.Add(new CoreViewModel(Cores.Count));
            }

            for (int i = 0; i < cores.Count; i++)
            {
                Cores[i].Percent = cores[i];
                Cores[i].Text = Format.Percent(cores[i]);
            }
        }

        MemoryValue = Format.Percent(m.MemoryPercent);
        MemoryTotal = Format.Bytes(m.MemoryTotalBytes);
        MemoryUsed = Format.Bytes(m.MemoryUsedBytes);
        MemoryAvailable = Format.Bytes(m.MemoryAvailableBytes);
        MemorySeries = _hub.Series(s => s.MemoryPercent);

        GpuValue = Format.Percent(m.GpuPercent);
        GpuDedicated = Format.Bytes(m.GpuDedicatedUsedBytes);
        GpuShared = Format.Bytes(m.GpuSharedUsedBytes);
        GpuSeries = _hub.Series(s => s.GpuPercent);

        DiskValue = Format.Percent(m.DiskActivePercent);
        DiskRead = Format.BytesPerSec(m.DiskReadBytesPerSec);
        DiskWrite = Format.BytesPerSec(m.DiskWriteBytesPerSec);
        var read = _hub.Series(s => s.DiskReadBytesPerSec);
        var write = _hub.Series(s => s.DiskWriteBytesPerSec);
        DiskReadSeries = read;
        DiskWriteSeries = write;
        DiskActiveSeries = _hub.Series(s => s.DiskActivePercent);
        DiskPeak = Peak(read, write);

        NetDown = Format.BytesPerSec(m.NetworkDownloadBytesPerSec);
        NetUp = Format.BytesPerSec(m.NetworkUploadBytesPerSec);
        NetValue = NetDown;
        var down = _hub.Series(s => s.NetworkDownloadBytesPerSec);
        var up = _hub.Series(s => s.NetworkUploadBytesPerSec);
        NetDownSeries = down;
        NetUpSeries = up;
        NetPeak = Peak(down, up);
    }
}
