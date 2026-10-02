using ListHero.Application.Authorization;
using ListHero.Application.Common;
using ListHero.Application.Identity;
using ListHero.Application.Security;
using ListHero.Domain.Entities;

namespace ListHero.Application.Collaboration;

public sealed class ListAccessResolver(IListViewerStore store, IUserService users,
    ICapabilityTokenService tokens, IListAccessService permissions, TimeProvider clock) : IListAccessResolver
{
    public async Task<ResolvedListAccess> ResolveAsync(Guid listId, ListRequestAccess request, CancellationToken ct = default)
    {
        var user = request.User is null ? null : await users.ResolveAsync(request.User, ct);
        var list = await store.GetActiveListAsync(listId, ct) ?? throw new ResourceNotFoundException();
        ListShareLink? share = null;
        GuestIdentity? guest = null;
        if (IsPlausible(request.ShareToken))
        {
            var candidate = await store.GetShareByHashAsync(tokens.HashForLookup(request.ShareToken!), ct);
            if (candidate is not null && tokens.Matches(request.ShareToken!, candidate.TokenHash)) share = candidate;
        }
        if (IsPlausible(request.GuestCredential))
        {
            var candidate = await store.GetGuestByHashAsync(tokens.HashForLookup(request.GuestCredential!), ct);
            if (candidate is not null && tokens.Matches(request.GuestCredential!, candidate.CredentialHash)) guest = candidate;
        }
        var viewer = new ViewerIdentity(user?.Id, guest?.Id);
        var decision = permissions.Evaluate(list, viewer, share, clock.GetUtcNow());
        if (!decision.CanView) throw new ResourceNotFoundException();
        return new(list, viewer, share, decision);
    }

    private static bool IsPlausible(string? token) => !string.IsNullOrWhiteSpace(token) && token.Length <= 128;
}
