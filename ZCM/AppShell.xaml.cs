using ZCM.Pages;

namespace ZCM
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();

            Routing.RegisterRoute(nameof(PeerDetailsPage), typeof(PeerDetailsPage));
            Routing.RegisterRoute(nameof(MessagingPage), typeof(MessagingPage));
            Routing.RegisterRoute(nameof(LLMChatPage), typeof(LLMChatPage));
            Routing.RegisterRoute(nameof(FileSharingPage), typeof(FileSharingPage));
            Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
        }
    }
}
