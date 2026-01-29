using System.Globalization;

namespace ZCM.Converters;

public class BoolToColorConverter : IValueConverter
{
    public Color OutgoingColor { get; set; } = Color.FromArgb("#6C63FF");
    public Color IncomingColor { get; set; } = Color.FromArgb("#2A2A2A");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (bool)value ? OutgoingColor : IncomingColor;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

