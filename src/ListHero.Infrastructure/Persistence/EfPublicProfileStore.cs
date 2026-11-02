using ListHero.Application.Common;
using ListHero.Application.Discovery;
using ListHero.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ListHero.Infrastructure.Persistence;

public sealed class EfPublicProfileStore(ListHeroDbContext db) : IPublicProfileStore
{
    public async Task SaveAsync(User user, byte[] expectedVersion, CancellationToken ct)
    {
        db.Users.Update(user);
        db.Entry(user).Property(u => u.RowVersion).OriginalValue = expectedVersion;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new EditConflictException(); }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 })
        { throw new HandleUnavailableException(); }
    }
}
