using ListHero.Domain.Entities;

namespace ListHero.Application.Identity;

public interface IUserService
{
    Task<User> ResolveAsync(ExternalUserIdentity identity, CancellationToken cancellationToken = default);
}

public interface IUserStore
{
    Task<User> GetOrCreateAsync(ExternalUserIdentity identity, DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
