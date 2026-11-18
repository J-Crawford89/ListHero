using ListHero.Application.Lists;
using ListHero.Contracts.Lists;
using ListHero.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
namespace ListHero.Infrastructure.Persistence;

public sealed class EfSavedListStore(ListHeroDbContext db) : ISavedListStore
{
    public Task<ListShareLink?> GetShareAsync(Guid userId, Guid listId, CancellationToken ct)
        => (from entry in db.SavedLists.AsNoTracking()
            join link in db.ListShareLinks.AsNoTracking() on entry.ShareLinkId equals link.Id
            where entry.UserId == userId && entry.WishListId == listId && entry.ArchivedAt == null
                && link.WishListId == listId
            select link).SingleOrDefaultAsync(ct);
    public async Task SaveAsync(Guid userId, Guid listId, Guid? shareId, bool? pinned, DateTimeOffset now, CancellationToken ct)
    {
        var entry = await db.SavedLists.SingleOrDefaultAsync(e => e.UserId == userId && e.WishListId == listId && e.ArchivedAt == null, ct);
        if (entry is not null) { entry.Update(shareId ?? entry.ShareLinkId, pinned ?? entry.IsPinned); await db.SaveChangesAsync(ct); return; }
        entry = new(userId, listId, shareId, pinned ?? false, now); db.SavedLists.Add(entry);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 })
        { db.Entry(entry).State = EntityState.Detached; await SaveAsync(userId, listId, shareId, pinned, now, ct); }
    }
    public async Task RemoveAsync(Guid userId, Guid listId, DateTimeOffset now, CancellationToken ct)
        => _ = await db.SavedLists.Where(e => e.UserId == userId && e.WishListId == listId && e.ArchivedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(e => e.ArchivedAt, now), ct);
    public async Task<SavedListPage> GetAsync(Guid userId, int page, bool pinnedOnly, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await (from entry in db.SavedLists.AsNoTracking()
            join list in db.WishLists.AsNoTracking() on entry.WishListId equals list.Id
            where entry.UserId == userId && entry.ArchivedAt == null && (!pinnedOnly || entry.IsPinned)
            orderby entry.CreatedAt descending, entry.Id
            select new { entry, list, Available = list.ArchivedAt == null && (list.OwnerId == userId || list.IsPublic
                || db.ListShareLinks.Any(link => link.Id == entry.ShareLinkId && link.WishListId == list.Id
                    && link.ArchivedAt == null && link.RevokedAt == null && (link.ExpiresAt == null || link.ExpiresAt > now))) })
            .Skip((page - 1) * 20).Take(21).ToArrayAsync(ct);
        return new(rows.Take(20).Select(r => new SavedListSummary(r.list.Id, r.Available ? r.list.Name : "List unavailable",
            r.Available, r.list.OwnerId == userId, r.entry.IsPinned)).ToArray(), page, rows.Length > 20);
    }
}
