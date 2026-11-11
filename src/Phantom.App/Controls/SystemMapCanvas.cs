using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Phantom.Core.Metrics;
using Phantom.Core.Models;
using Phantom.SystemMap;

namespace Phantom.App.Controls;

/// <summary>What the map draws: a graph, its layout, and whether nodes or edges changed since the last scene.</summary>
/// <param name="Refit">True when the user changed what is shown (filter, collapse), so the camera should reframe.</param>
public sealed record MapScene(ProcessGraph Graph, GraphLayout Layout, bool Refit = false);

/// <summary>
/// Custom-rendered, interactive process map.
/// <para>
/// Performance model: the whole scene is drawn in one <see cref="OnRender"/> pass (no per-node
/// visuals), brushes and pens are frozen and shared, label layouts are cached, and a per-frame
/// render loop runs only while something is animating. A metrics-only refresh costs a single repaint.
/// </para>
/// </summary>
public sealed class SystemMapCanvas : FrameworkElement
{
    private const double MinZoom = 0.04;
    private const double MaxZoom = 4;
    private const double LabelZoomThreshold = 0.7;
    private const int HeatSteps = 24;
    private const double MinNodePixels = 3;

    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene), typeof(MapScene), typeof(SystemMapCanvas),
        new FrameworkPropertyMetadata(null, (d, e) => ((SystemMapCanvas)d).OnSceneChanged((MapScene?)e.OldValue, (MapScene?)e.NewValue)));

    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(
        nameof(SelectedKey), typeof(ProcessKey?), typeof(SystemMapCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((SystemMapCanvas)d).OnHighlightChanged()));

    public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(
        nameof(SearchText), typeof(string), typeof(SystemMapCanvas),
        new FrameworkPropertyMetadata("", (d, _) => ((SystemMapCanvas)d).OnHighlightChanged()));

    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface LabelTypefaceBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x13, 0x15, 0x19)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEB)));
    private static readonly Brush TextDimBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0x9E)));
    private static readonly Brush TooltipBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0x23, 0x27, 0x2E)));
    private static readonly Pen TooltipPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x49)), 1));
    private static readonly Pen EdgePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x39, 0x42)), 1));
    private static readonly Pen EdgeDimPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x2C)), 1));
    private static readonly Pen EdgeHighlightPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF)), 1.6));
    private static readonly Pen ClusterPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x29)), 1));
    private static readonly Pen SelectionPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEB)), 2));
    private static readonly Pen MatchPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0xA9, 0x3B)), 2));
    private static readonly Pen UserRingPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xC9, 0xD1, 0xDB)), 1));
    private static readonly Pen CollapsedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x9B, 0xA3, 0xAE)), 1) { DashStyle = new DashStyle([2, 2], 0) });
    private static readonly Brush[] HeatBrushes = BuildHeat(1.0);
    private static readonly Brush[] HeatBrushesDim = BuildHeat(0.28);

    private readonly Dictionary<ProcessKey, Point> _positions = [];
    private readonly Dictionary<ProcessKey, Vector> _manualOffsets = [];
    private readonly Dictionary<string, (FormattedText Text, double MaxWidth)> _labelCache = new(StringComparer.Ordinal);
    private HashSet<ProcessKey> _lineage = [];
    private HashSet<ProcessKey> _matches = [];

    private double _zoom = 1;
    private Vector _offset;
    private double _targetZoom = 1;
    private Vector _targetOffset;
    private bool _viewAnimating;
    private bool _nodesAnimating;
    private bool _renderHooked;
    private bool _hasFitted;

    private ProcessKey? _hover;
    private Point _mouse;
    private Point _dragStart;
    private bool _panning;
    private bool _moved;
    private ProcessKey? _dragNode;

    public SystemMapCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Unloaded += (_, _) => UnhookRendering();
    }

    public MapScene? Scene
    {
        get => (MapScene?)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public ProcessKey? SelectedKey
    {
        get => (ProcessKey?)GetValue(SelectedKeyProperty);
        set => SetValue(SelectedKeyProperty, value);
    }

    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    /// <summary>Raised when a node is double-clicked (used to expand or collapse its subtree).</summary>
    public event Action<ProcessKey>? NodeActivated;

    /// <summary>Raised when the zoom level changes.</summary>
    public event Action<double>? ZoomChanged;

    public double Zoom => _zoom;
    public int MatchCount => _matches.Count;

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Brush[] BuildHeat(double opacity)
    {
        // Idle slate -> accent blue -> amber -> red, as CPU share rises.
        (double At, Color Color)[] stops =
        [
            (0.00, Color.FromRgb(0x3D, 0x47, 0x54)),
            (0.12, Color.FromRgb(0x4C, 0x9A, 0xFF)),
            (0.45, Color.FromRgb(0xE0, 0xA9, 0x3B)),
            (1.00, Color.FromRgb(0xE5, 0x60, 0x4D)),
        ];
        var result = new Brush[HeatSteps + 1];
        for (int i = 0; i <= HeatSteps; i++)
        {
            double t = i / (double)HeatSteps;
            int s = 0;
            while (s < stops.Length - 2 && t > stops[s + 1].At)
            {
                s++;
            }

            double local = (t - stops[s].At) / (stops[s + 1].At - stops[s].At);
            Color a = stops[s].Color, b = stops[s + 1].Color;
            var color = Color.FromArgb(
                (byte)(255 * opacity),
                (byte)(a.R + ((b.R - a.R) * local)),
                (byte)(a.G + ((b.G - a.G) * local)),
                (byte)(a.B + ((b.B - a.B) * local)));
            result[i] = Frozen(new SolidColorBrush(color));
        }

        return result;
    }

    /// <summary>Maps a CPU share (0-100 of total capacity) onto the heat ramp; 40% and above is fully hot.</summary>
    private static int HeatIndex(double? cpu)
    {
        double t = Math.Clamp((cpu ?? 0) / 40.0, 0, 1);
        return (int)Math.Round(Math.Sqrt(t) * HeatSteps);
    }

    private static double NodeRadius(ProcessInfo p)
    {
        double megabytes = Math.Max(0, p.WorkingSetBytes) / (1024.0 * 1024.0);
        return Math.Clamp(5 + (Math.Sqrt(megabytes) * 0.55), 5, 22);
    }

    // ----- Scene and highlight state -----

    private void OnSceneChanged(MapScene? oldScene, MapScene? scene)
    {
        if (scene is null)
        {
            _positions.Clear();
            InvalidateVisual();
            return;
        }

        bool topologyChanged = oldScene is null
            || oldScene.Graph.TopologySignature != scene.Graph.TopologySignature
            || !ReferenceEquals(oldScene.Layout, scene.Layout);

        if (topologyChanged)
        {
            // Drop state for nodes that are gone; new nodes grow out from their parent.
            foreach (var key in _positions.Keys.Where(k => scene.Graph.Find(k) is null).ToList())
            {
                _positions.Remove(key);
                _manualOffsets.Remove(key);
            }

            foreach (var node in scene.Graph.Nodes)
            {
                if (!_positions.ContainsKey(node.Key))
                {
                    _positions[node.Key] = node.ParentKey is { } parent && _positions.TryGetValue(parent, out var origin)
                        ? origin
                        : TargetOf(scene, node.Key);
                }
            }

            _nodesAnimating = true;
            HookRendering();
        }

        RecomputeHighlights();
        if (!_hasFitted && ActualWidth > 0 && scene.Graph.Nodes.Count > 0)
        {
            SnapAllToTargets();
            FitToView(animate: false);
        }
        else if (scene.Refit && _hasFitted)
        {
            FitToView();
        }

        InvalidateVisual();
    }

    private void OnHighlightChanged()
    {
        RecomputeHighlights();
        InvalidateVisual();
    }

    private void RecomputeHighlights()
    {
        var scene = Scene;
        _lineage = scene is not null && SelectedKey is { } selected ? scene.Graph.GetLineage(selected) : [];
        _matches = [];
        if (scene is not null && !string.IsNullOrWhiteSpace(SearchText))
        {
            foreach (var node in scene.Graph.Nodes)
            {
                if (ProcessGraph.MatchesSearch(node.Process, SearchText))
                {
                    _matches.Add(node.Key);
                }
            }
        }
    }

    private Point TargetOf(MapScene scene, ProcessKey key)
    {
        var p = scene.Layout.Positions.TryGetValue(key, out var mp) ? new Point(mp.X, mp.Y) : default;
        return _manualOffsets.TryGetValue(key, out var offset) ? p + offset : p;
    }

    private void SnapAllToTargets()
    {
        if (Scene is not { } scene)
        {
            return;
        }

        foreach (var node in scene.Graph.Nodes)
        {
            _positions[node.Key] = TargetOf(scene, node.Key);
        }
    }

    // ----- Animation -----

    private void HookRendering()
    {
        if (!_renderHooked)
        {
            _renderHooked = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void UnhookRendering()
    {
        if (_renderHooked)
        {
            _renderHooked = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        bool busy = false;
        if (_nodesAnimating && Scene is { } scene)
        {
            bool moving = false;
            foreach (var node in scene.Graph.Nodes)
            {
                if (node.Key == _dragNode || !_positions.TryGetValue(node.Key, out var current))
                {
                    continue;
                }

                var target = TargetOf(scene, node.Key);
                var delta = target - current;
                if (delta.LengthSquared < 0.25)
                {
                    _positions[node.Key] = target;
                }
                else
                {
                    _positions[node.Key] = current + (delta * 0.2);
                    moving = true;
                }
            }

            _nodesAnimating = moving;
            busy |= moving;
        }

        if (_viewAnimating)
        {
            double dz = _targetZoom - _zoom;
            var dv = _targetOffset - _offset;
            if (Math.Abs(dz) < 0.001 && dv.LengthSquared < 0.25)
            {
                SetView(_targetZoom, _targetOffset);
                _viewAnimating = false;
            }
            else
            {
                SetView(_zoom + (dz * 0.22), _offset + (dv * 0.22));
                busy = true;
            }
        }

        InvalidateVisual();
        if (!busy)
        {
            UnhookRendering();
        }
    }

    private void SetView(double zoom, Vector offset)
    {
        bool zoomChanged = Math.Abs(zoom - _zoom) > 1e-6;
        _zoom = zoom;
        _offset = offset;
        if (zoomChanged)
        {
            ZoomChanged?.Invoke(_zoom);
        }
    }

    private void MoveView(double zoom, Vector offset, bool animate)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _targetZoom = zoom;
        _targetOffset = offset;
        if (animate)
        {
            _viewAnimating = true;
            HookRendering();
        }
        else
        {
            _viewAnimating = false;
            SetView(zoom, offset);
            InvalidateVisual();
        }
    }

    // ----- Public view commands -----

    /// <summary>Zooms and pans so the whole graph is visible.</summary>
    public void FitToView(bool animate = true)
    {
        if (Scene is not { } scene || scene.Graph.Nodes.Count == 0 || ActualWidth < 10 || ActualHeight < 10)
        {
            return;
        }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var node in scene.Graph.Nodes)
        {
            var p = TargetOf(scene, node.Key);
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        const double margin = 70;
        double zoom = Math.Min((ActualWidth - margin) / Math.Max(1, maxX - minX), (ActualHeight - margin) / Math.Max(1, maxY - minY));
        zoom = Math.Clamp(zoom, MinZoom, 1.4);
        var center = new Vector((minX + maxX) / 2, (minY + maxY) / 2);
        MoveView(zoom, new Vector(ActualWidth / 2, ActualHeight / 2) - (center * zoom), animate);
        _hasFitted = true;
    }

    /// <summary>Centres the view on one process, zooming in far enough to read its neighbourhood.</summary>
    public void FocusOn(ProcessKey key)
    {
        if (Scene is not { } scene || scene.Graph.Find(key) is null)
        {
            return;
        }

        var target = (Vector)TargetOf(scene, key);
        double zoom = Math.Max(_zoom, 1.0);
        MoveView(zoom, new Vector(ActualWidth / 2, ActualHeight / 2) - (target * zoom), animate: true);
    }

    /// <summary>Discards manual node placement and restores the computed layout.</summary>
    public void ResetLayout()
    {
        _manualOffsets.Clear();
        _nodesAnimating = true;
        HookRendering();
        FitToView();
    }

    public void ZoomBy(double factor) => ZoomAround(new Point(ActualWidth / 2, ActualHeight / 2), factor, animate: true);

    private void ZoomAround(Point anchor, double factor, bool animate)
    {
        double from = animate ? _targetZoom : _zoom;
        var fromOffset = animate && _viewAnimating ? _targetOffset : _offset;
        double zoom = Math.Clamp(from * factor, MinZoom, MaxZoom);
        var world = ((Vector)anchor - fromOffset) / from;
        MoveView(zoom, (Vector)anchor - (world * zoom), animate);
    }

    /// <summary>Selects and focuses the next search match after the current selection. Returns false if there are none.</summary>
    public bool FocusNextMatch()
    {
        if (Scene is not { } scene || _matches.Count == 0)
        {
            return false;
        }

        var ordered = scene.Graph.Nodes.Where(n => _matches.Contains(n.Key)).Select(n => n.Key).ToList();
        int index = SelectedKey is { } selected ? ordered.IndexOf(selected) : -1;
        var next = ordered[(index + 1) % ordered.Count];
        SelectedKey = next;
        FocusOn(next);
        return true;
    }

    // ----- Input -----

    private Point ToScreen(Point world) => new((world.X * _zoom) + _offset.X, (world.Y * _zoom) + _offset.Y);

    private ProcessKey? HitTest(Point screen)
    {
        if (Scene is not { } scene)
        {
            return null;
        }

        ProcessKey? best = null;
        double bestDistance = double.MaxValue;
        foreach (var node in scene.Graph.Nodes)
        {
            if (!_positions.TryGetValue(node.Key, out var world))
            {
                continue;
            }

            var p = ToScreen(world);
            double radius = Math.Max(NodeRadius(node.Process) * _zoom, MinNodePixels + 1) + 3;
            double d = (p - screen).LengthSquared;
            if (d <= radius * radius && d < bestDistance)
            {
                bestDistance = d;
                best = node.Key;
            }
        }

        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _dragStart = _mouse = e.GetPosition(this);
        _moved = false;
        var hit = HitTest(_mouse);

        if (e.ClickCount == 2)
        {
            if (hit is { } activated)
            {
                NodeActivated?.Invoke(activated);
            }
            else
            {
                FitToView();
            }

            e.Handled = true;
            return;
        }

        if (hit is { } key)
        {
            SelectedKey = key;
            _dragNode = key;
        }
        else
        {
            _panning = true;
            _viewAnimating = false;
        }

        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        var delta = position - _mouse;
        _mouse = position;

        if (IsMouseCaptured)
        {
            if (!_moved && (position - _dragStart).Length < 4)
            {
                return;
            }

            _moved = true;
            if (_dragNode is { } key && _positions.TryGetValue(key, out var current))
            {
                var worldDelta = delta / _zoom;
                _manualOffsets[key] = _manualOffsets.GetValueOrDefault(key) + worldDelta;
                _positions[key] = current + worldDelta;
            }
            else if (_panning)
            {
                _offset += delta;
                _targetOffset = _offset;
            }

            InvalidateVisual();
            return;
        }

        var hover = HitTest(position);
        if (hover != _hover || hover is not null)
        {
            _hover = hover;
            Cursor = hover is null ? Cursors.Arrow : Cursors.Hand;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        // A plain click on empty space clears the selection; a pan does not.
        if (_panning && !_moved)
        {
            SelectedKey = null;
        }

        _panning = false;
        _dragNode = null;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _panning = false;
        _dragNode = null;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover is not null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomAround(e.GetPosition(this), Math.Pow(1.18, e.Delta / 120.0), animate: false);
        _targetZoom = _zoom;
        _targetOffset = _offset;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus:
                ZoomBy(1.25);
                e.Handled = true;
                break;
            case Key.Subtract or Key.OemMinus:
                ZoomBy(0.8);
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0 or Key.Home:
                FitToView();
                e.Handled = true;
                break;
            case Key.Escape:
                SelectedKey = null;
                e.Handled = true;
                break;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_hasFitted && Scene is { Graph.Nodes.Count: > 0 })
        {
            SnapAllToTargets();
            FitToView(animate: false);
        }
    }

    // ----- Rendering -----

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (Scene is not { } scene || scene.Graph.Nodes.Count == 0)
        {
            var empty = new FormattedText("Waiting for process data…", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                LabelTypeface, 13, TextDimBrush, dpi);
            dc.DrawText(empty, new Point((width - empty.Width) / 2, (height - empty.Height) / 2));
            return;
        }

        var graph = scene.Graph;
        bool searching = !string.IsNullOrWhiteSpace(SearchText);
        bool selecting = _lineage.Count > 0;
        var view = new Rect(-60, -60, width + 120, height + 120);

        // Cluster outlines give each process tree a visible territory when zoomed out.
        if (_manualOffsets.Count == 0 && !_nodesAnimating)
        {
            foreach (var cluster in scene.Layout.Clusters)
            {
                if (cluster.NodeCount < 2)
                {
                    continue;
                }

                var center = ToScreen(new Point(cluster.Center.X, cluster.Center.Y));
                double r = (cluster.Radius - 14) * _zoom;
                if (r > 6 && view.IntersectsWith(new Rect(center.X - r, center.Y - r, 2 * r, 2 * r)))
                {
                    dc.DrawEllipse(null, ClusterPen, center, r, r);
                }
            }
        }

        // Edges are batched into two geometries: ordinary and highlighted.
        var edges = new StreamGeometry();
        var highlighted = new StreamGeometry();
        using (var ec = edges.Open())
        using (var hc = highlighted.Open())
        {
            foreach (var node in graph.Nodes)
            {
                if (node.ParentKey is not { } parentKey
                    || !_positions.TryGetValue(node.Key, out var childWorld)
                    || !_positions.TryGetValue(parentKey, out var parentWorld))
                {
                    continue;
                }

                var a = ToScreen(parentWorld);
                var b = ToScreen(childWorld);
                if (!view.Contains(a) && !view.Contains(b) && !view.IntersectsWith(new Rect(a, b)))
                {
                    continue;
                }

                bool lit = selecting && _lineage.Contains(node.Key) && _lineage.Contains(parentKey);
                var ctx = lit ? hc : ec;
                ctx.BeginFigure(a, false, false);
                ctx.LineTo(b, true, false);
            }
        }

        edges.Freeze();
        highlighted.Freeze();
        dc.DrawGeometry(null, searching || selecting ? EdgeDimPen : EdgePen, edges);
        dc.DrawGeometry(null, EdgeHighlightPen, highlighted);

        // Nodes, then labels on top so text is never hidden behind a neighbour.
        bool labelsByZoom = _zoom >= LabelZoomThreshold;
        double labelWidth = Math.Clamp(Math.Round(RadialClusterLayout.NodeSpacing * _zoom * 1.7 / 8) * 8, 56, 220);
        List<(GraphNode Node, Point Center, double Radius, int Priority)>? labels = null;

        foreach (var node in graph.Nodes)
        {
            if (!_positions.TryGetValue(node.Key, out var world))
            {
                continue;
            }

            var center = ToScreen(world);
            double radius = Math.Max(NodeRadius(node.Process) * _zoom, MinNodePixels);
            if (!view.Contains(center))
            {
                continue;
            }

            bool isMatch = searching && _matches.Contains(node.Key);
            bool inLineage = selecting && _lineage.Contains(node.Key);
            bool dim = searching ? !isMatch && !inLineage : selecting && !inLineage;
            int heat = HeatIndex(node.Process.Pid == 0 ? 0 : node.Process.CpuPercent);
            var fill = dim ? HeatBrushesDim[heat] : HeatBrushes[heat];

            Pen? ring = null;
            if (!dim && node.Process.Owner == ProcessOwnerKind.CurrentUser && radius > 3.5)
            {
                ring = UserRingPen;
            }

            dc.DrawEllipse(fill, ring, center, radius, radius);

            if (node.IsCollapsed)
            {
                dc.DrawEllipse(null, CollapsedPen, center, radius + 4, radius + 4);
            }

            if (isMatch)
            {
                dc.DrawEllipse(null, MatchPen, center, radius + 3, radius + 3);
            }

            if (node.Key == SelectedKey)
            {
                dc.DrawEllipse(null, SelectionPen, center, radius + 3, radius + 3);
            }

            // Lower number = more important. Important labels claim screen space first.
            int priority = node.Key == SelectedKey ? 0 : node.Key == _hover ? 1 : isMatch ? 2 : inLineage ? 3 : 4;
            if (priority < 4 || (labelsByZoom && !dim))
            {
                (labels ??= []).Add((node, center, radius, priority));
            }
        }

        if (labels is not null)
        {
            // Greedy placement: a label is skipped when it would overlap one already drawn, so
            // dense areas stay legible instead of turning into a smear of text.
            labels.Sort((x, y) => x.Priority.CompareTo(y.Priority));
            var occupied = new List<Rect>(Math.Min(labels.Count, 256));
            foreach (var (node, center, radius, priority) in labels)
            {
                string text = node.IsCollapsed ? $"{node.Process.Name}  +{node.HiddenDescendantCount}" : node.Process.Name;
                var label = GetLabel(text, priority < 4, labelWidth, dpi);
                var origin = new Point(center.X - (label.Width / 2), center.Y + radius + 3);
                var box = new Rect(origin.X - 3, origin.Y, label.Width + 6, label.Height);
                if (priority > 1)
                {
                    bool blocked = false;
                    for (int i = 0; i < occupied.Count; i++)
                    {
                        if (occupied[i].IntersectsWith(box))
                        {
                            blocked = true;
                            break;
                        }
                    }

                    if (blocked)
                    {
                        continue;
                    }
                }

                occupied.Add(box);
                dc.DrawText(label, origin);
            }
        }

        if (_hover is { } hoverKey && !IsMouseCaptured && graph.Find(hoverKey) is { } hovered)
        {
            DrawTooltip(dc, hovered, dpi, width, height);
        }
    }

    private FormattedText GetLabel(string text, bool strong, double maxWidth, double dpi)
    {
        string cacheKey = strong ? "*" + text : text;
        if (_labelCache.TryGetValue(cacheKey, out var cached) && Math.Abs(cached.MaxWidth - maxWidth) < 0.5)
        {
            return cached.Text;
        }

        if (_labelCache.Count > 4000)
        {
            _labelCache.Clear();
        }

        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            strong ? LabelTypefaceBold : LabelTypeface, 11, strong ? TextBrush : TextDimBrush, dpi)
        {
            MaxTextWidth = strong ? Math.Max(maxWidth, 180) : maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Left,
        };
        _labelCache[cacheKey] = (formatted, maxWidth);
        return formatted;
    }

    private void DrawTooltip(DrawingContext dc, GraphNode node, double dpi, double width, double height)
    {
        var p = node.Process;
        string line1 = $"{p.Name}  ·  PID {p.Pid}";
        string line2 = $"CPU {Format.Percent(p.CpuPercent, 1)}   Memory {Format.Bytes(p.WorkingSetBytes)}   Threads {p.ThreadCount}";
        string line3 = node.IsCollapsed
            ? $"{node.HiddenDescendantCount} hidden descendants · double-click to expand"
            : node.ChildCount > 0 ? $"{node.ChildCount} child processes · double-click to collapse" : "No child processes";

        var t1 = new FormattedText(line1, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelTypefaceBold, 12, TextBrush, dpi);
        var t2 = new FormattedText(line2, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelTypeface, 11.5, TextBrush, dpi);
        var t3 = new FormattedText(line3, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelTypeface, 11, TextDimBrush, dpi);

        double w = Math.Max(t1.Width, Math.Max(t2.Width, t3.Width)) + 20;
        double h = t1.Height + t2.Height + t3.Height + 22;
        double x = Math.Min(_mouse.X + 16, width - w - 6);
        double y = _mouse.Y + 18 + h > height ? _mouse.Y - h - 10 : _mouse.Y + 18;
        x = Math.Max(6, x);
        y = Math.Max(6, y);

        dc.DrawRoundedRectangle(TooltipBrush, TooltipPen, new Rect(x, y, w, h), 5, 5);
        dc.DrawText(t1, new Point(x + 10, y + 8));
        dc.DrawText(t2, new Point(x + 10, y + 11 + t1.Height));
        dc.DrawText(t3, new Point(x + 10, y + 14 + t1.Height + t2.Height));
    }
}
