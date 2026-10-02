using ListHero.Domain.Entities;

namespace ListHero.Application.Authorization;

public interface IListAccessService
{
    ListPermissions Evaluate(WishList list, ViewerIdentity viewer,
        ListShareLink? validatedShareLink, DateTimeOffset now);

    bool CanUndo(ItemPurchase purchase, WishListItem item, WishList list, ViewerIdentity viewer,
        ListShareLink? validatedShareLink, DateTimeOffset now);
}
