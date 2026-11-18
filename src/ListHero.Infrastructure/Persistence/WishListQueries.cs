using ListHero.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ListHero.Infrastructure.Persistence;

internal static class WishListQueries
{
    // Access queries exclude disabled owners without modifying retained business records.
    public static IQueryable<WishList> ListsWithActiveOwners(this ListHeroDbContext db)
        => db.WishLists.AsNoTracking().Where(list => list.ArchivedAt == null
            && db.Users.Any(owner => owner.Id == list.OwnerId && owner.ArchivedAt == null));
}
