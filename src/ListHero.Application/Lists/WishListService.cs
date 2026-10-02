using ListHero.Application.Common;
using ListHero.Application.Identity;
using ListHero.Contracts.Lists;
using ListHero.Domain.Entities;

namespace ListHero.Application.Lists;

public sealed class WishListService(IUserService users, IWishListStore store,
    IWishListReadService reads, TimeProvider clock) : IWishListService
{
    public async Task<IReadOnlyList<WishListSummaryResponse>> GetMineAsync(ExternalUserIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var user = await users.ResolveAsync(identity, cancellationToken);
        var lists = await store.GetByOwnerAsync(user.Id, cancellationToken);
        return lists.Select(list => new WishListSummaryResponse(list.Id, list.Name,
            list.Description, list.IsPublic, list.CreatedAt)).ToArray();
    }

    public async Task<OwnerWishListResponse> CreateAsync(ExternalUserIdentity identity, CreateWishListRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await users.ResolveAsync(identity, cancellationToken);
        var list = new WishList(user.Id, request.Name, request.Description, clock.GetUtcNow());
        await store.AddListAsync(list, cancellationToken);
        return reads.ForOwner(list, [], new(user.Id), clock.GetUtcNow());
    }

    public async Task<OwnerWishListResponse> GetOwnerViewAsync(ExternalUserIdentity identity, Guid listId,
        CancellationToken cancellationToken = default)
    {
        var user = await users.ResolveAsync(identity, cancellationToken);
        var list = await store.GetOwnedAsync(listId, user.Id, cancellationToken)
            ?? throw new ResourceNotFoundException();
        var items = await store.GetItemsAsync(list.Id, cancellationToken);
        return reads.ForOwner(list, items, new(user.Id), clock.GetUtcNow());
    }

    public async Task<OwnerWishListResponse> AddItemAsync(ExternalUserIdentity identity, Guid listId,
        CreateWishListItemRequest request, CancellationToken cancellationToken = default)
    {
        var user = await users.ResolveAsync(identity, cancellationToken);
        var list = await store.GetOwnedAsync(listId, user.Id, cancellationToken)
            ?? throw new ResourceNotFoundException();
        var item = new WishListItem(list.Id, request.Name, request.Description, clock.GetUtcNow(),
            request.ApproximateUnitPrice, request.DesiredQuantity, request.Priority,
            request.DisplayOrder, request.Url, request.ImageUrl);
        await store.AddItemAsync(item, cancellationToken);
        var items = await store.GetItemsAsync(list.Id, cancellationToken);
        return reads.ForOwner(list, items, new(user.Id), clock.GetUtcNow());
    }
    public async Task<OwnerWishListResponse> UpdateAsync(ExternalUserIdentity identity, Guid listId, UpdateWishListRequest request, CancellationToken cancellationToken = default)
    {
        var list = await RequireOwnerAsync(identity, listId, cancellationToken);
        var version = RequireVersion(list.RowVersion, request.RowVersion);
        list.Update(request.Name, request.Description, request.IsPublic);
        await store.SaveListAsync(list, version, cancellationToken);
        return await GetOwnerViewAsync(identity, listId, cancellationToken);
    }

    public async Task ArchiveAsync(ExternalUserIdentity identity, Guid listId, ArchiveRequest request, CancellationToken cancellationToken = default)
    {
        var list = await RequireOwnerAsync(identity, listId, cancellationToken);
        var version = RequireVersion(list.RowVersion, request.RowVersion);
        list.Archive(clock.GetUtcNow());
        await store.SaveListAsync(list, version, cancellationToken);
    }

    public async Task<OwnerWishListResponse> UpdateItemAsync(ExternalUserIdentity identity, Guid listId, Guid itemId, UpdateWishListItemRequest request, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(identity, listId, cancellationToken);
        var item = await store.GetItemAsync(listId, itemId, cancellationToken) ?? throw new ResourceNotFoundException();
        var version = RequireVersion(item.RowVersion, request.RowVersion);
        item.Update(request.Name, request.Description, request.ApproximateUnitPrice, request.DesiredQuantity, request.Priority, request.DisplayOrder, request.Url, request.ImageUrl);
        await store.SaveItemAsync(item, version, cancellationToken);
        return await GetOwnerViewAsync(identity, listId, cancellationToken);
    }

    public async Task<OwnerWishListResponse> ArchiveItemAsync(ExternalUserIdentity identity, Guid listId, Guid itemId, ArchiveRequest request, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(identity, listId, cancellationToken);
        var item = await store.GetItemAsync(listId, itemId, cancellationToken) ?? throw new ResourceNotFoundException();
        var version = RequireVersion(item.RowVersion, request.RowVersion);
        item.Archive(clock.GetUtcNow());
        await store.SaveItemAsync(item, version, cancellationToken);
        return await GetOwnerViewAsync(identity, listId, cancellationToken);
    }

    public async Task<ItemEditWarningResponse> EditWarningAsync(ExternalUserIdentity identity, Guid listId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(identity, listId, cancellationToken);
        _ = await store.GetItemAsync(listId, itemId, cancellationToken) ?? throw new ResourceNotFoundException();
        return new(await store.HasPurchaseMarksAsync(itemId, cancellationToken));
    }

    private async Task<WishList> RequireOwnerAsync(ExternalUserIdentity identity, Guid listId, CancellationToken cancellationToken)
    {
        var user = await users.ResolveAsync(identity, cancellationToken);
        return await store.GetOwnedAsync(listId, user.Id, cancellationToken) ?? throw new ResourceNotFoundException();
    }

    internal static byte[] RequireVersion(byte[] actual, string expected)
    {
        byte[] version;
        try { version = Convert.FromBase64String(expected); }
        catch (FormatException) { throw new ArgumentException("Invalid row version."); }
        if (version.Length != 8) throw new ArgumentException("Invalid row version.");
        if (!actual.SequenceEqual(version)) throw new EditConflictException();
        return version;
    }
}
