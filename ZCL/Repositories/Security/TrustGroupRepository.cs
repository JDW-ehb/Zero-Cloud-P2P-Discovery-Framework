using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using ZCL.Models;

namespace ZCL.Repositories.Security;

public sealed class TrustGroupRepository : ITrustGroupRepository
{
    private readonly ServiceDBContext _db;
    public TrustGroupRepository(ServiceDBContext db) => _db = db;

    public Task<List<TrustGroupEntity>> GetAllAsync(CancellationToken ct = default)
        => _db.TrustGroups.OrderBy(x => x.Name).ToListAsync(ct);

    public Task<List<TrustGroupEntity>> GetEnabledAsync(CancellationToken ct = default)
        => _db.TrustGroups.Where(x => x.IsEnabled).OrderBy(x => x.Name).ToListAsync(ct);

    public Task<TrustGroupEntity?> GetActiveLocalAsync(CancellationToken ct = default)
        => _db.TrustGroups.FirstOrDefaultAsync(x => x.IsActiveLocal, ct);

    public async Task EnsureDefaultsAsync(CancellationToken ct = default)
    {
        if (await _db.TrustGroups.AnyAsync(ct))
            return;

        var bytes = RandomNumberGenerator.GetBytes(32);
        var hex = Convert.ToHexString(bytes);

        _db.TrustGroups.Add(new TrustGroupEntity
        {
            Id = Guid.NewGuid(),
            Name = "Default",
            SecretHex = hex,
            IsEnabled = true,
            IsActiveLocal = true,
            CreatedAtUtc = DateTime.UtcNow
        });

        _db.AnnouncedServiceSettings.AddRange(
            new AnnouncedServiceSettingEntity { ServiceName = "Messaging", IsEnabled = true },
            new AnnouncedServiceSettingEntity { ServiceName = "FileSharing", IsEnabled = true },
            new AnnouncedServiceSettingEntity { ServiceName = "LLMChat", IsEnabled = true }
        );

        await _db.SaveChangesAsync(ct);
    }

    public async Task UpsertAsync(IEnumerable<TrustGroupEntity> groups, CancellationToken ct = default)
    {
        // Replace-by-key (Id) style upsert
        var incoming = groups.ToList();
        var ids = incoming.Select(x => x.Id).ToHashSet();

        var existing = await _db.TrustGroups.ToListAsync(ct);

        // delete removed
        foreach (var ex in existing)
        {
            if (!ids.Contains(ex.Id))
                _db.TrustGroups.Remove(ex);
        }

        // upsert present
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
                ex.IsActiveLocal = g.IsActiveLocal;
            }
        }

        // enforce single active
        var activeCount = incoming.Count(x => x.IsActiveLocal);
        if (activeCount != 1)
        {
            // if bad state, pick first enabled or first
            var pick = incoming.FirstOrDefault(x => x.IsEnabled) ?? incoming.First();
            foreach (var g in incoming) g.IsActiveLocal = (g.Id == pick.Id);
        }

        await _db.SaveChangesAsync(ct);
    }
}