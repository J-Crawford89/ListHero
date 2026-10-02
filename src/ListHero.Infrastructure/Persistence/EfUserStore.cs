using ListHero.Application.Identity;
using ListHero.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ListHero.Infrastructure.Persistence;

public sealed class EfUserStore(ListHeroDbContext db) : IUserStore
{
    public async Task<User> GetOrCreateAsync(ExternalUserIdentity identity, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindAsync(identity, cancellationToken);
        if (existing is not null) return existing;
        var user = new User(identity.Issuer, identity.ObjectId, identity.DisplayName, now);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return user;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Simultaneous first requests can race the unique identity index.
            db.Entry(user).State = EntityState.Detached;
            var winner = await FindAsync(identity, cancellationToken);
            if (winner is null) throw;
            return winner;
        }
    }

    private Task<User?> FindAsync(ExternalUserIdentity identity, CancellationToken cancellationToken)
        => db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.IdentityIssuer == identity.Issuer
            && user.IdentitySubject == identity.ObjectId, cancellationToken);
}
