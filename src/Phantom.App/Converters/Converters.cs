using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Phantom.App.Converters;

/// <summary>Visible when the value is true / non-null / non-empty. Pass "invert" as the parameter to reverse.</summary>
public sealed class VisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            null => false,
            _ => true,
        };

        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Converts a UTC timestamp to local display text. The parameter is an optional format string.</summary>
public sealed class LocalTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime utc)
        {
            return Phantom.Core.Metrics.Format.Unavailable;
        }

        return utc.ToLocalTime().ToString(parameter as string ?? "yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Two-way bridge between an integer index and a radio button; the parameter is the button's index.</summary>
public sealed class IndexToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int index && int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int wanted) && index == wanted;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int wanted)
            ? wanted
            : Binding.DoNothing;
}

/// <summary>Maps 0-100 (or 0-1 with parameter "unit") to a 0-1 scale factor for meter bars.</summary>
public sealed class PercentToScaleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double v = value is double d && double.IsFinite(d) ? d : 0;
        return Math.Clamp(parameter as string == "unit" ? v : v / 100.0, 0, 1);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
