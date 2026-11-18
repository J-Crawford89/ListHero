using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using ListHero.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
namespace ListHero.Tests.Web;
public sealed partial class WebHostTests
{
    [Theory]
    [InlineData("/saved?page=2", "/saved?page=2")]
    [InlineData("/profile", "/profile")]
    [InlineData("/discover?q=some%20wishes", "/discover?q=some%20wishes")]
    [InlineData("//attacker.test", "/dashboard")]
    [InlineData("/\\attacker.test", "/dashboard")]
    [InlineData("/account/sign-in", "/dashboard")]
    [InlineData("/%2f/evil", "/dashboard")]
    [InlineData("/view/not-a-guid", "/dashboard")]
    [InlineData("/lists/a/b", "/dashboard")]
    [InlineData("/profile#secret", "/dashboard")]
    [InlineData(null, "/dashboard")]
    public void Return_routes_are_local_and_allowlisted(string? input, string expected) => Assert.Equal(expected, SessionPolicy.SafeReturn(input));

    [Theory]
    [InlineData("true", true)] [InlineData("false", false)]
    public async Task Login_challenge_remembers_choice_and_safe_destination(string choice, bool persistent)
    {
        await using var factory = new WebFactory();
        using var http = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var response = await http.GetAsync("/account/sign-in?returnUrl=%2Fsaved%3Fpage%3D2&rememberMe=" + choice);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        var properties = options.StateDataFormat.Unprotect(query["state"]!)!;
        Assert.Equal(persistent, properties.IsPersistent); Assert.Equal("/saved?page=2", properties.RedirectUri);
        Assert.False(options.UseTokenLifetime);
        Assert.Equal(TimeSpan.FromDays(30), factory.Services.GetRequiredService<IOptions<Microsoft.Identity.Web.TokenCacheProviders.Distributed.MsalDistributedTokenCacheAdapterOptions>>().Value.AbsoluteExpirationRelativeToNow);
    }

    [Fact]
    public async Task Root_routes_by_session_and_marketing_is_always_available()
    {
        await using var factory = new WebFactory();
        using var http = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/")).StatusCode);
        await http.GetAsync("/test/session");
        var root = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode); Assert.Equal("/dashboard", root.Headers.Location!.OriginalString);
        Assert.Contains("no-store", root.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/welcome")).StatusCode);
        Assert.Contains("Remember this device", await http.GetStringAsync("/sign-in"));
    }

    [Fact]
    public async Task Pending_private_return_is_encrypted_bound_to_antiforgery_and_consumed_once()
    {
        await using var factory = new WebFactory();
        using var http = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/account/pending-return", new FormUrlEncodedContent(new Dictionary<string,string> { ["destination"]="/profile" }))).StatusCode);
        await http.GetAsync("/test/session");
        var token = (await http.GetFromJsonAsync<string>("/test/antiforgery"))!;
        var target = $"/view/{Guid.NewGuid()}#private-share-key";
        async Task<HttpResponseMessage> Pending(string destination) => await http.PostAsync("/account/pending-return", new FormUrlEncodedContent(new Dictionary<string,string> { ["destination"] = destination, ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.BadRequest, (await Pending("//evil.test")).StatusCode);
        var response = await Pending(target);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ListHero.PendingReturn"));
        Assert.DoesNotContain("private-share-key", cookie); Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        var challenge = await http.GetAsync("/account/sign-in");
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"];
        Assert.Equal("/account/resume", factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme).StateDataFormat.Unprotect(state!)!.RedirectUri);
        Assert.Equal(target, (await http.GetAsync("/account/resume")).Headers.Location!.OriginalString);
        Assert.Equal("/dashboard", (await http.GetAsync("/account/resume")).Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/account/session", null)).StatusCode);
        var session = await http.PostAsync("/account/session", new FormUrlEncodedContent(new Dictionary<string,string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Fact]
    public async Task Cookie_policy_enforces_absolute_expiry_independently_of_sliding_expiry()
    {
        var clock = new SessionClock(); var options = new CookieAuthenticationOptions { TimeProvider = clock };
        SessionPolicy.Configure(options);
        var scheme = new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new("name", "Test")], "Cookies"));
        var properties = new AuthenticationProperties { IsPersistent = true, IssuedUtc = clock.GetUtcNow() };
        var sign = new CookieSigningInContext(new DefaultHttpContext(), scheme, options, user, properties, new CookieOptions());
        await options.Events.OnSigningIn(sign);
        Assert.True(properties.Items.ContainsKey(SessionPolicy.Started));
        clock.Now = clock.Now.AddDays(89);
        var valid = new CookieValidatePrincipalContext(new DefaultHttpContext(), scheme, options, new AuthenticationTicket(user, properties, "Cookies"));
        await options.Events.OnValidatePrincipal(valid); Assert.NotNull(valid.Principal);
        clock.Now = clock.Now.AddDays(1);
        await options.Events.OnValidatePrincipal(valid); Assert.Null(valid.Principal);
        Assert.Equal(TimeSpan.FromDays(30), options.ExpireTimeSpan); Assert.True(options.SlidingExpiration);
    }
    private sealed class SessionClock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Browser_cookie_persistence_matches_the_selected_mode(bool persistent)
    {
        await using var factory = new WebFactory();
        using var http = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var response = await http.GetAsync("/test/session?persistent=" + persistent.ToString().ToLowerInvariant());
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(".AspNetCore.Cookies="));
        Assert.Equal(persistent, cookie.Contains("expires=", StringComparison.OrdinalIgnoreCase));
        Assert.True(await http.GetFromJsonAsync<bool>("/test/authenticated"));
    }
}
