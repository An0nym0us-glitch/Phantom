using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using Phantom.App.Mvvm;
using Phantom.App.Services;
using Phantom.Core.Abstractions;
using Phantom.Core.Metrics;
using Phantom.Core.Models;
using Phantom.Core.Monitoring;
using Phantom.Infrastructure;

namespace Phantom.App.ViewModels;

public sealed class ShellViewModel : ObservableObject
{
    private readonly MonitoringEngine _engine;
    private readonly IHistoryStore _history;
    private readonly SettingsStore _settings;
    private PageViewModel _current;
    private bool _isPaused;
    private string _state = "Starting…", _lastUpdate = "", _cost = "", _summary = "", _historyStatus = "";
    private Brush _stateBrush = Theme.Brush("Brush.TextMuted");

    public ShellViewModel(
        MonitoringEngine engine,
        UiFrameHub hub,
        IHistoryStore history,
        SettingsStore settings,
        ISystemMetricsSource metrics,
        AppPaths paths,
        IDialogService dialogs)
    {
        _engine = engine;
        _history = history;
        _settings = settings;

        Processes = new ProcessesViewModel(hub, engine);
        Overview = new OverviewViewModel(hub, metrics, InspectProcess);
        Map = new SystemMapViewModel(hub);
        Timeline = new TimelineViewModel(hub, history, settings);
        Performance = new PerformanceViewModel(hub, metrics);
        History = new HistoryViewModel(history, settings, dialogs);
        Settings = new SettingsViewModel(settings, paths, metrics, dialogs);
        Pages = [Overview, Map, Processes, Timeline, Performance, History, Settings];

        _current = Overview;
        _current.IsActive = true;

        TogglePauseCommand = new RelayCommand(() => IsPaused = !IsPaused);
        RefreshCommand = new RelayCommand(engine.RefreshNow, () => !IsPaused);
        InspectCommand = new RelayCommand(p =>
        {
            if (p is ProcessKey key)
            {
                InspectProcess(key);
            }
        });

        hub.FrameArrived += (frame, _) => OnFrame(frame);
        settings.Changed += _ => UpdateHistoryStatus();
        UpdateHistoryStatus();
    }

    public OverviewViewModel Overview { get; }
    public SystemMapViewModel Map { get; }
    public ProcessesViewModel Processes { get; }
    public TimelineViewModel Timeline { get; }
    public PerformanceViewModel Performance { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }

    public ICommand TogglePauseCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand InspectCommand { get; }

    public PageViewModel Current
    {
        get => _current;
        set
        {
            if (value is null || ReferenceEquals(value, _current))
            {
                return;
            }

            _current.IsActive = false;
            _current = value;
            OnPropertyChanged();
            _current.IsActive = true;
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (Set(ref _isPaused, value))
            {
                _engine.SetPaused(value);
                OnPropertyChanged(nameof(PauseLabel));
                OnPropertyChanged(nameof(PauseIcon));
                if (value)
                {
                    State = "Paused";
                    StateBrush = Theme.Brush("Brush.Warn");
                }

                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string PauseLabel => _isPaused ? "Resume" : "Pause";
    public string PauseIcon => _isPaused ? "" : "";
    public string State { get => _state; private set => Set(ref _state, value); }
    public Brush StateBrush { get => _stateBrush; private set => Set(ref _stateBrush, value); }
    public string LastUpdate { get => _lastUpdate; private set => Set(ref _lastUpdate, value); }
    public string Cost { get => _cost; private set => Set(ref _cost, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string HistoryStatus { get => _historyStatus; private set => Set(ref _historyStatus, value); }

    /// <summary>Opens the Processes page with the given process selected.</summary>
    public void InspectProcess(ProcessKey key)
    {
        Current = Processes;
        Processes.Select(key);
    }

    private void OnFrame(MonitoringFrame frame)
    {
        if (_isPaused)
        {
            return;
        }

        if (frame.ProcessDataFresh)
        {
            State = "Monitoring";
            StateBrush = Theme.Brush("Brush.Good");
        }
        else
        {
            State = "Process data is stale";
            StateBrush = Theme.Brush("Brush.Danger");
        }

        var m = frame.Metrics;
        LastUpdate = $"Updated {m.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)}"
            + $" · every {_settings.Current.SampleIntervalMs / 1000.0:0.#} s";
        Cost = $"Collection {frame.CollectionTime.TotalMilliseconds:0} ms";
        Summary = $"CPU {Format.Percent(m.CpuPercent)}  ·  Memory {Format.Percent(m.MemoryPercent)}  ·  {Format.Count(m.ProcessCount)} processes";
    }

    private void UpdateHistoryStatus()
        => HistoryStatus = !_history.IsAvailable
            ? "History unavailable"
            : _settings.Current.PersistHistory ? "History recording on" : "History recording off";
}
