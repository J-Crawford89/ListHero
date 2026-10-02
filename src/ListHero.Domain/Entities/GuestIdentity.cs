using ListHero.Domain.Common;

namespace ListHero.Domain.Entities;

public sealed class GuestIdentity : Entity
{
    public string CredentialHash { get; private set; } = string.Empty;

    private GuestIdentity() { }

    public GuestIdentity(string credentialHash, DateTimeOffset createdAt) : base(createdAt)
        => CredentialHash = Guard.Required(credentialHash, 64, nameof(credentialHash));
}
