using ListHero.Contracts.Lists;

namespace ListHero.Client.Abstractions.Api;

public interface IWishListApi
{
    Task<IReadOnlyList<WishListSummaryResponse>> GetMineAsync(CancellationToken cancellationToken = default);
    Task<OwnerWishListResponse> CreateAsync(CreateWishListRequest request, CancellationToken cancellationToken = default);
    Task<OwnerWishListResponse> GetOwnerViewAsync(Guid listId, CancellationToken cancellationToken = default);
    Task<OwnerWishListResponse> AddItemAsync(Guid listId, CreateWishListItemRequest request, CancellationToken cancellationToken = default);
    Task<OwnerWishListResponse> UpdateAsync(Guid listId, UpdateWishListRequest request, CancellationToken cancellationToken = default);
    Task ArchiveAsync(Guid listId, ArchiveRequest request, CancellationToken cancellationToken = default);
    Task<OwnerWishListResponse> UpdateItemAsync(Guid listId, Guid itemId, UpdateWishListItemRequest request, CancellationToken cancellationToken = default);
    Task<OwnerWishListResponse> ArchiveItemAsync(Guid listId, Guid itemId, ArchiveRequest request, CancellationToken cancellationToken = default);
    Task<ItemEditWarningResponse> EditWarningAsync(Guid listId, Guid itemId, CancellationToken cancellationToken = default);
}

public interface IApiAccessTokenProvider
{
    Task<string> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class SignInRequiredException : Exception
{
    public SignInRequiredException() : base("Please sign in again to continue.") { }
}

public class ApiRequestException(string message) : Exception(message);
public sealed class ApiConflictException() : ApiRequestException("This record changed. Reload the list before trying again. Your unsaved changes are still in the form.");
