using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using ListHero.Application.Identity;
using ListHero.Application.Lists;
using ListHero.Contracts.Lists;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ListHero.Tests.Api;

public sealed partial class OwnerWorkflowTests
{
    [Theory]
    [InlineData("valid", 200)] [InlineData("signature", 401)] [InlineData("audience", 401)]
    [InlineData("issuer", 401)] [InlineData("expired", 401)] [InlineData("scope", 403)]
    public async Task Actual_JWT_middleware_validates_signature_issuer_audience_lifetime_and_scope(string variant, int status)
    {
        await using var factory = new TokenApiFactory();
        using var http = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        http.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(Guid.NewGuid(), variant));
        Assert.Equal((HttpStatusCode)status, (await http.GetAsync("/api/session")).StatusCode);
    }

    [Fact]
    public async Task Two_independently_signed_customer_tokens_remain_isolated()
    {
        await using var factory = new TokenApiFactory();
        using var owner = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var other = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        owner.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(Guid.NewGuid(), "valid"));
        other.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(Guid.NewGuid(), "valid"));
        var list = await JsonAsync<OwnerWishListResponse>(await owner.PostAsJsonAsync("/api/lists/", new CreateWishListRequest { Name = "Private" }));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/lists/{list.Id}/owner")).StatusCode);
        Assert.Empty((await other.GetFromJsonAsync<WishListSummaryResponse[]>("/api/lists/mine"))!);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/lists/{list.Id}/owner")).StatusCode);
    }

    private sealed class TokenApiFactory : WebApplicationFactory<ListHero.Api.ApiAssemblyMarker>
    {
        private const string Tenant = "11111111-1111-1111-1111-111111111111", Audience = "22222222-2222-2222-2222-222222222222";
        private const string Issuer = "https://login.microsoftonline.com/" + Tenant + "/v2.0";
        private readonly RSA rsa = RSA.Create(2048);
        private RsaSecurityKey Key => new(rsa) { KeyId = "test-key" };
        public string Token(Guid user, string variant)
        {
            using var wrongKey = RSA.Create(2048);
            var now = DateTime.UtcNow;
            var claims = new[] { new Claim("oid", user.ToString()), new Claim("tid", Tenant), new Claim("ver", "2.0"), new Claim("scp", variant == "scope" ? "unrelated" : "access_as_user") };
            var token = new JwtSecurityToken(variant == "issuer" ? "https://attacker.example/" : Issuer,
                variant == "audience" ? Guid.NewGuid().ToString() : Audience, claims,
                now.AddHours(-2), variant == "expired" ? now.AddHours(-1) : now.AddMinutes(5),
                new SigningCredentials(variant == "signature" ? new RsaSecurityKey(wrongKey) { KeyId = "test-key" } : Key, SecurityAlgorithms.RsaSha256));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing").UseSetting("Authentication:Enabled", "true")
                .UseSetting("EntraExternalId:Instance", "https://login.microsoftonline.com/")
                .UseSetting("EntraExternalId:TenantId", Tenant).UseSetting("EntraExternalId:ClientId", Audience);
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    configuration.SigningKeys.Add(Key);
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    options.TokenValidationParameters.ValidIssuer = Issuer;
                    // Use the fixture's exact issuer instead of fetching Entra tenant metadata.
                    // Signature, audience, issuer, lifetime, claims and scope validation remain enabled.
                    options.TokenValidationParameters.IssuerValidator = null;
                });
                services.RemoveAll<IUserStore>(); services.RemoveAll<IWishListStore>();
                services.AddSingleton<IUserStore, MemoryUserStore>(); services.AddSingleton<IWishListStore, MemoryListStore>();
            });
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) rsa.Dispose(); }
    }
}
