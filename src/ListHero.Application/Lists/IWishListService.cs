using ListHero.Application.Identity;
using ListHero.Contracts.Lists;

namespace ListHero.Application.Lists;

public interface IWishListService
{
    Task<IReadOnlyList<WishListSummaryResponse>> GetMineAsync(ExternalUserIdentity identity,
        CancellationToken cancellationToken = default);
    Task<OwnerWishListResponse> CreateAsync(ExternalUserIdentity identity, CreateWishListRequest request,
        CancellationToken cancellationToken = default);
    Task<OwnerWishListResponse> GetOwnerViewAsync(ExternalUserIdentity identity, Guid listId,
        CancellationToken cancellationToken = default);
    Task<OwnerWishListResponse> AddItemAsync(ExternalUserIdentity identity, Guid listId,
        CreateWishListItemRequest request, CancellationToken cancellationToken = default);
    Task<OwnerWishListResponse> UpdateAsync(ExternalUserIdentity identity, Guid listId, UpdateWishListRequest request, CancellationToken cancellationToken = default);
    Task ArchiveAsync(ExternalUserIdentity identity, Guid listId, ArchiveRequest request, CancellationToken cancellationToken = default);
    Task<OwnerWishListResponse> UpdateItemAsync(ExternalUserIdentity identity, Guid listId, Guid itemId, UpdateWishListItemRequest request, CancellationToken cancellationToken = default);
    Task<OwnerWishListResponse> ArchiveItemAsync(ExternalUserIdentity identity, Guid listId, Guid itemId, ArchiveRequest request, CancellationToken cancellationToken = default);
    Task<ItemEditWarningResponse> EditWarningAsync(ExternalUserIdentity identity, Guid listId, Guid itemId, CancellationToken cancellationToken = default);
}
