using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PyreMedia.Core.Organizing;

namespace PyreMedia.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var b = value is bool v && v;
        if (p is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
            b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is not bool b || !b;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => value is not bool b || !b;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is null || (value is string s && string.IsNullOrWhiteSpace(s))
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Maps the banner's error flag onto an InfoBar severity.</summary>
public sealed class BoolToSeverityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is bool b && b
            ? Wpf.Ui.Controls.InfoBarSeverity.Warning
            : Wpf.Ui.Controls.InfoBarSeverity.Success;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Colours preview rows: green = will change, amber = needs attention, dim = no-op.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Change = new(Color.FromRgb(0x6C, 0xC6, 0x8A));
    private static readonly SolidColorBrush Problem = new(Color.FromRgb(0xE8, 0xB3, 0x39));
    private static readonly SolidColorBrush Neutral = new(Color.FromRgb(0x8A, 0x8A, 0x8A));

    static StatusToBrushConverter()
    {
        Change.Freeze();
        Problem.Freeze();
        Neutral.Freeze();
    }

    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        PlanStatus.Change => Change,
        PlanStatus.Problem => Problem,
        _ => Neutral
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}
