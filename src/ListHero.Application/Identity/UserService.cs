using ListHero.Application.Common;
using ListHero.Domain.Entities;

namespace ListHero.Application.Identity;

public sealed class UserService(IUserStore store, TimeProvider clock) : IUserService
{
    public async Task<User> ResolveAsync(ExternalUserIdentity identity, CancellationToken cancellationToken = default)
    {
        var user = await store.GetOrCreateAsync(identity, clock.GetUtcNow(), cancellationToken);
        if (user.IsArchived) throw new AccountUnavailableException();
        return user;
    }
}
