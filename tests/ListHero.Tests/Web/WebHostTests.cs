using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using ListHero.Web;
using ListHero.Client.Abstractions.Api;
using ListHero.Client.Api;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ListHero.Tests.Web;

public sealed partial class WebHostTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Sign_in_challenge_preserves_only_the_expected_local_return_route(bool sharedList)
    {
        await using var factory = new WebFactory();
        using var http = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var id = Guid.NewGuid();
        var response = await http.GetAsync("/account/sign-in" + (sharedList ? $"?listId={id}" : "?returnUrl=https://attacker.example/"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("identity.example.test", response.Headers.Location!.Host);
        var query = QueryHelpers.ParseQuery(response.Headers.Location.Query);
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        var properties = options.StateDataFormat.Unprotect(query["state"]!);
        Assert.NotNull(properties);
        Assert.Equal(sharedList ? $"/view/{id}" : "/lists", properties.RedirectUri);
        Assert.Equal("https://localhost/signin-oidc", query["redirect_uri"].ToString());
    }

    [Fact]
    public async Task Sign_out_requires_a_valid_antiforgery_token_and_clears_the_actual_cookie()
    {
        await using var factory = new WebFactory();
        using var http = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync("/test/session")).StatusCode);
        Assert.True(await http.GetFromJsonAsync<bool>("/test/authenticated"));
        var missing = await http.PostAsync("/account/sign-out", null);
        Assert.True(missing.StatusCode == HttpStatusCode.BadRequest, $"Missing antiforgery returned {missing.StatusCode}; redirect={missing.Headers.Location}; body={await missing.Content.ReadAsStringAsync()}");
        Assert.True(await http.GetFromJsonAsync<bool>("/test/authenticated"));
        var token = await http.GetFromJsonAsync<string>("/test/antiforgery");
        using var forged = new HttpRequestMessage(HttpMethod.Post, "/account/sign-out");
        forged.Headers.Add("RequestVerificationToken", "forged-token");
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(forged)).StatusCode);
        using var valid = new HttpRequestMessage(HttpMethod.Post, "/account/sign-out");
        valid.Headers.Add("RequestVerificationToken", token);
        var response = await http.SendAsync(valid);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/logout", response.Headers.Location!.AbsolutePath);
        Assert.False(await http.GetFromJsonAsync<bool>("/test/authenticated"));
    }

    [Fact]
    public async Task Shared_pages_are_rendered_with_no_store_and_no_referrer_headers()
    {
        await using var factory = new WebFactory();
        using var http = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var response = await http.GetAsync($"/view/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.DoesNotContain("Fulfilled", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Web_host_cannot_run_outside_development_with_authentication_disabled()
    {
        using var factory = new WebFactory(enabled: false);
        Assert.Contains("Configure Entra External ID", Assert.Throws<InvalidOperationException>(() => factory.CreateClient()).Message);
    }

    private sealed class WebFactory(bool enabled = true, Func<HttpMessageHandler>? apiHandler = null) : WebApplicationFactory<WebAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseStaticWebAssets();
            builder.UseEnvironment("Testing").UseSetting("Authentication:Enabled", enabled.ToString())
                .UseSetting("EntraExternalId:Instance", "https://login.microsoftonline.com/")
                .UseSetting("EntraExternalId:TenantId", "11111111-1111-1111-1111-111111111111")
                .UseSetting("EntraExternalId:ClientId", "22222222-2222-2222-2222-222222222222")
                .UseSetting("EntraExternalId:ClientSecret", "test-only-never-used-for-network-access");
            builder.ConfigureTestServices(services =>
            {
                if (apiHandler is not null)
                {
                    services.AddScoped<IApiAccessTokenProvider, BrowserTokens>();
                    services.AddHttpClient<IWishListApi, WishListApiClient>().ConfigurePrimaryHttpMessageHandler(apiHandler);
                    services.AddHttpClient<IListCollaborationApi, ListCollaborationApiClient>().ConfigurePrimaryHttpMessageHandler(apiHandler);
                }
                services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                {
                    var configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = "https://identity.example.test/",
                        AuthorizationEndpoint = "https://identity.example.test/authorize",
                        TokenEndpoint = "https://identity.example.test/token",
                        EndSessionEndpoint = "https://identity.example.test/logout"
                    };
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
                services.AddSingleton<IStartupFilter, SessionFixture>();
            });
        }
    }

    private sealed class BrowserTokens(AuthenticationStateProvider authentication) : IApiAccessTokenProvider
    {
        public async Task<string> GetAsync(CancellationToken ct = default)
        {
            var user = (await authentication.GetAuthenticationStateAsync()).User;
            if (user.Identity?.IsAuthenticated != true) throw new SignInRequiredException();
            return "browser-" + user.FindFirst("oid")!.Value;
        }
    }

    // Fixture routes exist solely in the test host. Real middleware signs and validates the cookie.
    private sealed class SessionFixture : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, remaining) =>
            {
                if (!context.Request.Path.StartsWithSegments("/test")) { await remaining(context); return; }
                if (context.Request.Path == "/test/session")
                {
                    var principal = new ClaimsPrincipal(new ClaimsIdentity([new("oid", Guid.NewGuid().ToString()), new("name", "Test owner")], CookieAuthenticationDefaults.AuthenticationScheme));
                    var properties = new AuthenticationProperties();
                    properties.Items[".AuthScheme"] = OpenIdConnectDefaults.AuthenticationScheme;
                    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
                    context.Response.StatusCode = 204;
                    return;
                }
                var authentication = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                context.User = authentication.Principal ?? new ClaimsPrincipal();
                if (context.Request.Path == "/test/authenticated")
                    await context.Response.WriteAsJsonAsync(authentication.Succeeded);
                else if (context.Request.Path == "/test/antiforgery")
                    await context.Response.WriteAsJsonAsync(context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context).RequestToken);
                else context.Response.StatusCode = 404;
            });
            next(app);
        };
    }
}
