using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using Phantom.App.Mvvm;
using Phantom.App.Services;
using Phantom.Core.Metrics;
using Phantom.Core.Models;
using Phantom.Core.Monitoring;
using Phantom.SystemMap;

namespace Phantom.App.ViewModels;

public sealed class ProcessRowViewModel : ObservableObject
{
    private string _cpuText = "", _memoryText = "", _privateText = "", _handlesText = "";
    private double _cpuSort;
    private long _memory, _privateSort;
    private int _threads, _handlesSort;

    public ProcessRowViewModel(ProcessInfo p)
    {
        Info = p;
        Key = p.Key;
        Pid = p.Pid;
        Name = p.Name;
        StartSort = p.StartTimeUtc ?? DateTime.MinValue;
        StartText = p.StartTimeUtc is { } s ? s.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture) : Format.Unavailable;
        OwnerText = p.Owner switch
        {
            ProcessOwnerKind.CurrentUser => "User",
            ProcessOwnerKind.System => "System",
            _ => Format.Unavailable,
        };
        ArchitectureText = p.Architecture == ProcessArchitecture.Unknown ? Format.Unavailable : p.Architecture.ToString().ToLowerInvariant();
        Update(p);
    }

    public ProcessInfo Info { get; private set; }
    public ProcessKey Key { get; }
    public int Pid { get; }
    public string Name { get; }
    public DateTime StartSort { get; }
    public string StartText { get; }
    public string OwnerText { get; }
    public string ArchitectureText { get; }

    public double CpuSort { get => _cpuSort; private set => Set(ref _cpuSort, value); }
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    public long Memory { get => _memory; private set => Set(ref _memory, value); }
    public string MemoryText { get => _memoryText; private set => Set(ref _memoryText, value); }
    public long PrivateSort { get => _privateSort; private set => Set(ref _privateSort, value); }
    public string PrivateText { get => _privateText; private set => Set(ref _privateText, value); }
    public int Threads { get => _threads; private set => Set(ref _threads, value); }
    public int HandlesSort { get => _handlesSort; private set => Set(ref _handlesSort, value); }
    public string HandlesText { get => _handlesText; private set => Set(ref _handlesText, value); }

    public void Update(ProcessInfo p)
    {
        Info = p;
        // Sort keys place "unavailable" below every real value instead of treating it as zero.
        CpuSort = p.CpuPercent ?? -1;
        CpuText = Format.Percent(p.CpuPercent, 1);
        Memory = p.WorkingSetBytes;
        MemoryText = Format.Bytes(p.WorkingSetBytes);
        PrivateSort = p.PrivateBytes ?? -1;
        PrivateText = Format.Bytes(p.PrivateBytes);
        Threads = p.ThreadCount;
        HandlesSort = p.HandleCount ?? -1;
        HandlesText = Format.Count(p.HandleCount);
    }
}

public sealed class ProcessesViewModel : PageViewModel
{
    private readonly UiFrameHub _hub;
    private readonly Dictionary<ProcessKey, ProcessRowViewModel> _byKey = [];
    private string _searchText = "";
    private int _filterIndex;
    private ProcessRowViewModel? _selected;
    private ProcessKey? _selectedKey;
    private string _summary = "";

    public ProcessesViewModel(UiFrameHub hub, MonitoringEngine engine)
    {
        _hub = hub;
        View = new ListCollectionView(Rows) { Filter = Matches };
        View.SortDescriptions.Add(new SortDescription(nameof(ProcessRowViewModel.CpuSort), ListSortDirection.Descending));
        View.IsLiveSorting = true;
        View.LiveSortingProperties.Add(nameof(ProcessRowViewModel.CpuSort));
        RefreshCommand = new RelayCommand(engine.RefreshNow);
        ClearSelectionCommand = new RelayCommand(() =>
        {
            _selectedKey = null;
            Selected = null;
            UpdateDetail();
        });
        hub.FrameArrived += (frame, _) =>
        {
            if (IsActive)
            {
                Apply(frame);
            }
        };
    }

    public override string Title => "Processes";
    public override string Subtitle => "Inspect every running process";
    public override string Icon => "";

    public ObservableCollection<ProcessRowViewModel> Rows { get; } = [];
    public ListCollectionView View { get; }
    public ProcessDetailViewModel Detail { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand ClearSelectionCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? ""))
            {
                View.Refresh();
                UpdateSummary();
            }
        }
    }

    /// <summary>0 = all, 1 = current user, 2 = system.</summary>
    public int FilterIndex
    {
        get => _filterIndex;
        set
        {
            if (Set(ref _filterIndex, value))
            {
                View.Refresh();
                UpdateSummary();
            }
        }
    }

    public ProcessRowViewModel? Selected
    {
        get => _selected;
        set
        {
            var previous = _selected;
            if (Set(ref _selected, value))
            {
                // When the selected process exits the grid drops its row; keep the key so the
                // details panel can say "exited" instead of silently going blank.
                bool exited = value is null && previous is not null && !_byKey.ContainsKey(previous.Key);
                if (!exited)
                {
                    _selectedKey = value?.Key;
                }

                OnPropertyChanged(nameof(HasSelection));
                UpdateDetail();
            }
        }
    }

    public bool HasSelection => _selected is not null || Detail.HasProcess;
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    /// <summary>Selects a process by identity (used when another page asks to inspect it).</summary>
    public void Select(ProcessKey key)
    {
        if (_filterIndex != 0 || _searchText.Length > 0)
        {
            _filterIndex = 0;
            _searchText = "";
            OnPropertyChanged(nameof(FilterIndex));
            OnPropertyChanged(nameof(SearchText));
            View.Refresh();
        }

        if (_hub.Latest is { } frame)
        {
            Apply(frame);
        }

        if (_byKey.TryGetValue(key, out var row))
        {
            Selected = row;
        }
    }

    /// <summary>Tells live sorting which property to watch after the user clicks a column header.</summary>
    public void SetLiveSortProperty(string property)
    {
        View.LiveSortingProperties.Clear();
        View.LiveSortingProperties.Add(property);
    }

    protected override void OnActivated()
    {
        if (_hub.Latest is { } frame)
        {
            Apply(frame);
        }
    }

    private bool Matches(object item)
    {
        var row = (ProcessRowViewModel)item;
        var filter = (ProcessFilter)Math.Clamp(_filterIndex, 0, 2);
        if (!ProcessGraph.MatchesFilter(row.Info, filter))
        {
            return false;
        }

        if (_searchText.Length == 0)
        {
            return true;
        }

        string term = _searchText.Trim();
        return row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.Pid.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.Ordinal)
            || (row.Info.ExecutablePath?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void Apply(MonitoringFrame frame)
    {
        // Update rows in place; only genuinely new or exited processes touch the collection.
        var seen = new HashSet<ProcessKey>(frame.Snapshot.Count);
        foreach (var p in frame.Snapshot.Processes)
        {
            seen.Add(p.Key);
            if (_byKey.TryGetValue(p.Key, out var row))
            {
                row.Update(p);
            }
            else
            {
                row = new ProcessRowViewModel(p);
                _byKey[p.Key] = row;
                Rows.Add(row);
            }
        }

        if (seen.Count != _byKey.Count)
        {
            for (int i = Rows.Count - 1; i >= 0; i--)
            {
                if (!seen.Contains(Rows[i].Key))
                {
                    _byKey.Remove(Rows[i].Key);
                    Rows.RemoveAt(i);
                }
            }
        }

        UpdateSummary();
        UpdateDetail();
    }

    private void UpdateSummary()
        => Summary = View.Count == Rows.Count
            ? $"{Rows.Count:N0} processes"
            : $"{View.Count:N0} of {Rows.Count:N0} processes";

    private void UpdateDetail()
    {
        if (_hub.Latest is { } frame)
        {
            Detail.Update(_selectedKey, frame.Snapshot, frame.Hierarchy);
        }
        else
        {
            Detail.Clear();
        }

        OnPropertyChanged(nameof(HasSelection));
    }
}
