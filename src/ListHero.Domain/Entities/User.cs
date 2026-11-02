using ListHero.Domain.Common;

namespace ListHero.Domain.Entities;

public sealed class User : Entity
{
    public string IdentityIssuer { get; private set; } = string.Empty;
    public string IdentitySubject { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string? PublicDisplayName { get; private set; }
    public string? PublicHandle { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public bool HasPublicProfile => PublicDisplayName is not null && PublicHandle is not null;

    public static string NormalizeHandle(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var handle = value.Trim().ToLowerInvariant();
        if (handle.StartsWith('@')) handle = handle[1..];
        if (handle.Length is < 3 or > 30 || !char.IsAsciiLetterOrDigit(handle[0])
            || handle.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("Choose a handle of 3–30 letters, numbers, underscores or hyphens, starting with a letter or number.");
        return handle;
    }

    public void UpdatePublicProfile(string name, string handle)
    {
        var publicName = Guard.Required(name, 100, nameof(name));
        var normalized = NormalizeHandle(handle);
        if (PublicHandle is not null && PublicHandle != normalized)
            throw new ArgumentException("Your public handle cannot be changed after it is chosen.");
        PublicDisplayName = publicName;
        PublicHandle = normalized;
    }

    private User() { }

    public User(string identityIssuer, string identitySubject, string displayName, DateTimeOffset createdAt)
        : base(createdAt)
    {
        IdentityIssuer = Guard.Required(identityIssuer, 200, nameof(identityIssuer));
        IdentitySubject = Guard.Required(identitySubject, 200, nameof(identitySubject));
        DisplayName = Guard.Required(displayName, 200, nameof(displayName));
    }
}
