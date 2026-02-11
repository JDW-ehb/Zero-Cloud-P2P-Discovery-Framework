using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;

namespace ZCM.Notifications;

public static class TransientNotificationService
{
    public static async Task ShowAsync(
        string message,
        NotificationSeverity severity = NotificationSeverity.Info,
        int durationMs = 5000)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = Shell.Current?.CurrentPage as ContentPage;
            if (page?.Content is not Layout layout)
                return;

            var background = severity switch
            {
                NotificationSeverity.Success => Color.FromArgb("#27AE60"),
                NotificationSeverity.Warning => Color.FromArgb("#E67E22"),
                NotificationSeverity.Error => Color.FromArgb("#C0392B"),
                _ => Color.FromArgb("#2A2A2A")
            };

            var container = new Grid
            {
                VerticalOptions = LayoutOptions.End,
                HorizontalOptions = LayoutOptions.End,
                Margin = new Thickness(0, 0, 24, 24)
            };

            var label = new Label
            {
                Text = message,
                BackgroundColor = background,
                TextColor = Colors.White,
                Padding = new Thickness(18, 10),
                Opacity = 0
            };

            container.Children.Add(label);
            layout.Children.Add(container);

            await label.FadeTo(1, 200);
            await Task.Delay(durationMs);
            await label.FadeTo(0, 200);

            layout.Children.Remove(container);
        });
    }
}
