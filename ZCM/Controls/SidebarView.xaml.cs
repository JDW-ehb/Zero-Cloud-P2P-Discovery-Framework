using ZCM.Pages;

namespace ZCM.Controls;

public partial class SidebarView : ContentView
{
    public SidebarView()
    {
        InitializeComponent();
    }

    public DrawerHost? Host { get; set; }

    async Task NavigateAsync(string route)
    {
        if (Host is not null)
            Host.IsOpen = false;

        await Shell.Current.GoToAsync(route);
    }

    void DashboardButton_Clicked(object sender, EventArgs e) => _ = NavigateAsync("//MainPage");
    void MessagingButton_Clicked(object sender, EventArgs e) => _ = NavigateAsync(nameof(MessagingPage));
    void LlmButton_Clicked(object sender, EventArgs e) => _ = NavigateAsync(nameof(LLMChatPage));
    void ShareButton_Clicked(object sender, EventArgs e) => _ = NavigateAsync(nameof(FileSharingPage));

    async void SettingsButton_Clicked(object sender, EventArgs e)
    {
        if (Host is not null)
            Host.IsOpen = false;

        await Shell.Current.Navigation.PushModalAsync(new SettingsPage(), false);
    }

    async void DiscoveryButton_Clicked(object sender, EventArgs e)
    {
        if (Host is not null)
            Host.IsOpen = false;

        // Keep your existing DiscoveryPopup behavior
        if (Shell.Current.CurrentPage is MainPage main)
            await main.Navigation.PushModalAsync(new DiscoveryPopup(main), false);
        else
        {
            await Shell.Current.GoToAsync("//MainPage");
            if (Shell.Current.CurrentPage is MainPage mainAfterNav)
                await mainAfterNav.Navigation.PushModalAsync(new DiscoveryPopup(mainAfterNav), false);
        }
    }

    void ExitButton_Clicked(object sender, EventArgs e)
    {
#if ANDROID || IOS || MACCATALYST || WINDOWS
        Application.Current?.Quit();
#endif
    }
}