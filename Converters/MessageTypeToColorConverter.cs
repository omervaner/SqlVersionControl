using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SqlVersionControl.Models;

namespace SqlVersionControl.Converters;

public class MessageTypeToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MessageType type)
            return GetBrush("TextPrimary");

        var resourceKey = type switch
        {
            MessageType.Error => "WarningSeverityCritical",
            MessageType.Timing => "TextSecondary",
            _ => "TextPrimary"
        };

        return GetBrush(resourceKey);
    }

    private static IBrush GetBrush(string key)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out var resource) == true
            && resource is IBrush brush)
            return brush;
        return Brushes.White;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
