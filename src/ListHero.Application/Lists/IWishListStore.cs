using ListHero.Domain.Entities;

namespace ListHero.Application.Lists;

public interface IWishListStore
{
    Task<IReadOnlyList<WishList>> GetByOwnerAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<WishList?> GetOwnedAsync(Guid listId, Guid ownerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WishListItem>> GetItemsAsync(Guid listId, CancellationToken cancellationToken = default);
    Task AddListAsync(WishList list, CancellationToken cancellationToken = default);
    Task AddItemAsync(WishListItem item, CancellationToken cancellationToken = default);
    Task<WishListItem?> GetItemAsync(Guid listId, Guid itemId, CancellationToken cancellationToken = default);
    Task SaveListAsync(WishList list, byte[] expectedVersion, CancellationToken cancellationToken = default);
    Task SaveItemAsync(WishListItem item, byte[] expectedVersion, CancellationToken cancellationToken = default);
    Task<bool> HasPurchaseMarksAsync(Guid itemId, CancellationToken cancellationToken = default);
}
