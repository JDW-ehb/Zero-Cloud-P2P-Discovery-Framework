using System.Globalization;

namespace ZCM.Converters;

public class ChatTimestampConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime utc)
            return string.Empty;

        var local = utc.ToLocalTime();
        var now = DateTime.Now;

        if (local.Date == now.Date)
            return local.ToString("HH:mm");

        if (local.Date == now.Date.AddDays(-1))
            return $"Yesterday at {local:HH:mm}";

        return local.ToString("dd:MM:yyyy 'at' HH:mm");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
