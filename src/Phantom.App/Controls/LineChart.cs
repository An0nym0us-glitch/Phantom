using System.Windows;
using System.Windows.Media;

namespace Phantom.App.Controls;

/// <summary>
/// Lightweight time-series chart drawn directly with a <see cref="DrawingContext"/>.
/// Up to two series share one scale. NaN entries are gaps (unavailable samples) and are
/// never drawn as zero. The newest value sits at the right edge; a short history is
/// right-aligned so the line grows in from the right like a strip chart.
/// </summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = Register<IReadOnlyList<double>?>(nameof(Values), null);
    public static readonly DependencyProperty Values2Property = Register<IReadOnlyList<double>?>(nameof(Values2), null);
    public static readonly DependencyProperty StrokeProperty = Register<Brush?>(nameof(Stroke), Brushes.DodgerBlue);
    public static readonly DependencyProperty Stroke2Property = Register<Brush?>(nameof(Stroke2), Brushes.Orange);
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), double.NaN);
    public static readonly DependencyProperty CapacityProperty = Register(nameof(Capacity), 0);
    public static readonly DependencyProperty ShowGridProperty = Register(nameof(ShowGrid), true);
    public static readonly DependencyProperty FillAreaProperty = Register(nameof(FillArea), true);
    public static readonly DependencyProperty GridBrushProperty = Register<Brush?>(nameof(GridBrush), null);

    private static DependencyProperty Register<T>(string name, T defaultValue)
        => DependencyProperty.Register(name, typeof(T), typeof(LineChart),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<double>? Values2
    {
        get => (IReadOnlyList<double>?)GetValue(Values2Property);
        set => SetValue(Values2Property, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Stroke2
    {
        get => (Brush?)GetValue(Stroke2Property);
        set => SetValue(Stroke2Property, value);
    }

    /// <summary>Top of the scale. NaN (the default) scales to the largest visible value.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>Number of horizontal slots. 0 stretches the available values across the full width.</summary>
    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public bool ShowGrid
    {
        get => (bool)GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public bool FillArea
    {
        get => (bool)GetValue(FillAreaProperty);
        set => SetValue(FillAreaProperty, value);
    }

    public Brush? GridBrush
    {
        get => (Brush?)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2)
        {
            return;
        }

        // A transparent backdrop keeps the whole area hit-testable for tooltips.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        if (ShowGrid && GridBrush is { } gridBrush)
        {
            var pen = new Pen(gridBrush, 1);
            pen.Freeze();
            for (int i = 1; i < 4; i++)
            {
                double y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            }
        }

        var a = Values;
        var b = Values2;
        double max = Maximum;
        if (double.IsNaN(max) || max <= 0)
        {
            max = Math.Max(PeakOf(a), PeakOf(b));
            max = max <= 0 ? 1 : max * 1.15;
        }

        DrawSeries(dc, b, Stroke2, max, w, h);
        DrawSeries(dc, a, Stroke, max, w, h);
    }

    private static double PeakOf(IReadOnlyList<double>? values)
    {
        double peak = 0;
        if (values is not null)
        {
            for (int i = 0; i < values.Count; i++)
            {
                double v = values[i];
                if (!double.IsNaN(v) && v > peak)
                {
                    peak = v;
                }
            }
        }

        return peak;
    }

    private void DrawSeries(DrawingContext dc, IReadOnlyList<double>? values, Brush? stroke, double max, double w, double h)
    {
        if (values is null || values.Count == 0 || stroke is null)
        {
            return;
        }

        int slots = Math.Max(Capacity, values.Count);
        double step = slots > 1 ? w / (slots - 1) : w;
        int offset = slots - values.Count;
        const double inset = 1.5;
        double usable = h - (2 * inset);

        double X(int i) => (offset + i) * step;
        double Y(double v) => inset + (usable * (1 - Math.Clamp(v / max, 0, 1)));

        var line = new StreamGeometry();
        StreamGeometry? area = FillArea ? new StreamGeometry() : null;
        using (var lc = line.Open())
        using (var ac = area?.Open())
        {
            int runStart = -1;
            for (int i = 0; i <= values.Count; i++)
            {
                bool valid = i < values.Count && !double.IsNaN(values[i]);
                if (valid)
                {
                    var p = new Point(X(i), Y(values[i]));
                    if (runStart < 0)
                    {
                        runStart = i;
                        lc.BeginFigure(p, false, false);
                        ac?.BeginFigure(new Point(p.X, h), true, true);
                        ac?.LineTo(p, false, false);
                    }
                    else
                    {
                        lc.LineTo(p, true, true);
                        ac?.LineTo(p, false, false);
                    }
                }
                else if (runStart >= 0)
                {
                    if (i - runStart == 1)
                    {
                        // A single isolated sample still deserves a visible mark.
                        lc.LineTo(new Point(X(runStart) + 0.01, Y(values[runStart])), true, true);
                    }

                    ac?.LineTo(new Point(X(i - 1), h), false, false);
                    runStart = -1;
                }
            }
        }

        line.Freeze();
        if (area is not null)
        {
            area.Freeze();
            var fill = stroke.Clone();
            fill.Opacity = 0.13;
            fill.Freeze();
            dc.DrawGeometry(fill, null, area);
        }

        var pen = new Pen(stroke, 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}
