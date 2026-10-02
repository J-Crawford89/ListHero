using ListHero.Application.Authorization;
using ListHero.Application.Identity;
using ListHero.Contracts.Lists;
using ListHero.Domain.Entities;

namespace ListHero.Application.Collaboration;

// Credentials are supplied separately from resource identifiers and never trusted as actor IDs.
public sealed record ListRequestAccess(ExternalUserIdentity? User, string? ShareToken, string? GuestCredential)
{
    public override string ToString() => "ListRequestAccess { Credentials = [redacted] }";
}

public sealed record ResolvedListAccess(WishList List, ViewerIdentity Viewer, ListShareLink? Share, ListPermissions Permissions);
public interface IListAccessResolver
{
    Task<ResolvedListAccess> ResolveAsync(Guid listId, ListRequestAccess request, CancellationToken ct = default);
}
public interface IListViewerStore
{
    Task<WishList?> GetActiveListAsync(Guid listId, CancellationToken ct = default);
    Task<ListShareLink?> GetShareByHashAsync(string hash, CancellationToken ct = default);
    Task<GuestIdentity?> GetGuestByHashAsync(string hash, CancellationToken ct = default);
    Task<IReadOnlyList<ItemPurchase>> GetPurchasesAsync(Guid listId, CancellationToken ct = default);
}
public interface IShareLinkStore
{
    Task<IReadOnlyList<ListShareLink>> GetLinksAsync(Guid listId, CancellationToken ct = default);
    Task AddLinkAsync(ListShareLink link, CancellationToken ct = default);
    Task RevokeLinkAsync(Guid listId, Guid linkId, DateTimeOffset now, CancellationToken ct = default);
}
public interface IPurchaseStore
{
    Task AddGuestAsync(GuestIdentity guest, CancellationToken ct = default);
    Task<ItemPurchase?> GetPurchaseAsync(Guid itemId, Guid markId, CancellationToken ct = default);
    Task<ItemPurchase?> GetByIdempotencyKeyAsync(Guid itemId, Guid key, CancellationToken ct = default);
    Task<ItemPurchase> AddOrGetAsync(ItemPurchase mark, CancellationToken ct = default);
    Task SavePurchaseAsync(ItemPurchase mark, byte[] expectedVersion, CancellationToken ct = default);
}
public interface IShareLinkService
{
    Task<IReadOnlyList<ShareLinkResponse>> GetAsync(ExternalUserIdentity owner, Guid listId, CancellationToken ct = default);
    Task<ShareLinkResponse> CreateAsync(ExternalUserIdentity owner, Guid listId, CreateShareLinkRequest request, CancellationToken ct = default);
    Task RevokeAsync(ExternalUserIdentity owner, Guid listId, Guid linkId, CancellationToken ct = default);
}
public interface IListViewerService
{
    Task<ListViewResponse> GetAsync(Guid listId, ListRequestAccess request, CancellationToken ct = default);
}
public interface IPurchaseService
{
    Task<GuestCredentialResponse> IssueGuestAsync(Guid listId, ListRequestAccess access, CancellationToken ct = default);
    Task<OwnPurchaseMarkResponse> MarkAsync(Guid listId, Guid itemId, ListRequestAccess access, CreatePurchaseMarkRequest request, CancellationToken ct = default);
    Task UndoAsync(Guid listId, Guid itemId, Guid markId, ListRequestAccess access, ArchiveRequest request, CancellationToken ct = default);
}
