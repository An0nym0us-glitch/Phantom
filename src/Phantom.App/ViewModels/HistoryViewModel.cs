using System.Globalization;
using System.IO;
using System.Windows.Input;
using Phantom.App.Mvvm;
using Phantom.App.Services;
using Phantom.Core.Abstractions;
using Phantom.Core.Metrics;
using Phantom.Core.Models;
using Phantom.Infrastructure;
using Phantom.Storage;

namespace Phantom.App.ViewModels;

public sealed record SessionLine(string Started, string Ended, string Duration, string Version);

public sealed class HistoryViewModel : PageViewModel
{
    private const int Buckets = 240;

    private readonly IHistoryStore _store;
    private readonly SettingsStore _settings;
    private readonly IDialogService _dialogs;
    private int _rangeIndex = 1;
    private int _version;
    private bool _isBusy;
    private string _status = "", _rangeLabel = "", _coverage = "";
    private string _statSamples = "", _statEvents = "", _statSessions = "", _statSize = "", _statOldest = "", _retentionText = "";
    private double[]? _cpu, _memory, _gpu, _diskRead, _diskWrite, _netDown, _netUp, _processes;
    private IReadOnlyList<SessionLine> _sessions = [];

    private static readonly TimeSpan?[] Ranges =
        [TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24), TimeSpan.FromDays(7), TimeSpan.FromDays(30)];

    public HistoryViewModel(IHistoryStore store, SettingsStore settings, IDialogService dialogs)
    {
        _store = store;
        _settings = settings;
        _dialogs = dialogs;
        RefreshCommand = new RelayCommand(Load, () => !IsBusy);
        ExportMetricsCommand = new RelayCommand(format => Export(metrics: true, format as string), _ => CanUseStore);
        ExportEventsCommand = new RelayCommand(format => Export(metrics: false, format as string), _ => CanUseStore);
        ApplyRetentionCommand = new RelayCommand(ApplyRetention, () => CanUseStore);
        CompactCommand = new RelayCommand(() => RunMaintenance("Database compacted.", s => s.Compact()), () => CanUseStore);
        ClearCommand = new RelayCommand(Clear, () => CanUseStore);
        OpenFolderCommand = new RelayCommand(() =>
        {
            if (_store.DatabasePath is { } path && Path.GetDirectoryName(path) is { } folder)
            {
                _dialogs.OpenFolder(folder);
            }
        });
    }

    public override string Title => "History";
    public override string Subtitle => "Stored metrics and data management";
    public override string Icon => "";

    public IReadOnlyList<string> RangeOptions { get; } = ["Last hour", "Last 6 hours", "Last 24 hours", "Last 7 days", "Last 30 days"];
    public ICommand RefreshCommand { get; }
    public ICommand ExportMetricsCommand { get; }
    public ICommand ExportEventsCommand { get; }
    public ICommand ApplyRetentionCommand { get; }
    public ICommand CompactCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public bool IsAvailable => _store.IsAvailable;
    public string? StoreMessage => _store.StatusMessage;
    public string DatabasePath => _store.DatabasePath ?? "";
    private bool CanUseStore => _store.IsAvailable && !IsBusy;

    public int RangeIndex
    {
        get => _rangeIndex;
        set
        {
            if (Set(ref _rangeIndex, value))
            {
                Load();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string RangeLabel { get => _rangeLabel; private set => Set(ref _rangeLabel, value); }
    public string Coverage { get => _coverage; private set => Set(ref _coverage, value); }
    public string StatSamples { get => _statSamples; private set => Set(ref _statSamples, value); }
    public string StatEvents { get => _statEvents; private set => Set(ref _statEvents, value); }
    public string StatSessions { get => _statSessions; private set => Set(ref _statSessions, value); }
    public string StatSize { get => _statSize; private set => Set(ref _statSize, value); }
    public string StatOldest { get => _statOldest; private set => Set(ref _statOldest, value); }
    public string RetentionText { get => _retentionText; private set => Set(ref _retentionText, value); }
    public double[]? Cpu { get => _cpu; private set => Set(ref _cpu, value); }
    public double[]? Memory { get => _memory; private set => Set(ref _memory, value); }
    public double[]? Gpu { get => _gpu; private set => Set(ref _gpu, value); }
    public double[]? DiskRead { get => _diskRead; private set => Set(ref _diskRead, value); }
    public double[]? DiskWrite { get => _diskWrite; private set => Set(ref _diskWrite, value); }
    public double[]? NetDown { get => _netDown; private set => Set(ref _netDown, value); }
    public double[]? NetUp { get => _netUp; private set => Set(ref _netUp, value); }
    public double[]? Processes { get => _processes; private set => Set(ref _processes, value); }
    public IReadOnlyList<SessionLine> Sessions { get => _sessions; private set => Set(ref _sessions, value); }

    protected override void OnActivated()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(StoreMessage));
        Load();
    }

    private (DateTime From, DateTime To) CurrentRange()
    {
        var to = DateTime.UtcNow;
        var span = Ranges[Math.Clamp(_rangeIndex, 0, Ranges.Length - 1)]!.Value;
        return (to - span, to);
    }

    /// <summary>
    /// Places bucketed samples on a fixed time axis. Buckets with no data stay NaN so that
    /// periods when Phantom was not running appear as gaps rather than as flat lines.
    /// </summary>
    private static double[] Align(IReadOnlyList<SystemMetricsSample> samples, DateTime from, DateTime to,
        Func<SystemMetricsSample, double?> selector)
    {
        var result = new double[Buckets];
        Array.Fill(result, double.NaN);
        double span = (to - from).Ticks;
        foreach (var s in samples)
        {
            if (selector(s) is { } value)
            {
                int index = (int)Math.Clamp((s.TimestampUtc - from).Ticks / span * Buckets, 0, Buckets - 1);
                result[index] = value;
            }
        }

        return result;
    }

    private async void Load()
    {
        if (!IsActive)
        {
            return;
        }

        int version = ++_version;
        var (from, to) = CurrentRange();
        RetentionText = $"Records older than {_settings.Current.RetentionDays} days are deleted automatically.";
        RangeLabel = $"{from.ToLocalTime():g}  →  {to.ToLocalTime():g}";
        if (!_store.IsAvailable)
        {
            Coverage = "";
            return;
        }

        IsBusy = true;
        try
        {
            var (samples, stats, sessions) = await Task.Run(() =>
                (_store.QueryMetrics(from, to, Buckets), _store.GetStats(), _store.GetSessions(12)));
            if (version != _version)
            {
                return;
            }

            Cpu = Align(samples, from, to, s => s.CpuPercent);
            Memory = Align(samples, from, to, s => s.MemoryPercent);
            Gpu = Align(samples, from, to, s => s.GpuPercent);
            DiskRead = Align(samples, from, to, s => s.DiskReadBytesPerSec);
            DiskWrite = Align(samples, from, to, s => s.DiskWriteBytesPerSec);
            NetDown = Align(samples, from, to, s => s.NetworkDownloadBytesPerSec);
            NetUp = Align(samples, from, to, s => s.NetworkUploadBytesPerSec);
            Processes = Align(samples, from, to, s => s.ProcessCount);
            Coverage = samples.Count == 0
                ? "No samples were recorded in this period."
                : $"{samples.Count:N0} of {Buckets} time slots contain data. Gaps mean Phantom was not running or recording.";

            StatSamples = stats.MetricSampleCount.ToString("N0", CultureInfo.CurrentCulture);
            StatEvents = stats.EventCount.ToString("N0", CultureInfo.CurrentCulture);
            StatSessions = stats.SessionCount.ToString("N0", CultureInfo.CurrentCulture);
            StatSize = Format.Bytes(stats.DatabaseSizeBytes);
            StatOldest = stats.OldestRecordUtc is { } oldest ? oldest.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : Format.Unavailable;
            Sessions = sessions.Select(s => new SessionLine(
                s.StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                s.EndedUtc is { } e ? e.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "Running now",
                Format.Duration((s.EndedUtc ?? DateTime.UtcNow) - s.StartedUtc),
                s.AppVersion)).ToList();
        }
        catch (Exception)
        {
            Status = "History could not be loaded.";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(StoreMessage));
        }
    }

    private async void Export(bool metrics, string? formatName)
    {
        var format = string.Equals(formatName, "json", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Json : ExportFormat.Csv;
        string extension = format == ExportFormat.Json ? "json" : "csv";
        string kind = metrics ? "metrics" : "events";
        string? path = _dialogs.PickSaveFile($"phantom-{kind}-{DateTime.Now:yyyyMMdd-HHmm}.{extension}",
            format == ExportFormat.Json ? "JSON (*.json)|*.json" : "CSV (*.csv)|*.csv");
        if (path is null)
        {
            return;
        }

        var (from, to) = CurrentRange();
        IsBusy = true;
        try
        {
            long count = await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                return metrics
                    ? HistoryExporter.ExportMetrics(_store, from, to, stream, format)
                    : HistoryExporter.ExportEvents(_store, new ActivityQuery { FromUtc = from, ToUtc = to }, stream, format);
            });
            Status = $"Exported {count:N0} {kind} records from the selected period to {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Status = $"Export failed: the file could not be written ({ex.GetType().Name}).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyRetention()
    {
        int days = _settings.Current.RetentionDays;
        RunMaintenance(null, s =>
        {
            long removed = s.ApplyRetention(DateTime.UtcNow.AddDays(-days));
            return $"Removed {removed:N0} records older than {days} days.";
        });
    }

    private void Clear()
    {
        if (_dialogs.Confirm("Clear history",
                "Delete all stored metric samples, activity events and past sessions?\n\nThis cannot be undone. Live monitoring is not affected."))
        {
            RunMaintenance("History cleared.", s => s.ClearAll());
        }
    }

    private void RunMaintenance(string message, Action<IHistoryStore> action)
        => RunMaintenance(null, s =>
        {
            action(s);
            return message;
        });

    private async void RunMaintenance(string? _, Func<IHistoryStore, string> action)
    {
        IsBusy = true;
        try
        {
            Status = await Task.Run(() => action(_store));
        }
        catch (Exception)
        {
            Status = "The operation failed.";
        }
        finally
        {
            IsBusy = false;
        }

        Load();
    }
}
