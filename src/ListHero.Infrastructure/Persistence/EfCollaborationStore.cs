using ListHero.Application.Collaboration;
using ListHero.Application.Common;
using ListHero.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ListHero.Infrastructure.Persistence;

public sealed class EfCollaborationStore(ListHeroDbContext db) : IListViewerStore, IShareLinkStore, IPurchaseStore
{
    public Task<WishList?> GetActiveListAsync(Guid listId, CancellationToken ct = default)
        => db.WishLists.AsNoTracking().SingleOrDefaultAsync(list => list.Id == listId && list.ArchivedAt == null, ct);
    public Task<ListShareLink?> GetShareByHashAsync(string hash, CancellationToken ct = default)
        => db.ListShareLinks.AsNoTracking().SingleOrDefaultAsync(link => link.TokenHash == hash && link.ArchivedAt == null, ct);
    public Task<GuestIdentity?> GetGuestByHashAsync(string hash, CancellationToken ct = default)
        => db.GuestIdentities.AsNoTracking().SingleOrDefaultAsync(guest => guest.CredentialHash == hash && guest.ArchivedAt == null, ct);
    public async Task<IReadOnlyList<ItemPurchase>> GetPurchasesAsync(Guid listId, CancellationToken ct = default)
    {
        var itemIds = db.WishListItems.Where(item => item.WishListId == listId && item.ArchivedAt == null).Select(item => item.Id);
        return await db.ItemPurchases.AsNoTracking().Where(mark => itemIds.Contains(mark.WishListItemId)
            && mark.ArchivedAt == null).ToArrayAsync(ct);
    }
    public async Task<IReadOnlyList<ListShareLink>> GetLinksAsync(Guid listId, CancellationToken ct = default)
        => await db.ListShareLinks.AsNoTracking().Where(link => link.WishListId == listId && link.ArchivedAt == null)
            .OrderByDescending(link => link.CreatedAt).ThenBy(link => link.Id).ToArrayAsync(ct);
    public async Task AddLinkAsync(ListShareLink link, CancellationToken ct = default)
    {
        db.ListShareLinks.Add(link);
        await db.SaveChangesAsync(ct);
    }
    public async Task RevokeLinkAsync(Guid listId, Guid linkId, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!await db.ListShareLinks.AnyAsync(link => link.Id == linkId && link.WishListId == listId && link.ArchivedAt == null, ct))
            throw new ResourceNotFoundException();
        await db.ListShareLinks.Where(link => link.Id == linkId && link.WishListId == listId
            && link.RevokedAt == null && link.ArchivedAt == null).ExecuteUpdateAsync(update => update.SetProperty(link => link.RevokedAt, now), ct);
    }
    public async Task AddGuestAsync(GuestIdentity guest, CancellationToken ct = default)
    {
        db.GuestIdentities.Add(guest);
        await db.SaveChangesAsync(ct);
    }
    public Task<ItemPurchase?> GetPurchaseAsync(Guid itemId, Guid markId, CancellationToken ct = default)
        => db.ItemPurchases.AsNoTracking().SingleOrDefaultAsync(mark => mark.Id == markId && mark.WishListItemId == itemId && mark.ArchivedAt == null, ct);
    public Task<ItemPurchase?> GetByIdempotencyKeyAsync(Guid itemId, Guid key, CancellationToken ct = default)
        => db.ItemPurchases.AsNoTracking().SingleOrDefaultAsync(mark => mark.WishListItemId == itemId && mark.IdempotencyKey == key, ct);
    public async Task<ItemPurchase> AddOrGetAsync(ItemPurchase mark, CancellationToken ct = default)
    {
        db.ItemPurchases.Add(mark);
        try { await db.SaveChangesAsync(ct); return mark; }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.Entry(mark).State = EntityState.Detached;
            return await GetByIdempotencyKeyAsync(mark.WishListItemId, mark.IdempotencyKey, ct)
                ?? throw new EditConflictException();
        }
    }
    public async Task SavePurchaseAsync(ItemPurchase mark, byte[] expectedVersion, CancellationToken ct = default)
    {
        db.ItemPurchases.Update(mark);
        db.Entry(mark).Property(p => p.RowVersion).OriginalValue = expectedVersion;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new EditConflictException(); }
    }
}
