using ListHero.Application.Lists;
using ListHero.Application.Common;
using ListHero.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ListHero.Infrastructure.Persistence;

public sealed class EfWishListStore(ListHeroDbContext db) : IWishListStore
{
    public async Task<IReadOnlyList<WishList>> GetByOwnerAsync(Guid ownerId, CancellationToken cancellationToken = default)
        => await db.WishLists.AsNoTracking().Where(list => list.OwnerId == ownerId && list.ArchivedAt == null)
            .OrderByDescending(list => list.CreatedAt).ThenBy(list => list.Id).ToArrayAsync(cancellationToken);

    public Task<WishList?> GetOwnedAsync(Guid listId, Guid ownerId, CancellationToken cancellationToken = default)
        => db.WishLists.AsNoTracking().SingleOrDefaultAsync(list => list.Id == listId
            && list.OwnerId == ownerId && list.ArchivedAt == null, cancellationToken);

    public async Task<IReadOnlyList<WishListItem>> GetItemsAsync(Guid listId, CancellationToken cancellationToken = default)
        => await db.WishListItems.AsNoTracking().Where(item => item.WishListId == listId && item.ArchivedAt == null)
            .OrderBy(item => item.DisplayOrder).ThenBy(item => item.Id).ToArrayAsync(cancellationToken);

    public async Task AddListAsync(WishList list, CancellationToken cancellationToken = default)
    {
        db.WishLists.Add(list);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddItemAsync(WishListItem item, CancellationToken cancellationToken = default)
    {
        db.WishListItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);
    }
    public Task<WishListItem?> GetItemAsync(Guid listId, Guid itemId, CancellationToken cancellationToken = default)
        => db.WishListItems.AsNoTracking().SingleOrDefaultAsync(item => item.Id == itemId && item.WishListId == listId && item.ArchivedAt == null, cancellationToken);

    public async Task SaveListAsync(WishList list, byte[] expectedVersion, CancellationToken cancellationToken = default)
    {
        db.WishLists.Update(list);
        db.Entry(list).Property(e => e.RowVersion).OriginalValue = expectedVersion;
        await SaveAsync(cancellationToken);
    }
    public async Task SaveItemAsync(WishListItem item, byte[] expectedVersion, CancellationToken cancellationToken = default)
    {
        db.WishListItems.Update(item);
        db.Entry(item).Property(e => e.RowVersion).OriginalValue = expectedVersion;
        await SaveAsync(cancellationToken);
    }
    public Task<bool> HasPurchaseMarksAsync(Guid itemId, CancellationToken cancellationToken = default)
        => db.ItemPurchases.AnyAsync(mark => mark.WishListItemId == itemId && mark.ArchivedAt == null && mark.UndoneAt == null, cancellationToken);
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new EditConflictException(); }
    }
}
