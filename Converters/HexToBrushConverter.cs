using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SqlVersionControl.Converters;

public class HexToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
        {
            try
            {
                return new SolidColorBrush(Color.Parse(hex));
            }
            catch { }
        }
        return new SolidColorBrush(Color.Parse("#88a1bb"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
