using ListHero.Domain.Entities;

namespace ListHero.Application.Authorization;

public sealed class ListAccessService : IListAccessService
{
    public ListPermissions Evaluate(WishList list, ViewerIdentity viewer,
        ListShareLink? validatedShareLink, DateTimeOffset now)
    {
        if (list.IsArchived) return ListPermissions.Denied;
        if (viewer.UserId == list.OwnerId)
            return new(true, true, true, false, false);

        // The caller must first match the presented token to this link's hash.
        var hasShareAccess = validatedShareLink is not null
            && validatedShareLink.WishListId == list.Id && validatedShareLink.IsActive(now);
        var canView = list.IsPublic || hasShareAccess;
        if (!canView) return ListPermissions.Denied;
        var authenticatedUser = viewer.UserId is Guid userId && userId != Guid.Empty;
        return new(true, false, false, true, hasShareAccess || authenticatedUser);
    }

    public bool CanUndo(ItemPurchase purchase, WishListItem item, WishList list, ViewerIdentity viewer,
        ListShareLink? validatedShareLink, DateTimeOffset now)
    {
        if (item.IsArchived || item.WishListId != list.Id || purchase.WishListItemId != item.Id)
            return false;
        var permissions = Evaluate(list, viewer, validatedShareLink, now);
        return permissions.CanMarkPurchases && !permissions.IsOwner && purchase.IsActive
            && ((purchase.UserId.HasValue && purchase.UserId == viewer.UserId)
                || (purchase.GuestIdentityId.HasValue && purchase.GuestIdentityId == viewer.GuestIdentityId));
    }
}
