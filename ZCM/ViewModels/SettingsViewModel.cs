using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ZCL.API;
using ZCL.Models;
using ZCL.Security;

namespace ZCM.ViewModels;

public sealed class SettingsViewModel : BindableObject
{
    private string _peerName = "";
    public string PeerName { get => _peerName; set { _peerName = value; OnPropertyChanged(); } }

    private int _discoveryPort;
    public int DiscoveryPort { get => _discoveryPort; set { _discoveryPort = value; OnPropertyChanged(); } }

    private string _multicastAddress = "";
    public string MulticastAddress { get => _multicastAddress; set { _multicastAddress = value; OnPropertyChanged(); } }

    private int _discoveryTimeoutMs;
    public int DiscoveryTimeoutMS { get => _discoveryTimeoutMs; set { _discoveryTimeoutMs = value; OnPropertyChanged(); } }

    public ObservableCollection<TrustGroupDraftItem> Groups { get; } = new();
    public ObservableCollection<ServiceAnnounceDraftItem> AnnouncedServices { get; } = new();

    private string? _originalActiveGroupId;

    public async Task LoadDraftAsync()
    {
        Config.Instance.Load();

        PeerName = Config.Instance.PeerName;
        DiscoveryPort = Config.Instance.DiscoveryPort;
        MulticastAddress = Config.Instance.MulticastAddress;
        DiscoveryTimeoutMS = Config.Instance.DiscoveryTimeoutMS;
        
        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var groups = await db.TrustGroups.OrderBy(x => x.Name).ToListAsync();
        Groups.Clear();
        foreach (var g in groups)
        {
            Groups.Add(new TrustGroupDraftItem
            {
                Id = g.Id,
                Name = g.Name,
                SecretHex = g.SecretHex,
                IsEnabled = g.IsEnabled,
                IsActiveLocal = g.IsActiveLocal
            });
        }

        _originalActiveGroupId = Groups.FirstOrDefault(x => x.IsActiveLocal)?.Id.ToString();

        var services = await db.AnnouncedServiceSettings.OrderBy(x => x.ServiceName).ToListAsync();
        AnnouncedServices.Clear();
        foreach (var s in services)
        {
            AnnouncedServices.Add(new ServiceAnnounceDraftItem
            {
                ServiceName = s.ServiceName,
                IsEnabled = s.IsEnabled
            });
        }
    }

    public void SetActiveGroup(Guid id)
    {
        foreach (var g in Groups)
            g.IsActiveLocal = (g.Id == id);

        OnPropertyChanged(nameof(Groups));
    }

    public async Task SaveAllAsync(TrustGroupCache trustCache)
    {
        Config.Instance.PeerName = PeerName;
        Config.Instance.DiscoveryPort = DiscoveryPort;
        Config.Instance.MulticastAddress = MulticastAddress;
        Config.Instance.DiscoveryTimeoutMS = DiscoveryTimeoutMS;
        Config.Instance.Save();

        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        var incomingGroups = Groups.Select(g => new TrustGroupEntity
        {
            Id = g.Id == Guid.Empty ? Guid.NewGuid() : g.Id,
            Name = g.Name.Trim(),
            SecretHex = g.SecretHex.Trim(),
            IsEnabled = g.IsEnabled,
            IsActiveLocal = g.IsActiveLocal,
            CreatedAtUtc = DateTime.UtcNow
        }).ToList();

        if (incomingGroups.Count > 0 && incomingGroups.Count(x => x.IsActiveLocal) != 1)
        {
            var pick = incomingGroups.FirstOrDefault(x => x.IsEnabled) ?? incomingGroups.First();
            foreach (var g in incomingGroups) g.IsActiveLocal = (g.Id == pick.Id);
        }

        db.TrustGroups.RemoveRange(db.TrustGroups);
        await db.SaveChangesAsync();

        db.TrustGroups.AddRange(incomingGroups);

        db.AnnouncedServiceSettings.RemoveRange(db.AnnouncedServiceSettings);
        await db.SaveChangesAsync();

        db.AnnouncedServiceSettings.AddRange(
            AnnouncedServices.Select(s => new AnnouncedServiceSettingEntity
            {
                ServiceName = s.ServiceName,
                IsEnabled = s.IsEnabled
            }));

        await db.SaveChangesAsync();

        var enabledSecrets = incomingGroups.Where(x => x.IsEnabled).Select(x => x.SecretHex).ToList();
        var activeSecret = incomingGroups.FirstOrDefault(x => x.IsActiveLocal)?.SecretHex;

        trustCache.SetEnabledSecrets(enabledSecrets);
        trustCache.SetActiveSecret(activeSecret);

        var newActiveId = incomingGroups.FirstOrDefault(x => x.IsActiveLocal)?.Id.ToString();
        if (!string.Equals(_originalActiveGroupId, newActiveId, StringComparison.Ordinal))
        {
            TlsCertificateProvider.DeleteLocalIdentityCertificate(Config.Instance.AppDataDirectory);
        }
    }
}

public sealed class TrustGroupDraftItem : BindableObject
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string SecretHex { get; set; } = ""; 
    public bool IsEnabled { get; set; }
    public bool IsActiveLocal { get; set; }

    public string Display => Name;
}

public sealed class ServiceAnnounceDraftItem : BindableObject
{
    public string ServiceName { get; set; } = "";
    public bool IsEnabled { get; set; }
}