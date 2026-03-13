using System.Collections.ObjectModel;
using Microsoft.EntityFrameworkCore;
using ZCL.API;
using ZCL.Models;
using ZCL.Security;

namespace ZCM.ViewModels;

public sealed class SettingsViewModel : BindableObject
{
    private string _peerName = "";
    public string PeerName
    {
        get => _peerName;
        set { _peerName = value; OnPropertyChanged(); }
    }

    private int _discoveryPort;
    public int DiscoveryPort
    {
        get => _discoveryPort;
        set { _discoveryPort = value; OnPropertyChanged(); }
    }

    private string _multicastAddress = "";
    public string MulticastAddress
    {
        get => _multicastAddress;
        set { _multicastAddress = value; OnPropertyChanged(); }
    }

    private string _discoveryTimeoutMsText = "";
    public string DiscoveryTimeoutMSText
    {
        get => _discoveryTimeoutMsText;
        set
        {
            _discoveryTimeoutMsText = value;
            OnPropertyChanged();
        }
    }

    public int DiscoveryTimeoutMS
    {
        get => int.TryParse(_discoveryTimeoutMsText, out var v) ? v : 0;
        set
        {
            _discoveryTimeoutMsText = value.ToString();
            OnPropertyChanged(nameof(DiscoveryTimeoutMSText));
        }
    }

    private string _networkSecret = "";
    private string? _originalNetworkSecret;

    public string NetworkSecret
    {
        get => _networkSecret;
        set { _networkSecret = value; OnPropertyChanged(); }
    }

    public ObservableCollection<TrustGroupDraftItem> Groups { get; } = new();
    public ObservableCollection<ServiceAnnounceDraftItem> AnnouncedServices { get; } = new();

    // ---------------------------------------------------------
    // LOAD
    // ---------------------------------------------------------

    public async Task LoadDraftAsync()
    {
        Config.Instance.Load();

        PeerName = Config.Instance.PeerName;
        DiscoveryPort = Config.Instance.DiscoveryPort;
        MulticastAddress = Config.Instance.MulticastAddress;
        DiscoveryTimeoutMS = Config.Instance.DiscoveryTimeoutMS;
        NetworkSecret = Config.Instance.NetworkSecret;

        _originalNetworkSecret = NetworkSecret;

        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        // Load groups
        var groups = await db.TrustGroups
            .OrderBy(x => x.Name)
            .ToListAsync();

        Groups.Clear();

        foreach (var g in groups)
        {
            Groups.Add(new TrustGroupDraftItem
            {
                Id = g.Id,
                Name = g.Name,
                SecretHex = g.SecretHex,
                IsEnabled = g.IsEnabled
            });
        }

        // Load services
        var services = await db.AnnouncedServiceSettings
            .OrderBy(x => x.ServiceName)
            .ToListAsync();

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

    // ---------------------------------------------------------
    // SAVE
    // ---------------------------------------------------------

    public async Task SaveAllAsync(TrustGroupCache trustCache)
    {
        // ---- Save core config ----

        Config.Instance.PeerName = PeerName.Trim();
        Config.Instance.DiscoveryPort = DiscoveryPort;
        Config.Instance.MulticastAddress = MulticastAddress.Trim();
        Config.Instance.DiscoveryTimeoutMS = DiscoveryTimeoutMS;
        Config.Instance.NetworkSecret = NetworkSecret.Trim();

        Config.Instance.Save();

        // If network secret changed -> regenerate TLS cert
        if (!string.Equals(_originalNetworkSecret, NetworkSecret, StringComparison.Ordinal))
        {
            TlsCertificateProvider.DeleteLocalIdentityCertificate(
                Config.Instance.AppDataDirectory);
        }

        using var scope = ServiceHelper.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDBContext>();

        // ---- Persist groups ----

        var incomingGroups = Groups
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

        // Replace all groups (simple + deterministic)
        db.TrustGroups.RemoveRange(db.TrustGroups);
        await db.SaveChangesAsync();

        db.TrustGroups.AddRange(incomingGroups);

        // ---- Persist service settings ----

        db.AnnouncedServiceSettings.RemoveRange(db.AnnouncedServiceSettings);
        await db.SaveChangesAsync();

        db.AnnouncedServiceSettings.AddRange(
            AnnouncedServices.Select(s => new AnnouncedServiceSettingEntity
            {
                ServiceName = s.ServiceName,
                IsEnabled = s.IsEnabled
            }));

        await db.SaveChangesAsync();

        // ---- Update runtime trust cache ----

        var enabledSecrets = incomingGroups
            .Where(x => x.IsEnabled)
            .Select(x => x.SecretHex)
            .ToList();

        trustCache.SetEnabledSecrets(enabledSecrets);
        await ServiceHelper.ResetNetworkBoundaryAsync();
    }
}

// ---------------------------------------------------------
// Draft Models
// ---------------------------------------------------------

public sealed class TrustGroupDraftItem : BindableObject
{
    public Guid Id { get; set; }

    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    private string _secretHex = "";
    public string SecretHex
    {
        get => _secretHex;
        set { _secretHex = value; OnPropertyChanged(); }
    }

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnPropertyChanged(); }
    }

    public string Display => Name;
}

public sealed class ServiceAnnounceDraftItem : BindableObject
{
    public string ServiceName { get; set; } = "";

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; OnPropertyChanged(); }
    }
}