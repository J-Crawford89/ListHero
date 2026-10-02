using ListHero.Application.Authorization;
using ListHero.Contracts.Lists;
using ListHero.Domain.Entities;

namespace ListHero.Application.Lists;

public sealed class WishListReadService(IListAccessService access) : IWishListReadService
{
    public OwnerWishListResponse ForOwner(WishList list, IEnumerable<WishListItem> items,
        ViewerIdentity viewer, DateTimeOffset now)
    {
        if (!access.Evaluate(list, viewer, null, now).IsOwner)
            throw new UnauthorizedAccessException("Owner access is required.");
        return new(list.Id, list.Name, list.Description, list.IsPublic,
            ActiveItems(list, items).Select(ToResponse).ToArray(), Convert.ToBase64String(list.RowVersion));
    }

    public ViewerWishListResponse ForViewer(WishList list, IEnumerable<WishListItem> items,
        IEnumerable<ItemPurchase> purchases, ViewerIdentity viewer,
        ListShareLink? validatedShareLink, DateTimeOffset now)
    {
        var permissions = access.Evaluate(list, viewer, validatedShareLink, now);
        if (!permissions.CanViewPurchases)
            throw new UnauthorizedAccessException("Viewer access is required; owners must use the owner response.");
        var totals = purchases.Where(p => p.IsActive).GroupBy(p => p.WishListItemId)
            .ToDictionary(group => group.Key, group => group.Sum(p => (long)p.Quantity));
        var responseItems = ActiveItems(list, items).Select(item =>
        {
            var quantity = totals.GetValueOrDefault(item.Id);
            return new ViewerWishListItemResponse(ToResponse(item), quantity,
                quantity >= item.DesiredQuantity, Math.Max(0L, quantity - item.DesiredQuantity));
        }).ToArray();
        var activeIds = responseItems.Select(item => item.Item.Id).ToHashSet();
        var ownMarks = purchases.Where(p => p.IsActive && activeIds.Contains(p.WishListItemId)
            && ((p.UserId.HasValue && p.UserId == viewer.UserId)
                || (p.GuestIdentityId.HasValue && p.GuestIdentityId == viewer.GuestIdentityId)))
            .Select(p => new OwnPurchaseMarkResponse(p.Id, p.WishListItemId, p.Quantity, p.CreatedAt,
                p.UndoneAt, Convert.ToBase64String(p.RowVersion))).ToArray();
        return new(list.Id, list.Name, list.Description, list.IsPublic, permissions.CanMarkPurchases, responseItems, ownMarks);
    }

    private static IEnumerable<WishListItem> ActiveItems(WishList list, IEnumerable<WishListItem> items)
        => items.Where(item => item.WishListId == list.Id && !item.IsArchived)
            .OrderBy(item => item.DisplayOrder).ThenBy(item => item.Id);

    private static WishListItemResponse ToResponse(WishListItem item) => new(item.Id, item.Name,
        item.Description, item.ApproximateUnitPrice, item.DesiredQuantity, item.Priority,
        item.DisplayOrder, item.Url, item.ImageUrl, Convert.ToBase64String(item.RowVersion));
}
