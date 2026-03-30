using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SqlVersionControl.Converters;

/// <summary>
/// MultiValueConverter: takes (IconColor hex string, BadgeOpacity double) and returns
/// a SolidColorBrush with the icon color at the specified opacity.
/// </summary>
public class BadgeBackgroundConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return Brushes.Transparent;

        var hexColor = values[0] as string ?? "#888888";
        var opacity = values[1] is double d ? d : 0.08;

        try
        {
            var color = Color.Parse(hexColor);
            var alpha = (byte)(opacity * 255);
            return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        }
        catch
        {
            return Brushes.Transparent;
        }
    }
}
