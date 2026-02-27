using ZCL.Models;

namespace ZCL.Repositories.Security;

public interface IAnnouncedServiceSettingsRepository
{
    Task<List<AnnouncedServiceSettingEntity>> GetAllAsync(CancellationToken ct = default);
    Task<HashSet<string>> GetEnabledNamesAsync(CancellationToken ct = default);
    Task UpsertAsync(IEnumerable<AnnouncedServiceSettingEntity> settings, CancellationToken ct = default);
}