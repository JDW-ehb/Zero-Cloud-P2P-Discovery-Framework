using System.Runtime.CompilerServices;
using ZCM.Controls;
using ZCM.Pages;

namespace ZCM
{
    public partial class AppShell : Shell
    {
        private bool _intercepting;

        public AppShell()
        {
            InitializeComponent();

            Routing.RegisterRoute(nameof(MessagingPage), typeof(MessagingPage));
            Routing.RegisterRoute(nameof(LLMChatPage), typeof(LLMChatPage));
            Routing.RegisterRoute(nameof(FileSharingPage), typeof(FileSharingPage));
            Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
        }

        protected override void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            base.OnPropertyChanged(propertyName);

            if (propertyName == nameof(FlyoutIsPresented) && FlyoutIsPresented && !_intercepting)
            {
                _intercepting = true;
                FlyoutIsPresented = false;
                _intercepting = false;

                if (CurrentPage is IDrawerPage { PageDrawer: not null } drawerPage)
                {
                    drawerPage.PageDrawer.IsOpen = !drawerPage.PageDrawer.IsOpen;
                }
            }
        }

        // -------------------------
        // Navigation Helpers
        // -------------------------

        private async Task NavigateIfNotCurrentAsync<TPage>(string route)
        {
            FlyoutIsPresented = false;

            if (CurrentPage is TPage)
                return;

            await GoToAsync(route);
            LogNavigationStack();
        }

        // -------------------------
        // Buttons
        // -------------------------

        private async void DashboardButton_Clicked(object sender, EventArgs e)
        {
            await NavigateIfNotCurrentAsync<MainPage>("//MainPage");
        }

        private async void MessagingButton_Clicked(object sender, EventArgs e)
        {
            await NavigateIfNotCurrentAsync<MessagingPage>(nameof(MessagingPage));
        }

        private async void LlmButton_Clicked(object sender, EventArgs e)
        {
            await NavigateIfNotCurrentAsync<LLMChatPage>(nameof(LLMChatPage));
        }

        private async void ShareButton_Clicked(object sender, EventArgs e)
        {
            await NavigateIfNotCurrentAsync<FileSharingPage>(nameof(FileSharingPage));
        }

        private async void SettingsButton_Clicked(object sender, EventArgs e)
        {
            await NavigateIfNotCurrentAsync<SettingsPage>(nameof(SettingsPage));
        }

        private void ExitButton_Clicked(object sender, EventArgs e)
        {
#if WINDOWS
            var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
            if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
            {
                nativeWindow.Close();
            }
#else
            Application.Current?.Quit();
#endif
        }

        // -------------------------
        // Logging
        // -------------------------

        private void LogNavigationStack()
        {
            var stack = Navigation.NavigationStack;

            System.Diagnostics.Debug.WriteLine("---- Navigation Stack ----");

            foreach (var page in stack)
                System.Diagnostics.Debug.WriteLine(page.GetType().Name);

            System.Diagnostics.Debug.WriteLine("--------------------------");
        }
    }
}