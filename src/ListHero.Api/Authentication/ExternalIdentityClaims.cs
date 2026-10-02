using System.Security.Claims;
using ListHero.Application.Identity;

namespace ListHero.Api.Authentication;

public static class ExternalIdentityClaims
{
    public static ExternalUserIdentity ToExternalIdentity(this ClaimsPrincipal principal)
    {
        var issuer = principal.FindFirst("iss")?.Value;
        if (principal.Identity?.IsAuthenticated != true || !Guid.TryParse(principal.FindFirst("oid")?.Value, out var objectId)
            || objectId == Guid.Empty || !Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri)
            || issuerUri.Scheme != Uri.UriSchemeHttps)
            throw new UnauthorizedAccessException("A validated external user identity is required.");

        var name = principal.FindFirst("name")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "List Hero user";
        return new(issuer!, objectId.ToString(), name[..Math.Min(name.Length, 200)]);
    }
}
