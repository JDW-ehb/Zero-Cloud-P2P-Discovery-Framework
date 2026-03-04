using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace ZCM.Converters
{
    public class ServiceNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string serviceName)
            {
                // If the service starts with "LLMChat", return just "LLM"
                // Handles formats like "LLMChat (phi-latest)" or "LLMChat"
                if (serviceName.StartsWith("LLMChat", StringComparison.OrdinalIgnoreCase))
                {
                    return "LLM";
                }
                return serviceName;
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}