using ListHero.Application.Authorization;
using ListHero.Contracts.Lists;
using ListHero.Domain.Entities;

namespace ListHero.Application.Lists;

public interface IWishListReadService
{
    OwnerWishListResponse ForOwner(WishList list, IEnumerable<WishListItem> items,
        ViewerIdentity viewer, DateTimeOffset now);

    ViewerWishListResponse ForViewer(WishList list, IEnumerable<WishListItem> items,
        IEnumerable<ItemPurchase> purchases, ViewerIdentity viewer,
        ListShareLink? validatedShareLink, DateTimeOffset now);
}
