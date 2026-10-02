using ListHero.Domain.Common;

namespace ListHero.Domain.Entities;

public sealed class User : Entity
{
    public string IdentityIssuer { get; private set; } = string.Empty;
    public string IdentitySubject { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;

    private User() { }

    public User(string identityIssuer, string identitySubject, string displayName, DateTimeOffset createdAt)
        : base(createdAt)
    {
        IdentityIssuer = Guard.Required(identityIssuer, 200, nameof(identityIssuer));
        IdentitySubject = Guard.Required(identitySubject, 200, nameof(identitySubject));
        DisplayName = Guard.Required(displayName, 200, nameof(displayName));
    }
}
