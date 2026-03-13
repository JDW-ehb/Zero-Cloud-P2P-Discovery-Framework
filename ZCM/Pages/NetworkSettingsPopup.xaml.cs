using ZCM.ViewModels;
using ZCL.API;

namespace ZCM.Pages;

public partial class NetworkSettingsPopup : ContentPage
{
    private readonly SettingsViewModel _vm;

    // temporary editable values

    public string TempDiscoveryPort { get; set; } = "";
    public string TempMulticastAddress { get; set; } = "";
    public string TempDiscoveryTimeout { get; set; } = "";

    public NetworkSettingsPopup(SettingsViewModel vm)
    {
        InitializeComponent();

        _vm = vm;

        // copy values from ViewModel
        TempDiscoveryPort = vm.DiscoveryPort.ToString();
        TempMulticastAddress = vm.MulticastAddress;
        TempDiscoveryTimeout = vm.DiscoveryTimeoutMSText;

        BindingContext = this;
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        // commit values to ViewModel

        if (int.TryParse(TempDiscoveryPort, out var port))
            _vm.DiscoveryPort = port;

        _vm.MulticastAddress = TempMulticastAddress;
        _vm.DiscoveryTimeoutMSText = TempDiscoveryTimeout;

        // immediately persist to config

        Config.Instance.DiscoveryPort = _vm.DiscoveryPort;
        Config.Instance.MulticastAddress = _vm.MulticastAddress;
        Config.Instance.DiscoveryTimeoutMS = _vm.DiscoveryTimeoutMS;

        Config.Instance.Save();

        await Navigation.PopModalAsync(false);
    }

    private async void OnBackClicked(object sender, EventArgs e)
    {
        // discard changes
        await Navigation.PopModalAsync(false);
    }

    private async void OnBackdropTapped(object sender, EventArgs e)
    {
        await Navigation.PopModalAsync(false);
    }
}