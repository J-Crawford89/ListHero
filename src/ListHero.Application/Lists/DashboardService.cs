using ListHero.Application.Collaboration;
using ListHero.Application.Identity;
using ListHero.Contracts.Lists;
using ListHero.Domain.Entities;
namespace ListHero.Application.Lists;

public interface ISavedListStore
{
    Task<ListShareLink?> GetShareAsync(Guid userId, Guid listId, CancellationToken ct);
    Task SaveAsync(Guid userId, Guid listId, Guid? shareId, bool pinned, DateTimeOffset now, CancellationToken ct);
    Task RemoveAsync(Guid userId, Guid listId, DateTimeOffset now, CancellationToken ct);
    Task<SavedListPage> GetAsync(Guid userId, int page, bool pinnedOnly, DateTimeOffset now, CancellationToken ct);
}
public interface IDashboardService
{
    Task<DashboardResponse> GetAsync(ExternalUserIdentity identity, CancellationToken ct = default);
    Task<SavedListPage> SavedAsync(ExternalUserIdentity identity, int page, CancellationToken ct = default);
    Task SaveAsync(Guid listId, ListRequestAccess access, bool pinned, CancellationToken ct = default);
    Task RemoveAsync(ExternalUserIdentity identity, Guid listId, CancellationToken ct = default);
}
public sealed class DashboardService(IUserService users, IWishListStore lists, ISavedListStore saved,
    IListAccessResolver access, TimeProvider clock) : IDashboardService
{
    public async Task<DashboardResponse> GetAsync(ExternalUserIdentity identity, CancellationToken ct = default)
    {
        var user = await users.ResolveAsync(identity, ct);
        var owned = await lists.GetByOwnerAsync(user.Id, ct);
        var pins = await saved.GetAsync(user.Id, 1, true, clock.GetUtcNow(), ct);
        var entries = await saved.GetAsync(user.Id, 1, false, clock.GetUtcNow(), ct);
        return new(pins.Items, owned.Take(5).Select(l => new WishListSummaryResponse(l.Id, l.Name, l.Description, l.IsPublic, l.CreatedAt)).ToArray(), entries.Items.Take(5).ToArray());
    }
    public async Task<SavedListPage> SavedAsync(ExternalUserIdentity identity, int page, CancellationToken ct = default)
    {
        if (page is < 1 or > 1000) throw new ArgumentException("Choose a page from 1–1000.");
        var user = await users.ResolveAsync(identity, ct);
        return await saved.GetAsync(user.Id, page, false, clock.GetUtcNow(), ct);
    }
    public async Task SaveAsync(Guid listId, ListRequestAccess request, bool pinned, CancellationToken ct = default)
    {
        var user = await users.ResolveAsync(request.User ?? throw new UnauthorizedAccessException(), ct);
        var resolved = await access.ResolveAsync(listId, request, ct);
        await saved.SaveAsync(user.Id, listId, resolved.Share?.Id, pinned, clock.GetUtcNow(), ct);
    }
    public async Task RemoveAsync(ExternalUserIdentity identity, Guid listId, CancellationToken ct = default)
        => await saved.RemoveAsync((await users.ResolveAsync(identity, ct)).Id, listId, clock.GetUtcNow(), ct);
}
