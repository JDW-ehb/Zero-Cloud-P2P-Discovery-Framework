using System.Globalization;

namespace ZCM.Converters;

public class OnlineStatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isOnline = (bool)value;
        return isOnline
            ? Color.FromArgb("#27AE60")
            : Color.FromArgb("#E74C3C"); 
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}