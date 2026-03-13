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
            // Prefer the explicit overlay layer if the page provides one
            var layer = NotificationHost.Layer;
            if (layer == null)
                return;

            var background = severity switch
            {
                NotificationSeverity.Success => Color.FromArgb("#27AE60"),
                NotificationSeverity.Warning => Color.FromArgb("#E67E22"),
                NotificationSeverity.Error => Color.FromArgb("#C0392B"),
                _ => Color.FromArgb("#2A2A2A")
            };

            var toast = new Frame
            {
                BackgroundColor = background,
                CornerRadius = 14,
                Padding = new Thickness(18, 10),
                HasShadow = false,
                MaximumWidthRequest = 320,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.End,
                Margin = new Thickness(16),
                Content = new Label
                {
                    Text = message,
                    TextColor = Colors.White,
                    LineBreakMode = LineBreakMode.WordWrap
                },
                Opacity = 0,
                InputTransparent = true
            };

            // Ensure overlay behavior inside the layer grid
            Grid.SetRowSpan(toast, int.MaxValue);
            Grid.SetColumnSpan(toast, int.MaxValue);
            toast.ZIndex = 999;

            layer.Children.Add(toast);

            await toast.FadeTo(1, 200);
            await Task.Delay(durationMs);
            await toast.FadeTo(0, 200);

            layer.Children.Remove(toast);
        });
    }
}