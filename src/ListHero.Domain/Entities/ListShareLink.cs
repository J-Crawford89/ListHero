using ListHero.Domain.Common;

namespace ListHero.Domain.Entities;

public sealed class ListShareLink : Entity
{
    public Guid WishListId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public string ProtectedToken { get; private set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    private ListShareLink() { }

    public ListShareLink(Guid wishListId, string tokenHash, string protectedToken,
        DateTimeOffset createdAt, DateTimeOffset? expiresAt = null) : base(createdAt)
    {
        WishListId = Guard.Id(wishListId, nameof(wishListId));
        TokenHash = Guard.Required(tokenHash, 64, nameof(tokenHash));
        ProtectedToken = Guard.Required(protectedToken, 2048, nameof(protectedToken));
        if (expiresAt <= createdAt) throw new ArgumentOutOfRangeException(nameof(expiresAt));
        ExpiresAt = expiresAt;
    }

    public bool IsActive(DateTimeOffset now) => !IsArchived && RevokedAt is null
        && (ExpiresAt is null || ExpiresAt > now);

    public void Revoke(DateTimeOffset revokedAt) => RevokedAt ??= revokedAt;
}
