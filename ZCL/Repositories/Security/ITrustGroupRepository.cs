using ZCL.Models;

namespace ZCL.Repositories.Security
{
    public interface ITrustGroupRepository
    {
        Task<List<TrustGroupEntity>> GetAllAsync(CancellationToken ct = default);
        Task<List<TrustGroupEntity>> GetEnabledAsync(CancellationToken ct = default);
        Task UpsertAsync(IEnumerable<TrustGroupEntity> groups, CancellationToken ct = default);
        Task EnsureDefaultsAsync(CancellationToken ct = default);
    }
}