using System.Windows.Input;
using Phantom.App.Controls;
using Phantom.App.Mvvm;
using Phantom.App.Services;
using Phantom.Core.Models;
using Phantom.Core.Monitoring;
using Phantom.SystemMap;

namespace Phantom.App.ViewModels;

public sealed class SystemMapViewModel : PageViewModel
{
    private readonly UiFrameHub _hub;
    private readonly HashSet<ProcessKey> _collapsed = [];
    private MapScene? _scene;
    private ProcessKey? _selectedKey;
    private string _searchText = "";
    private int _filterIndex;
    private string _summary = "";
    private bool _building;
    private bool _rebuildRequested;
    private bool _inspectorOpen = true;

    public SystemMapViewModel(UiFrameHub hub)
    {
        _hub = hub;
        CollapseAllCommand = new RelayCommand(CollapseAll);
        ExpandAllCommand = new RelayCommand(() =>
        {
            _collapsed.Clear();
            Rebuild();
        });
        ToggleSelectedCommand = new RelayCommand(() =>
        {
            if (SelectedKey is { } key)
            {
                ToggleCollapse(key);
            }
        }, () => SelectedKey is { } key && _scene?.Graph.Find(key) is { ChildCount: > 0 });
        SelectProcessCommand = new RelayCommand(p =>
        {
            if (p is ProcessKey key)
            {
                SelectedKey = key;
            }
        });
        ToggleInspectorCommand = new RelayCommand(() => InspectorOpen = !InspectorOpen);
        hub.FrameArrived += (_, _) =>
        {
            if (IsActive)
            {
                Rebuild();
            }
        };
    }

    public override string Title => "System Map";
    public override string Subtitle => "The live process ecosystem";
    public override string Icon => "";

    public ProcessDetailViewModel Detail { get; } = new();
    public ICommand CollapseAllCommand { get; }
    public ICommand ExpandAllCommand { get; }
    public ICommand ToggleSelectedCommand { get; }
    public ICommand SelectProcessCommand { get; }
    public ICommand ToggleInspectorCommand { get; }

    public MapScene? Scene { get => _scene; private set => Set(ref _scene, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public bool InspectorOpen { get => _inspectorOpen; set => Set(ref _inspectorOpen, value); }

    public ProcessKey? SelectedKey
    {
        get => _selectedKey;
        set
        {
            if (Set(ref _selectedKey, value))
            {
                UpdateDetail();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => Set(ref _searchText, value ?? "");
    }

    /// <summary>0 = all, 1 = current user, 2 = system.</summary>
    public int FilterIndex
    {
        get => _filterIndex;
        set
        {
            if (Set(ref _filterIndex, value))
            {
                Rebuild();
            }
        }
    }

    public void ToggleCollapse(ProcessKey key)
    {
        if (!_collapsed.Remove(key))
        {
            _collapsed.Add(key);
        }

        Rebuild();
    }

    private void CollapseAll()
    {
        if (_scene is null)
        {
            return;
        }

        // Collapse every tree down to its root: one node per process family.
        foreach (var root in _scene.Graph.Roots)
        {
            if (root.ChildCount > 0)
            {
                _collapsed.Add(root.Key);
            }
        }

        Rebuild();
    }

    protected override void OnActivated() => Rebuild();

    /// <summary>
    /// Rebuilds the scene off the UI thread. Requests arriving while a build is running are
    /// merged into a single follow-up build, and layout is recomputed only when topology changed.
    /// </summary>
    private async void Rebuild()
    {
        if (_hub.Latest is not { } frame)
        {
            return;
        }

        if (_building)
        {
            _rebuildRequested = true;
            return;
        }

        _building = true;
        try
        {
            do
            {
                _rebuildRequested = false;
                frame = _hub.Latest ?? frame;
                var processes = frame.Snapshot.Processes;
                var filter = (ProcessFilter)Math.Clamp(_filterIndex, 0, 2);
                var collapsed = new HashSet<ProcessKey>(_collapsed);
                var previous = _scene;

                var scene = await Task.Run(() =>
                {
                    var graph = ProcessGraph.Build(processes, filter, collapsed);
                    var layout = previous is not null && previous.Graph.TopologySignature == graph.TopologySignature
                        ? previous.Layout
                        : RadialClusterLayout.Compute(graph);
                    return new MapScene(graph, layout);
                });

                Scene = scene;
                // Forget collapse state for processes that no longer exist.
                _collapsed.RemoveWhere(k => frame.Snapshot.FindByPid(k.Pid)?.Key != k);
                int trees = scene.Graph.Roots.Count;
                int hidden = scene.Graph.Nodes.Sum(n => n.HiddenDescendantCount);
                Summary = $"{scene.Graph.Nodes.Count:N0} nodes · {trees:N0} trees"
                    + (hidden > 0 ? $" · {hidden:N0} hidden in collapsed groups" : "");
                UpdateDetail();
            }
            while (_rebuildRequested);
        }
        catch (Exception)
        {
            // A failed build keeps the previous scene; the next frame will try again.
        }
        finally
        {
            _building = false;
        }
    }

    private void UpdateDetail()
    {
        if (_hub.Latest is { } frame)
        {
            Detail.Update(_selectedKey, frame.Snapshot, frame.Hierarchy);
        }
    }
}
