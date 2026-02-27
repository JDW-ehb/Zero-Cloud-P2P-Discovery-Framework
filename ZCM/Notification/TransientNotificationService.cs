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
            var window = Application.Current?.Windows.FirstOrDefault();
            if (window?.Page is not Shell shell)
                return;

            var currentPage = shell.CurrentPage as ContentPage;
            if (currentPage == null)
                return;

            // Get actual visual root container of the page
            var pageRoot = currentPage.Content as Layout;
            if (pageRoot == null)
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
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Start,
                Content = new Label
                {
                    Text = message,
                    TextColor = Colors.White,
                    LineBreakMode = LineBreakMode.WordWrap
                },
                Opacity = 0
            };

            // Absolute positioning using Translation
            pageRoot.Children.Add(toast);

            await Task.Delay(10); // allow layout

            var size = toast.Measure(double.PositiveInfinity, double.PositiveInfinity);

            toast.TranslationX = pageRoot.Width - size.Width - 32;
            toast.TranslationY = pageRoot.Height - size.Height - 32;

            await toast.FadeTo(1, 200);
            await Task.Delay(durationMs);
            await toast.FadeTo(0, 200);

            pageRoot.Children.Remove(toast);
        });
    }
}