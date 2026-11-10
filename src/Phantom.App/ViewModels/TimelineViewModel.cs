using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Phantom.App.Mvvm;
using Phantom.App.Services;
using Phantom.Core.Abstractions;
using Phantom.Core.Models;
using Phantom.Infrastructure;

namespace Phantom.App.ViewModels;

public sealed class TimelineItemViewModel(ActivityEvent e)
{
    public ActivityEvent Event { get; } = e;
    public string Time { get; } = e.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public string Date { get; } = e.TimestampUtc.ToLocalTime().ToString("ddd d MMM", CultureInfo.CurrentCulture);
    public string Category { get; } = LabelFor(e.Category);
    public Brush Marker { get; } = BrushFor(e.Category);
    public string Process { get; } = e.ProcessName is null ? "" : e.Pid is { } pid ? $"{e.ProcessName} · PID {pid}" : e.ProcessName;
    public string Description { get; } = e.Description;
    public bool IsInferred { get; } = e.Provenance == EventProvenance.Inferred;
    public string Provenance { get; } = e.Provenance == EventProvenance.Inferred ? "Inferred" : "Observed";
    public string ProvenanceHint { get; } = e.Provenance == EventProvenance.Inferred
        ? "Deduced by comparing samples; the time is accurate to the sampling interval."
        : "Reported by Windows or by Phantom itself; the time is exact.";

    public static string LabelFor(ActivityCategory category) => category switch
    {
        ActivityCategory.ProcessStarted => "Process started",
        ActivityCategory.ProcessExited => "Process exited",
        ActivityCategory.ApplicationLaunched => "Application launch",
        ActivityCategory.ResourceSpike => "Resource spike",
        ActivityCategory.MonitoringInterruption => "Interruption",
        _ => "Monitoring",
    };

    public static Brush BrushFor(ActivityCategory category) => Theme.Brush(category switch
    {
        ActivityCategory.ProcessStarted => "Brush.Good",
        ActivityCategory.ProcessExited => "Brush.TextMuted",
        ActivityCategory.ApplicationLaunched => "Brush.Accent",
        ActivityCategory.ResourceSpike => "Brush.Warn",
        ActivityCategory.MonitoringInterruption => "Brush.Danger",
        _ => "Brush.Series.Memory",
    });
}

public sealed class CategoryFilterViewModel(ActivityCategory category, Action changed) : ObservableObject
{
    private bool _isOn = true;

    public ActivityCategory Category { get; } = category;
    public string Label { get; } = TimelineItemViewModel.LabelFor(category);
    public Brush Marker { get; } = TimelineItemViewModel.BrushFor(category);

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (Set(ref _isOn, value))
            {
                changed();
            }
        }
    }
}

public sealed class TimelineViewModel : PageViewModel
{
    private const int PageSize = 200;
    private const int MaxLiveItems = 2000;
    private const string DateFormat = "yyyy-MM-dd HH:mm";

    private readonly UiFrameHub _hub;
    private readonly IHistoryStore _store;
    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _debounce;
    private int _version;
    private int _rangeIndex = 1;
    private string _searchText = "";
    private string _customFrom = "", _customTo = "";
    private string _summary = "", _sourceNote = "", _rangeError = "";
    private bool _isLoading, _canLoadMore, _dirty = true;
    private long _total;

    public TimelineViewModel(UiFrameHub hub, IHistoryStore store, SettingsStore settings)
    {
        _hub = hub;
        _store = store;
        _settings = settings;
        Categories = Enum.GetValues<ActivityCategory>().Select(c => new CategoryFilterViewModel(c, Reload)).ToList();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Reload();
        };

        ReloadCommand = new RelayCommand(Reload);
        LoadMoreCommand = new RelayCommand(() => Load(append: true), () => CanLoadMore && !IsLoading);
        ApplyCustomRangeCommand = new RelayCommand(() =>
        {
            RangeIndex = RangeOptions.Count - 1;
            Reload();
        });
        ShiftEarlierCommand = new RelayCommand(() => ShiftWindow(-1));
        ShiftLaterCommand = new RelayCommand(() => ShiftWindow(1));

        var today = DateTime.Now.Date;
        _customFrom = today.ToString(DateFormat, CultureInfo.InvariantCulture);
        _customTo = today.AddDays(1).ToString(DateFormat, CultureInfo.InvariantCulture);

        hub.FrameArrived += (_, events) => OnLiveEvents(events);
        settings.Changed += _ => _dirty = true;
    }

    public override string Title => "Activity Timeline";
    public override string Subtitle => "What happened, and when";
    public override string Icon => "";

    public ObservableCollection<TimelineItemViewModel> Items { get; } = [];
    public IReadOnlyList<CategoryFilterViewModel> Categories { get; }
    public IReadOnlyList<string> RangeOptions { get; } =
        ["This session", "Last hour", "Today", "Last 24 hours", "Last 7 days", "All history", "Custom range"];

    public ICommand ReloadCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand ApplyCustomRangeCommand { get; }
    public ICommand ShiftEarlierCommand { get; }
    public ICommand ShiftLaterCommand { get; }

    public int RangeIndex
    {
        get => _rangeIndex;
        set
        {
            if (Set(ref _rangeIndex, value))
            {
                OnPropertyChanged(nameof(IsCustomRange));
                Reload();
            }
        }
    }

    public bool IsCustomRange => _rangeIndex == RangeOptions.Count - 1;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? ""))
            {
                _debounce.Stop();
                _debounce.Start();
            }
        }
    }

    public string CustomFrom { get => _customFrom; set => Set(ref _customFrom, value ?? ""); }
    public string CustomTo { get => _customTo; set => Set(ref _customTo, value ?? ""); }
    public string RangeError { get => _rangeError; private set => Set(ref _rangeError, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string SourceNote { get => _sourceNote; private set => Set(ref _sourceNote, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public bool CanLoadMore { get => _canLoadMore; private set => Set(ref _canLoadMore, value); }

    private bool UsesDatabase => _store.IsAvailable && _settings.Current.PersistHistory && _rangeIndex != 0;

    protected override void OnActivated()
    {
        if (_dirty)
        {
            Reload();
        }
    }

    private static bool TryParseLocal(string text, out DateTime utc)
    {
        string[] formats = [DateFormat, "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss"];
        if (DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out local))
        {
            utc = local.ToUniversalTime();
            return true;
        }

        utc = default;
        return false;
    }

    private void ShiftWindow(int direction)
    {
        if (!TryParseLocal(CustomFrom, out var from) || !TryParseLocal(CustomTo, out var to) || to <= from)
        {
            RangeError = $"Enter both dates as {DateFormat.ToLowerInvariant()}.";
            return;
        }

        var span = to - from;
        CustomFrom = (from + (span * direction)).ToLocalTime().ToString(DateFormat, CultureInfo.InvariantCulture);
        CustomTo = (to + (span * direction)).ToLocalTime().ToString(DateFormat, CultureInfo.InvariantCulture);
        if (!IsCustomRange)
        {
            RangeIndex = RangeOptions.Count - 1;
        }
        else
        {
            Reload();
        }
    }

    /// <summary>Builds the query for the current filters, or returns null if the custom range is invalid.</summary>
    private ActivityQuery? BuildQuery()
    {
        var now = DateTime.UtcNow;
        DateTime? from = null, to = null;
        RangeError = "";
        switch (_rangeIndex)
        {
            case 1:
                from = now.AddHours(-1);
                break;
            case 2:
                from = DateTime.Now.Date.ToUniversalTime();
                break;
            case 3:
                from = now.AddDays(-1);
                break;
            case 4:
                from = now.AddDays(-7);
                break;
            case 6:
                if (!TryParseLocal(CustomFrom, out var f) || !TryParseLocal(CustomTo, out var t))
                {
                    RangeError = $"Enter both dates as {DateFormat.ToLowerInvariant()}.";
                    return null;
                }

                if (t <= f)
                {
                    RangeError = "The end of the range must be after its start.";
                    return null;
                }

                from = f;
                to = t;
                break;
        }

        var categories = Categories.Where(c => c.IsOn).Select(c => c.Category).ToList();
        return new ActivityQuery
        {
            FromUtc = from,
            ToUtc = to,
            Categories = categories.Count == Categories.Count ? null : categories,
            SearchText = string.IsNullOrWhiteSpace(_searchText) ? null : _searchText.Trim(),
            Limit = PageSize,
        };
    }

    private static bool Matches(ActivityEvent e, ActivityQuery q)
    {
        if ((q.FromUtc is { } from && e.TimestampUtc < from) || (q.ToUtc is { } to && e.TimestampUtc > to))
        {
            return false;
        }

        if (q.Categories is { } categories && !categories.Contains(e.Category))
        {
            return false;
        }

        if (q.SearchText is { } text)
        {
            return e.Description.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (e.ProcessName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                || e.Pid?.ToString(CultureInfo.InvariantCulture) == text;
        }

        return true;
    }

    private void Reload() => Load(append: false);

    private async void Load(bool append)
    {
        if (!IsActive)
        {
            _dirty = true;
            return;
        }

        _dirty = false;
        int version = ++_version;
        var query = BuildQuery();
        if (query is null)
        {
            Items.Clear();
            Summary = "";
            CanLoadMore = false;
            return;
        }

        query = query with { Offset = append ? Items.Count : 0 };
        bool database = UsesDatabase;
        SourceNote = database
            ? "Reading from the local history database."
            : _rangeIndex == 0
                ? "Showing events from the current session (kept in memory)."
                : !_store.IsAvailable
                    ? "History database unavailable — showing the current session only."
                    : "History recording is turned off — showing the current session only.";

        IsLoading = true;
        try
        {
            IReadOnlyList<ActivityEvent> events;
            long total;
            if (database)
            {
                (events, total) = await Task.Run(() => (_store.QueryEvents(query), _store.CountEvents(query)));
            }
            else
            {
                var matching = _hub.SessionEvents.Where(e => Matches(e, query)).OrderByDescending(e => e.TimestampUtc).ToList();
                total = matching.Count;
                events = matching.Skip(query.Offset).Take(PageSize).ToList();
            }

            if (version != _version)
            {
                return; // A newer request superseded this one.
            }

            if (!append)
            {
                Items.Clear();
            }

            foreach (var e in events)
            {
                Items.Add(new TimelineItemViewModel(e));
            }

            _total = total;
            UpdateSummary();
        }
        catch (Exception)
        {
            Summary = "Events could not be loaded.";
        }
        finally
        {
            if (version == _version)
            {
                IsLoading = false;
            }
        }
    }

    private void UpdateSummary()
    {
        CanLoadMore = Items.Count < _total;
        Summary = _total == 0
            ? "No events match the current filters."
            : $"Showing {Items.Count:N0} of {_total:N0} events, newest first.";
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnLiveEvents(IReadOnlyList<ActivityEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        if (!IsActive)
        {
            _dirty = true;
            return;
        }

        // Only open-ended ranges receive live events; a fixed historical window stays put.
        if (IsLoading || BuildQuery() is not { ToUtc: null } query)
        {
            return;
        }

        foreach (var e in events.OrderBy(e => e.TimestampUtc))
        {
            if (Matches(e, query))
            {
                Items.Insert(0, new TimelineItemViewModel(e));
                _total++;
            }
        }

        while (Items.Count > MaxLiveItems)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        UpdateSummary();
    }
}
