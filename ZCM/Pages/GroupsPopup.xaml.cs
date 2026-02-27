using System.Security.Cryptography;
using ZCM.ViewModels;

namespace ZCM.Pages;

public partial class GroupsPopup : ContentPage
{
    private readonly SettingsViewModel _vm;

    public GroupsPopup(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    private async void OnCloseClicked(object sender, EventArgs e)
        => await Navigation.PopModalAsync(false);

    private async void OnBackdropTapped(object sender, EventArgs e)
        => await Navigation.PopModalAsync(false);

    private void OnAddGroupClicked(object sender, EventArgs e)
    {
        var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        _vm.Groups.Add(new TrustGroupDraftItem
        {
            Id = Guid.NewGuid(),
            Name = $"Group {_vm.Groups.Count + 1}",
            SecretHex = hex,
            IsEnabled = true,
            IsActiveLocal = _vm.Groups.Count == 0
        });

        if (_vm.Groups.Count == 1)
            _vm.SetActiveGroup(_vm.Groups[0].Id);
    }

    private void OnSetActiveClicked(object sender, EventArgs e)
    {
        if (sender is not Button btn)
            return;

        if (btn.BindingContext is not TrustGroupDraftItem item)
            return;

        _vm.SetActiveGroup(item.Id);
    }
}