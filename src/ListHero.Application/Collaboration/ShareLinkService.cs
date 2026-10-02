using ListHero.Application.Common;
using ListHero.Application.Identity;
using ListHero.Application.Lists;
using ListHero.Application.Security;
using ListHero.Contracts.Lists;
using ListHero.Domain.Entities;

namespace ListHero.Application.Collaboration;

public sealed class ShareLinkService(IUserService users, IWishListStore lists, IShareLinkStore store,
    ICapabilityTokenService tokens, TimeProvider clock) : IShareLinkService
{
    public async Task<IReadOnlyList<ShareLinkResponse>> GetAsync(ExternalUserIdentity owner, Guid listId, CancellationToken ct = default)
    {
        await RequireOwnerAsync(owner, listId, ct);
        return (await store.GetLinksAsync(listId, ct)).Select(ToResponse).ToArray();
    }
    public async Task<ShareLinkResponse> CreateAsync(ExternalUserIdentity owner, Guid listId, CreateShareLinkRequest request, CancellationToken ct = default)
    {
        await RequireOwnerAsync(owner, listId, ct);
        var token = tokens.Issue();
        var link = new ListShareLink(listId, token.Hash, tokens.ProtectShareToken(token.Token), clock.GetUtcNow(), request.ExpiresAt);
        await store.AddLinkAsync(link, ct);
        return ToResponse(link);
    }
    public async Task RevokeAsync(ExternalUserIdentity owner, Guid listId, Guid linkId, CancellationToken ct = default)
    {
        await RequireOwnerAsync(owner, listId, ct);
        await store.RevokeLinkAsync(listId, linkId, clock.GetUtcNow(), ct);
    }
    private async Task RequireOwnerAsync(ExternalUserIdentity owner, Guid listId, CancellationToken ct)
    {
        var user = await users.ResolveAsync(owner, ct);
        _ = await lists.GetOwnedAsync(listId, user.Id, ct) ?? throw new ResourceNotFoundException();
    }
    private ShareLinkResponse ToResponse(ListShareLink link)
    {
        var active = link.IsActive(clock.GetUtcNow());
        return new(link.Id, active ? tokens.UnprotectShareToken(link.ProtectedToken) : null, link.CreatedAt, link.ExpiresAt, link.RevokedAt, active);
    }
}
