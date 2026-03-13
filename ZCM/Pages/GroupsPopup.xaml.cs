using System.Security.Cryptography;
using ZCL.Models;
using ZCL.Security;
using ZCM.ViewModels;
using Microsoft.EntityFrameworkCore;

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

    private void OnCloseClicked(object sender, EventArgs e)
        => SafeClose();

    private void OnBackdropTapped(object sender, EventArgs e)
        => SafeClose();

    // Add Group
    private void OnAddGroupClicked(object sender, EventArgs e)
    {
        var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        _vm.Groups.Add(new TrustGroupDraftItem
        {
            Id = Guid.NewGuid(),
            Name = $"Group {_vm.Groups.Count + 1}",
            SecretHex = hex,
            IsEnabled = true
        });
    }

    // Delete Group
    private void OnDeleteGroupClicked(object sender, EventArgs e)
    {
        if (sender is not Button btn)
            return;

        if (btn.BindingContext is not TrustGroupDraftItem item)
            return;

        _vm.Groups.Remove(item);
    }

    // Copy Secret
    private async void OnCopySecretClicked(object sender, EventArgs e)
    {
        if (sender is not Button btn)
            return;

        if (btn.BindingContext is not TrustGroupDraftItem item)
            return;

        await Clipboard.Default.SetTextAsync(item.SecretHex);
    }

    // Regenerate Secret
    private void OnRegenerateSecretClicked(object sender, EventArgs e)
    {
        if (sender is not Button btn)
            return;

        if (btn.BindingContext is not TrustGroupDraftItem item)
            return;

        item.SecretHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    // Toggle Show / Hide
    private void OnToggleSecretClicked(object sender, EventArgs e)
    {
        if (sender is not Button btn)
            return;

        if (btn.Parent is not Grid grid)
            return;

        var entry = grid.Children.OfType<Entry>().FirstOrDefault();
        if (entry == null)
            return;

        entry.IsPassword = !entry.IsPassword;
        btn.Text = entry.IsPassword ? "Show" : "Hide";
    }

    private void SafeClose()
    {
        Dispatcher.Dispatch(async () =>
        {
            try
            {
                if (Navigation?.ModalStack?.Count > 0)
                    await Navigation.PopModalAsync(false);
            }
            catch
            {
            }
        });
    }

    private void OnBackClicked(object sender, EventArgs e)
    {
        SafeClose();
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var incomingGroups = _vm.Groups
            .Where(g => !string.IsNullOrWhiteSpace(g.Name))
            .Select(g => new TrustGroupEntity
            {
                Id = g.Id == Guid.Empty ? Guid.NewGuid() : g.Id,
                Name = g.Name.Trim(),
                SecretHex = g.SecretHex.Trim(),
                IsEnabled = g.IsEnabled,
                CreatedAtUtc = DateTime.UtcNow
            })
            .ToList();

        db.TrustGroups.RemoveRange(db.TrustGroups);
        await db.SaveChangesAsync();

        db.TrustGroups.AddRange(incomingGroups);
        await db.SaveChangesAsync();

        var trustCache = ServiceHelper.GetService<TrustGroupCache>();

        var enabledSecrets = incomingGroups
            .Where(x => x.IsEnabled)
            .Select(x => x.SecretHex)
            .ToList();

        trustCache.SetEnabledSecrets(enabledSecrets);

        await ServiceHelper.ResetNetworkBoundaryAsync();

        SafeClose();
    }
}