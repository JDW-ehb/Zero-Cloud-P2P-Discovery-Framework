using Microsoft.EntityFrameworkCore;
using ZCL.Models;
using System.Diagnostics;

namespace ZCM.Pages;

public partial class PeerDetailsPage : ContentPage
{
    private MainPage.PeerNodeCard? _card;

    public PeerDetailsPage()
    {
        InitializeComponent();
        this.BackgroundColor = new Color(0, 0, 0, 0.6f);
    }

    public MainPage.PeerNodeCard? Card
    {
        get => _card;
        set
        {
            _card = value;
            BindingContext = _card;
        }
    }

    private async void OnCloseClicked(object sender, EventArgs e)
        => await Navigation.PopModalAsync(false);

    private async void OnBackdropTapped(object sender, EventArgs e)
        => await Navigation.PopModalAsync(false);

    private async void OnServiceClicked(object sender, EventArgs e)
    {
        if (sender is not Button btn || _card is null)
            return;

        var serviceName = btn.CommandParameter as string;

        if (string.IsNullOrEmpty(serviceName))
        {
            if (btn.Text == "LLM")
                serviceName = _card.Services.FirstOrDefault(s => s.StartsWith("LLMChat", StringComparison.OrdinalIgnoreCase));
            else
                serviceName = btn.Text;
        }

        if (string.IsNullOrEmpty(serviceName))
        {
            Debug.WriteLine("ServiceClicked: No service name found");
            return;
        }

        Debug.WriteLine($"ServiceClicked: {serviceName}");

        // Map to the local announced service name
        var localServiceName = serviceName switch
        {
            var s when s.StartsWith("Messaging", StringComparison.OrdinalIgnoreCase) => "Messaging",
            var s when s.StartsWith("Filesharing", StringComparison.OrdinalIgnoreCase) => "FileSharing",
            var s when s.StartsWith("LLMChat", StringComparison.OrdinalIgnoreCase) => "LLMChat",
            _ => null
        };

        // Check if the local peer has this service enabled
        if (localServiceName != null)
        {
            using var scope = ServiceHelper.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

            var isEnabled = await db.AnnouncedServiceSettings
                .AnyAsync(s => s.ServiceName == localServiceName && s.IsEnabled);

            if (!isEnabled)
            {
                await DisplayAlert(
                    "Service Disabled",
                    $"{localServiceName} is not enabled on your node. Enable it in Settings first.",
                    "OK");
                return;
            }
        }

        var peer = _card.ToPeerNode();

        var nav = Shell.Current.Navigation;

        await nav.PopModalAsync(false);
        await nav.PopModalAsync(false);

        if (serviceName.StartsWith("Messaging", StringComparison.OrdinalIgnoreCase))
        {
            await Shell.Current.GoToAsync(nameof(MessagingPage),
                new Dictionary<string, object> { { "Peer", peer } });
        }
        else if (serviceName.StartsWith("Filesharing", StringComparison.OrdinalIgnoreCase))
        {
            await Shell.Current.GoToAsync(nameof(FileSharingPage),
                new Dictionary<string, object> { { "Peer", peer } });
        }
        else if (serviceName.StartsWith("LLMChat", StringComparison.OrdinalIgnoreCase))
        {
            await Shell.Current.GoToAsync(nameof(LLMChatPage),
                new Dictionary<string, object> { { "Peer", peer } });
        }
        else
        {
            Debug.WriteLine($"ServiceClicked: Unknown service type: {serviceName}");
        }
    }
}