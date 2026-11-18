using ListHero.Contracts.Lists;
namespace ListHero.Client.Abstractions.Api;
public interface IDashboardApi
{
    Task<DashboardResponse> GetAsync(CancellationToken ct = default);
    Task<SavedListPage> SavedAsync(int page = 1, CancellationToken ct = default);
    Task SaveAsync(Guid listId, bool pinned, string? shareToken = null, CancellationToken ct = default);
    Task RemoveAsync(Guid listId, CancellationToken ct = default);
}
