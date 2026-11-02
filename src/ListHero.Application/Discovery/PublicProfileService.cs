using ListHero.Application.Identity;
using ListHero.Application.Lists;
using ListHero.Contracts.Discovery;
using ListHero.Domain.Entities;

namespace ListHero.Application.Discovery;

public sealed class HandleUnavailableException : Exception;
public sealed class PublicProfileRequiredException : Exception;
public interface IPublicProfileStore
{
    Task SaveAsync(User user, byte[] expectedVersion, CancellationToken ct);
}
public interface IPublicProfileService
{
    Task<OwnProfileResponse> GetAsync(ExternalUserIdentity identity, CancellationToken ct = default);
    Task<OwnProfileResponse> UpdateAsync(ExternalUserIdentity identity, UpdatePublicProfileRequest request, CancellationToken ct = default);
}
public sealed class PublicProfileService(IUserService users, IPublicProfileStore store) : IPublicProfileService
{
    public async Task<OwnProfileResponse> GetAsync(ExternalUserIdentity identity, CancellationToken ct = default)
        => Response(await users.ResolveAsync(identity, ct));

    public async Task<OwnProfileResponse> UpdateAsync(ExternalUserIdentity identity, UpdatePublicProfileRequest request, CancellationToken ct = default)
    {
        var user = await users.ResolveAsync(identity, ct);
        var version = WishListService.RequireVersion(user.RowVersion, request.RowVersion);
        if (!request.ConfirmDiscovery) throw new ArgumentException("Confirm that your public name and public lists can appear in discovery.");
        user.UpdatePublicProfile(request.DisplayName, request.Handle);
        await store.SaveAsync(user, version, ct);
        return Response(user);
    }

    private static OwnProfileResponse Response(User user)
        => new(user.PublicDisplayName, user.PublicHandle, user.DisplayName, Convert.ToBase64String(user.RowVersion));
}
