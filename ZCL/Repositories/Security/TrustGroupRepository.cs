// File: ZCL/Repositories/Security/TrustGroupRepository.cs
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using ZCL.Models;

namespace ZCL.Repositories.Security;

public sealed class TrustGroupRepository : ITrustGroupRepository
{
    private readonly ServiceDBContext _db;

    public TrustGroupRepository(ServiceDBContext db)
        => _db = db;

    public Task<List<TrustGroupEntity>> GetAllAsync(CancellationToken ct = default)
        => _db.TrustGroups
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

    public Task<List<TrustGroupEntity>> GetEnabledAsync(CancellationToken ct = default)
        => _db.TrustGroups
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

    // EnsureDefaultsAsync: deterministic Default group secret so fresh installs can talk.
    public async Task EnsureDefaultsAsync(CancellationToken ct = default)
    {
        if (await _db.TrustGroups.AnyAsync(ct))
            return;

        // Deterministic, strong 256-bit default secret (same on every install)
        const string defaultSeed = "ZC_DEFAULT_TRUST_GROUP_V1";
        var defaultBytes = SHA256.HashData(Encoding.UTF8.GetBytes(defaultSeed));
        var defaultHex = Convert.ToHexString(defaultBytes);

        _db.TrustGroups.Add(new TrustGroupEntity
        {
            Id = Guid.NewGuid(),
            Name = "Default",
            SecretHex = defaultHex,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow
        });

        _db.AnnouncedServiceSettings.AddRange(
            new AnnouncedServiceSettingEntity { ServiceName = "Messaging", IsEnabled = true },
            new AnnouncedServiceSettingEntity { ServiceName = "FileSharing", IsEnabled = true },
            new AnnouncedServiceSettingEntity { ServiceName = "LLMChat", IsEnabled = true }
        );

        await _db.SaveChangesAsync(ct);
    }

    // UpsertAsync: keep your current behavior (delete missing, add/update present)
    public async Task UpsertAsync(IEnumerable<TrustGroupEntity> groups, CancellationToken ct = default)
    {
        var incoming = groups.ToList();
        var ids = incoming.Select(x => x.Id).ToHashSet();

        var existing = await _db.TrustGroups.ToListAsync(ct);

        // Remove deleted groups
        foreach (var ex in existing)
            if (!ids.Contains(ex.Id))
                _db.TrustGroups.Remove(ex);

        // Add / update
        foreach (var g in incoming)
        {
            var ex = existing.FirstOrDefault(x => x.Id == g.Id);

            if (ex == null)
            {
                _db.TrustGroups.Add(g);
            }
            else
            {
                ex.Name = g.Name;
                ex.SecretHex = g.SecretHex;
                ex.IsEnabled = g.IsEnabled;
            }
        }

        await _db.SaveChangesAsync(ct);
    }
}