namespace ListHero.Application.Identity;

// Only the API authentication boundary constructs this from validated token claims.
public sealed record ExternalUserIdentity(string Issuer, string ObjectId, string DisplayName);
