using ListHero.Application.Lists;
using ListHero.Contracts.Lists;

namespace ListHero.Application.Collaboration;

public sealed class ListViewerService(IListAccessResolver access, IWishListStore lists,
    IListViewerStore store, IWishListReadService reads, TimeProvider clock) : IListViewerService
{
    public async Task<ListViewResponse> GetAsync(Guid listId, ListRequestAccess request, CancellationToken ct = default)
    {
        var resolved = await access.ResolveAsync(listId, request, ct);
        var items = await lists.GetItemsAsync(listId, ct);
        // Owner precedence avoids even querying the purchase table for the ordinary owner view.
        if (resolved.Permissions.IsOwner)
            return new(reads.ForOwner(resolved.List, items, resolved.Viewer, clock.GetUtcNow()), null);
        return new(null, reads.ForViewer(resolved.List, items, await store.GetPurchasesAsync(listId, ct),
            resolved.Viewer, resolved.Share, clock.GetUtcNow()));
    }
}
