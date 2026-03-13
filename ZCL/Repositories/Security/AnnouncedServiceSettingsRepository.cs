using Microsoft.EntityFrameworkCore;
using ZCL.Models;

namespace ZCL.Repositories.Security;

public sealed class AnnouncedServiceSettingsRepository : IAnnouncedServiceSettingsRepository
{
    private readonly ServiceDBContext _db;
    public AnnouncedServiceSettingsRepository(ServiceDBContext db) => _db = db;

    public Task<List<AnnouncedServiceSettingEntity>> GetAllAsync(CancellationToken ct = default)
        => _db.AnnouncedServiceSettings.OrderBy(x => x.ServiceName).ToListAsync(ct);

    public async Task<HashSet<string>> GetEnabledNamesAsync(CancellationToken ct = default)
        => (await _db.AnnouncedServiceSettings
                .Where(x => x.IsEnabled)
                .Select(x => x.ServiceName)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

    public async Task UpsertAsync(IEnumerable<AnnouncedServiceSettingEntity> settings, CancellationToken ct = default)
    {
        var incoming = settings.ToList();
        var map = incoming.ToDictionary(x => x.ServiceName, StringComparer.Ordinal);

        var existing = await _db.AnnouncedServiceSettings.ToListAsync(ct);

        // add/update
        foreach (var s in incoming)
        {
            var ex = existing.FirstOrDefault(x => x.ServiceName == s.ServiceName);
            if (ex == null)
                _db.AnnouncedServiceSettings.Add(new AnnouncedServiceSettingEntity { ServiceName = s.ServiceName, IsEnabled = s.IsEnabled });
            else
                ex.IsEnabled = s.IsEnabled;
        }

        // delete removed (optional)
        foreach (var ex in existing)
        {
            if (!map.ContainsKey(ex.ServiceName))
                _db.AnnouncedServiceSettings.Remove(ex);
        }

        await _db.SaveChangesAsync(ct);
    }
}