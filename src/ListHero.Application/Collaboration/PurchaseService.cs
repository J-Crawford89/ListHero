using ListHero.Application.Common;
using ListHero.Application.Lists;
using ListHero.Application.Security;
using ListHero.Contracts.Lists;
using ListHero.Domain.Entities;

namespace ListHero.Application.Collaboration;

public sealed class PurchaseService(IListAccessResolver access, IWishListStore lists, IPurchaseStore store,
    ICapabilityTokenService tokens, TimeProvider clock) : IPurchaseService
{
    public async Task<GuestCredentialResponse> IssueGuestAsync(Guid listId, ListRequestAccess request, CancellationToken ct = default)
    {
        var resolved = await RequireBuyerAsync(listId, request, ct);
        if (resolved.Viewer.UserId.HasValue) throw new UnauthorizedAccessException();
        if (resolved.Viewer.GuestIdentityId.HasValue) return new(request.GuestCredential!);
        var credential = tokens.Issue();
        await store.AddGuestAsync(new(credential.Hash, clock.GetUtcNow()), ct);
        return new(credential.Token);
    }

    public async Task<OwnPurchaseMarkResponse> MarkAsync(Guid listId, Guid itemId, ListRequestAccess request,
        CreatePurchaseMarkRequest markRequest, CancellationToken ct = default)
    {
        var resolved = await RequireBuyerAsync(listId, request, ct);
        _ = await lists.GetItemAsync(listId, itemId, ct) ?? throw new ResourceNotFoundException();
        if (!resolved.Viewer.UserId.HasValue && !resolved.Viewer.GuestIdentityId.HasValue)
            throw new UnauthorizedAccessException();
        var mark = new ItemPurchase(itemId, markRequest.Quantity, resolved.Viewer.UserId,
            resolved.Viewer.UserId.HasValue ? null : resolved.Viewer.GuestIdentityId, markRequest.IdempotencyKey, clock.GetUtcNow());
        var existing = await store.GetByIdempotencyKeyAsync(itemId, markRequest.IdempotencyKey, ct);
        var result = existing ?? await store.AddOrGetAsync(mark, ct);
        if (result.UserId != mark.UserId || result.GuestIdentityId != mark.GuestIdentityId || result.Quantity != mark.Quantity)
            throw new EditConflictException();
        return ToResponse(result);
    }

    public async Task UndoAsync(Guid listId, Guid itemId, Guid markId, ListRequestAccess request, ArchiveRequest undoRequest, CancellationToken ct = default)
    {
        var resolved = await RequireBuyerAsync(listId, request, ct);
        _ = await lists.GetItemAsync(listId, itemId, ct) ?? throw new ResourceNotFoundException();
        var mark = await store.GetPurchaseAsync(itemId, markId, ct) ?? throw new ResourceNotFoundException();
        if (!((mark.UserId.HasValue && mark.UserId == resolved.Viewer.UserId)
            || (mark.GuestIdentityId.HasValue && mark.GuestIdentityId == resolved.Viewer.GuestIdentityId)))
            throw new ResourceNotFoundException();
        if (mark.UndoneAt.HasValue) return;
        var version = WishListService.RequireVersion(mark.RowVersion, undoRequest.RowVersion);
        mark.Undo(clock.GetUtcNow());
        try { await store.SavePurchaseAsync(mark, version, ct); }
        catch (EditConflictException)
        {
            var current = await store.GetPurchaseAsync(itemId, markId, ct);
            if (current?.UndoneAt is null) throw;
        }
    }

    private async Task<ResolvedListAccess> RequireBuyerAsync(Guid listId, ListRequestAccess request, CancellationToken ct)
    {
        var resolved = await access.ResolveAsync(listId, request, ct);
        if (!resolved.Permissions.CanMarkPurchases || resolved.Permissions.IsOwner) throw new UnauthorizedAccessException();
        return resolved;
    }
    private static OwnPurchaseMarkResponse ToResponse(ItemPurchase mark) => new(mark.Id, mark.WishListItemId,
        mark.Quantity, mark.CreatedAt, mark.UndoneAt, Convert.ToBase64String(mark.RowVersion));
}
