using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SqlVersionControl.Converters;

/// <summary>
/// Converts null values to the string "NULL" for display in result grids.
/// Non-null values pass through as their ToString() representation.
/// </summary>
public class NullDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value == null ? "NULL" : value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && (s == "NULL" || string.IsNullOrEmpty(s)))
            return null;
        return value;
    }
}

/// <summary>
/// Returns FontStyle.Italic for null values, FontStyle.Normal otherwise.
/// </summary>
public class NullFontStyleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value == null ? FontStyle.Italic : FontStyle.Normal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Returns a muted grey brush for null values, or the default text brush otherwise.
/// </summary>
public class NullForegroundConverter : IValueConverter
{
    private static readonly IBrush NullBrush = new SolidColorBrush(Color.FromRgb(102, 102, 102)); // #666666

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Return null for non-null values so the DataGrid default foreground applies
        return value == null ? NullBrush : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
