using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ZCM.Converters
{
    public class BoolToLayoutConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (bool)value ? LayoutOptions.End : LayoutOptions.Start;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

}
