using System.Globalization;
using Microsoft.AspNetCore.Authentication.Cookies;
namespace ListHero.Web.Services;
public static class SessionPolicy
{
    public const string Started = "ListHero.SessionStarted";
    public static void Configure(CookieAuthenticationOptions options)
    {
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        var signing = options.Events.OnSigningIn;
        options.Events.OnSigningIn = async c =>
        {
            await signing(c);
            var started = (options.TimeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            c.Properties.Items[Started] = started;
            if (c.Principal?.Identity is System.Security.Claims.ClaimsIdentity identity)
            {
                foreach (var claim in identity.FindAll(Started).Concat(identity.FindAll("ListHero.SessionId")).ToArray()) identity.RemoveClaim(claim);
                identity.AddClaim(new(Started, started));
                identity.AddClaim(new("ListHero.SessionId", Guid.NewGuid().ToString("N")));
            }
        };
        var validate = options.Events.OnValidatePrincipal;
        options.Events.OnValidatePrincipal = async c =>
        {
            await validate(c);
            if (c.Principal is null) return;
            var origin = c.Properties.IssuedUtc ?? DateTimeOffset.MinValue;
            if (c.Properties.Items.TryGetValue(Started, out var text) && long.TryParse(text, out var seconds)) origin = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if ((options.TimeProvider ?? TimeProvider.System).GetUtcNow() - origin >= TimeSpan.FromDays(90)) c.RejectPrincipal();
            else if (!c.Properties.Items.ContainsKey(Started)) { c.Properties.Items[Started] = origin.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture); c.ShouldRenew = true; }
        };
    }
    public static string SafeReturn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !value.StartsWith('/') || value.StartsWith("//")
            || value.Any(c => char.IsControl(c) || c is '\\' or '#')) return "/dashboard";
        var path = value.Split('?')[0];
        if (path.Contains('%')) return "/dashboard";
        var allowed = path is "/dashboard" or "/lists" or "/saved" or "/profile" or "/discover";
        if (path.StartsWith("/lists/") || path.StartsWith("/view/")) allowed = path.Split('/').Length == 3 && Guid.TryParse(path.Split('/').Last(), out _);
        if (path.StartsWith("/people/")) allowed = path[8..].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') && path.Length is >= 11 and <= 38;
        return allowed ? value : "/dashboard";
    }
}
