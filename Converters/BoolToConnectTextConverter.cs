using System.Globalization;
using Avalonia.Data.Converters;

namespace SqlVersionControl.Converters;

public class BoolToConnectTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? "Connecting..." : "Connect";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
