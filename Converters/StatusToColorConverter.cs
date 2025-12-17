using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SqlVersionControl.Converters;

public class StatusToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as string;
        return status switch
        {
            "Both" => Brushes.DodgerBlue,
            "Identical" => Brushes.DodgerBlue,
            "Modified" => Brushes.Orange,
            "Source Only" => Brushes.Green,
            "Target Only" => Brushes.OrangeRed,
            _ => Brushes.Gray
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
