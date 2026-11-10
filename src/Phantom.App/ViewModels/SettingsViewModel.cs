using System.Globalization;
using System.Reflection;
using System.Security.Principal;
using System.Windows.Input;
using Phantom.App.Mvvm;
using Phantom.App.Services;
using Phantom.Core.Abstractions;
using Phantom.Core.Models;
using Phantom.Infrastructure;

namespace Phantom.App.ViewModels;

public sealed record Option<T>(string Label, T Value);

public sealed class SettingsViewModel : PageViewModel
{
    private readonly SettingsStore _store;
    private readonly ISystemMetricsSource _metrics;
    private IReadOnlyList<SensorStatus> _sensors = [];
    private string _saveStatus = "";

    public SettingsViewModel(SettingsStore store, AppPaths paths, ISystemMetricsSource metrics, IDialogService dialogs)
    {
        _store = store;
        _metrics = metrics;
        DataFolder = paths.Root;
        LogFolder = paths.LogDirectory;
        OpenDataFolderCommand = new RelayCommand(() => dialogs.OpenFolder(paths.Root));
        ResetCommand = new RelayCommand(() =>
        {
            Save(new AppSettings());
            foreach (string name in new[] { nameof(Interval), nameof(Retention), nameof(PersistHistory), nameof(ProcessCpuSpike), nameof(SystemCpuSpike), nameof(MemorySpike) })
            {
                OnPropertyChanged(name);
            }
        });

        Intervals =
        [
            new("High — every 0.5 s", 500),
            new("Normal — every 1 s", 1000),
            new("Low — every 2 s", 2000),
            new("Very low — every 5 s", 5000),
        ];
        Retentions = AppSettings.AllowedRetentionDays.Select(d => new Option<int>(d == 1 ? "1 day" : $"{d} days", d)).ToList();
        SpikeLevels = new[] { 25, 50, 75, 90, 95 }.Select(v => new Option<double>($"{v}%", v)).ToList();
    }

    public override string Title => "Settings";
    public override string Subtitle => "Monitoring, privacy and data";
    public override string Icon => "";

    public IReadOnlyList<Option<int>> Intervals { get; }
    public IReadOnlyList<Option<int>> Retentions { get; }
    public IReadOnlyList<Option<double>> SpikeLevels { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand ResetCommand { get; }
    public string DataFolder { get; }
    public string LogFolder { get; }

    public string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

    public string Runtime { get; } = $".NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";

    public string Privilege { get; } = IsElevated()
        ? "Running as administrator. Phantom does not need this; it was started that way."
        : "Running as a standard user. Details of protected and other-account processes are shown as unavailable.";

    public string SaveStatus { get => _saveStatus; private set => Set(ref _saveStatus, value); }
    public IReadOnlyList<SensorStatus> Sensors { get => _sensors; private set => Set(ref _sensors, value); }

    public Option<int>? Interval
    {
        get => Intervals.FirstOrDefault(o => o.Value == _store.Current.SampleIntervalMs) ?? Intervals[1];
        set
        {
            if (value is not null)
            {
                Save(_store.Current with { SampleIntervalMs = value.Value });
            }
        }
    }

    public Option<int>? Retention
    {
        get => Retentions.FirstOrDefault(o => o.Value == _store.Current.RetentionDays) ?? Retentions[2];
        set
        {
            if (value is not null)
            {
                Save(_store.Current with { RetentionDays = value.Value });
            }
        }
    }

    public bool PersistHistory
    {
        get => _store.Current.PersistHistory;
        set => Save(_store.Current with { PersistHistory = value });
    }

    public Option<double>? ProcessCpuSpike
    {
        get => Nearest(_store.Current.ProcessCpuSpikePercent);
        set => Save(_store.Current with { ProcessCpuSpikePercent = value?.Value ?? 50 });
    }

    public Option<double>? SystemCpuSpike
    {
        get => Nearest(_store.Current.SystemCpuSpikePercent);
        set => Save(_store.Current with { SystemCpuSpikePercent = value?.Value ?? 90 });
    }

    public Option<double>? MemorySpike
    {
        get => Nearest(_store.Current.MemorySpikePercent);
        set => Save(_store.Current with { MemorySpikePercent = value?.Value ?? 90 });
    }

    private Option<double> Nearest(double value) => SpikeLevels.MinBy(o => Math.Abs(o.Value - value))!;

    private void Save(AppSettings settings)
    {
        if (settings == _store.Current)
        {
            return;
        }

        SaveStatus = _store.Save(settings)
            ? $"Saved at {DateTime.Now.ToString("T", CultureInfo.CurrentCulture)}."
            : "Applied for this session, but the settings file could not be written.";
    }

    protected override async void OnActivated()
    {
        try
        {
            Sensors = await Task.Run(_metrics.GetSensorStatus);
        }
        catch (Exception)
        {
            Sensors = [];
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
