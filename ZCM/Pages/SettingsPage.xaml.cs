using System.Diagnostics;
using ZCL.API;
using ZCM.Notifications;
using ZCL.Security;


namespace ZCM.Pages;

public partial class SettingsPage : ContentPage
{

    private readonly string _originalTlsSecret;

    private readonly Config _config;

    public SettingsPage()
    {
        InitializeComponent();
        _config = Config.Instance;
        _originalTlsSecret = _config.TlsSharedSecret;
        BindingContext = _config;
    }

    private async void SaveButton_Clicked(object sender, EventArgs e)
    {
        var baseDir = Config.Instance.AppDataDirectory;

        // Persist settings
        _config.Save();

        // If secret changed => invalidate identity cert so it gets regenerated
        if (_config.TlsSharedSecret != _originalTlsSecret)
        {
            TlsCertificateProvider.DeleteLocalIdentityCertificate(baseDir);

            // Optional: force-create immediately so you *see* the cert file right away
            _ = TlsCertificateProvider.LoadOrCreateIdentityCertificate(
                baseDirectory: baseDir,
                peerLabel: Config.Instance.PeerName);
        }

        await Navigation.PopModalAsync(false);

        await TransientNotificationService.ShowAsync(
            "Configuration saved successfully.",
            NotificationSeverity.Success,
            2000);
    }

    private async void OnCloseClicked(object sender, EventArgs e)
    {
        await Navigation.PopModalAsync(false);
    }

    private async void OnBackdropTapped(object sender, EventArgs e)
    {
        await Navigation.PopModalAsync(false);
    }

}