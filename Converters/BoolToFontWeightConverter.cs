using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SqlVersionControl.Converters;

public class BoolToFontWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? FontWeight.SemiBold : FontWeight.Normal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
