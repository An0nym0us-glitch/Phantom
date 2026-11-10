using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using Phantom.App.Mvvm;
using Phantom.App.Services;
using Phantom.Core.Abstractions;
using Phantom.Core.Metrics;
using Phantom.Core.Models;
using Phantom.Core.Monitoring;

namespace Phantom.App.ViewModels;

public sealed class MetricCardViewModel(string title, string icon, Brush stroke, Brush? stroke2 = null) : ObservableObject
{
    private string _value = Format.Unavailable;
    private string _detail = "";
    private double[]? _values;
    private double[]? _values2;
    private double _maximum = 100;

    public string Title { get; } = title;
    public string Icon { get; } = icon;
    public Brush Stroke { get; } = stroke;
    public Brush? Stroke2 { get; } = stroke2;
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public double[]? Values { get => _values; set => Set(ref _values, value); }
    public double[]? Values2 { get => _values2; set => Set(ref _values2, value); }
    /// <summary>Chart scale; NaN auto-scales (used for throughput, which has no natural maximum).</summary>
    public double Maximum { get => _maximum; set => Set(ref _maximum, value); }
}

public sealed record TopProcess(ProcessKey Key, string Name, int Pid, string ValueText, double Fraction);

public sealed record InfoLine(string Label, string Value);

public sealed class EventLineViewModel(ActivityEvent e)
{
    public string Time { get; } = e.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
    public string Description { get; } = e.Description;
    public Brush Marker { get; } = TimelineItemViewModel.BrushFor(e.Category);
}

public sealed class OverviewViewModel : PageViewModel
{
    private readonly UiFrameHub _hub;
    private readonly ISystemMetricsSource _metrics;
    private IReadOnlyList<TopProcess> _topCpu = [];
    private IReadOnlyList<TopProcess> _topMemory = [];
    private IReadOnlyList<InfoLine> _hardware = [];
    private IReadOnlyList<EventLineViewModel> _recentEvents = [];
    private double[]? _cpuSeries, _memorySeries;
    private string _gpuNote = "";

    public OverviewViewModel(UiFrameHub hub, ISystemMetricsSource metrics, Action<ProcessKey> inspect)
    {
        _hub = hub;
        _metrics = metrics;
        Cpu = new MetricCardViewModel("CPU", "", Theme.Brush("Brush.Series.Cpu"));
        Memory = new MetricCardViewModel("Memory", "", Theme.Brush("Brush.Series.Memory"));
        Gpu = new MetricCardViewModel("GPU", "", Theme.Brush("Brush.Series.Gpu"));
        Disk = new MetricCardViewModel("Disk", "", Theme.Brush("Brush.Series.Disk"));
        Network = new MetricCardViewModel("Network", "", Theme.Brush("Brush.Series.NetDown"), Theme.Brush("Brush.Series.NetUp")) { Maximum = double.NaN };
        Processes = new MetricCardViewModel("Processes", "", Theme.Brush("Brush.TextSecondary")) { Maximum = double.NaN };
        Cards = [Cpu, Memory, Gpu, Disk, Network, Processes];
        InspectCommand = new RelayCommand(p =>
        {
            if (p is ProcessKey key)
            {
                inspect(key);
            }
        });
        hub.FrameArrived += (frame, _) =>
        {
            if (IsActive)
            {
                Apply(frame);
            }
        };
    }

    public override string Title => "Overview";
    public override string Subtitle => "Live system summary";
    public override string Icon => "";

    public MetricCardViewModel Cpu { get; }
    public MetricCardViewModel Memory { get; }
    public MetricCardViewModel Gpu { get; }
    public MetricCardViewModel Disk { get; }
    public MetricCardViewModel Network { get; }
    public MetricCardViewModel Processes { get; }
    public IReadOnlyList<MetricCardViewModel> Cards { get; }
    public ICommand InspectCommand { get; }
    public int ChartCapacity => UiFrameHub.HistoryCapacity;

    public double[]? CpuSeries { get => _cpuSeries; private set => Set(ref _cpuSeries, value); }
    public double[]? MemorySeries { get => _memorySeries; private set => Set(ref _memorySeries, value); }
    public IReadOnlyList<TopProcess> TopCpu { get => _topCpu; private set => Set(ref _topCpu, value); }
    public IReadOnlyList<TopProcess> TopMemory { get => _topMemory; private set => Set(ref _topMemory, value); }
    public IReadOnlyList<InfoLine> Hardware { get => _hardware; private set => Set(ref _hardware, value); }
    public IReadOnlyList<EventLineViewModel> RecentEvents { get => _recentEvents; private set => Set(ref _recentEvents, value); }
    public string GpuNote { get => _gpuNote; private set => Set(ref _gpuNote, value); }

    protected override void OnActivated()
    {
        if (_hardware.Count == 0)
        {
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
            // Registry and adapter enumeration can be slow on some machines; keep it off the UI thread.
            var (hw, sensors) = await Task.Run(() => (_metrics.GetHardwareInfo(), _metrics.GetSensorStatus()));
            var lines = new List<InfoLine>
            {
                new("Processor", hw.CpuName ?? Format.Unavailable),
                new("Logical processors", hw.LogicalProcessors.ToString()),
                new("Installed memory", Format.Bytes(hw.TotalMemoryBytes)),
                new("Graphics", hw.Gpus.Count == 0
                    ? "No adapter information available"
                    : string.Join(", ", hw.Gpus.Select(g => g.DedicatedMemoryBytes is { } m ? $"{g.Name} ({Format.Bytes(m)})" : g.Name))),
                new("Operating system", $"{hw.OsDescription} ({hw.OsArchitecture})"),
            };
            foreach (var d in hw.Drives)
            {
                lines.Add(new($"Drive {d.Name.TrimEnd('\\')}", $"{Format.Bytes(d.FreeBytes)} free of {Format.Bytes(d.TotalBytes)}"));
            }

            Hardware = lines;
            GpuNote = sensors.FirstOrDefault(s => s.Sensor == "GPU" && !s.IsAvailable)?.Reason ?? "";
        }
        catch (Exception)
        {
            Hardware = [new("Hardware", "Information could not be read")];
        }
    }

    private void Apply(MonitoringFrame frame)
    {
        var m = frame.Metrics;

        Cpu.Value = Format.Percent(m.CpuPercent);
        Cpu.Detail = $"{Environment.ProcessorCount} logical processors";
        Cpu.Values = CpuSeries = _hub.Series(s => s.CpuPercent);

        Memory.Value = Format.Percent(m.MemoryPercent);
        Memory.Detail = m.MemoryUsedBytes is null ? "Unavailable" : $"{Format.Bytes(m.MemoryUsedBytes)} of {Format.Bytes(m.MemoryTotalBytes)}";
        Memory.Values = MemorySeries = _hub.Series(s => s.MemoryPercent);

        Gpu.Value = Format.Percent(m.GpuPercent);
        Gpu.Detail = m.GpuPercent is null
            ? (GpuNote.Length > 0 ? "Telemetry unavailable" : "No GPU activity data")
            : m.GpuDedicatedUsedBytes is { } dedicated ? $"{Format.Bytes(dedicated)} dedicated in use" : "Busiest engine";
        Gpu.Values = _hub.Series(s => s.GpuPercent);

        Disk.Value = m.DiskActivePercent is null && m.DiskReadBytesPerSec is null ? Format.Unavailable : Format.Percent(m.DiskActivePercent);
        Disk.Detail = m.DiskReadBytesPerSec is null
            ? "Counters unavailable"
            : $"Read {Format.BytesPerSec(m.DiskReadBytesPerSec)} · Write {Format.BytesPerSec(m.DiskWriteBytesPerSec)}";
        Disk.Values = _hub.Series(s => s.DiskActivePercent);

        Network.Value = Format.BytesPerSec(m.NetworkDownloadBytesPerSec);
        Network.Detail = m.NetworkUploadBytesPerSec is null && m.NetworkDownloadBytesPerSec is null
            ? "Measuring…"
            : $"Download · Upload {Format.BytesPerSec(m.NetworkUploadBytesPerSec)}";
        Network.Values = _hub.Series(s => s.NetworkDownloadBytesPerSec);
        Network.Values2 = _hub.Series(s => s.NetworkUploadBytesPerSec);

        Processes.Value = Format.Count(m.ProcessCount);
        Processes.Detail = $"{frame.Snapshot.Processes.Sum(p => (long)p.ThreadCount):N0} threads · up {Format.Duration(m.Uptime)}";
        Processes.Values = _hub.Series(s => s.ProcessCount);

        var real = frame.Snapshot.Processes.Where(p => p.Pid != 0).ToList();
        TopCpu = real.Where(p => p.CpuPercent is > 0)
            .OrderByDescending(p => p.CpuPercent)
            .Take(6)
            .Select(p => new TopProcess(p.Key, p.Name, p.Pid, Format.Percent(p.CpuPercent, 1), Math.Clamp(p.CpuPercent!.Value / 100, 0, 1)))
            .ToList();
        long total = Math.Max(1, m.MemoryTotalBytes ?? 1);
        TopMemory = real.OrderByDescending(p => p.WorkingSetBytes)
            .Take(6)
            .Select(p => new TopProcess(p.Key, p.Name, p.Pid, Format.Bytes(p.WorkingSetBytes), Math.Clamp((double)p.WorkingSetBytes / total, 0, 1)))
            .ToList();

        var events = _hub.SessionEvents;
        RecentEvents = events.Skip(Math.Max(0, events.Count - 7)).Reverse().Select(e => new EventLineViewModel(e)).ToList();
    }
}
