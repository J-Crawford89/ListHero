using System.Reflection;
using System.Security.Claims;
using ListHero.Client.Abstractions.Api;
using ListHero.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Microsoft.JSInterop;

namespace ListHero.Tests.Web;

public sealed class WebServiceTests
{
    [Fact]
    public async Task Guest_credential_is_protected_survives_a_new_store_and_can_be_cleared()
    {
        var js = new StorageRuntime();
        var protection = new EphemeralDataProtectionProvider();
        var first = Store(js, protection);
        Assert.Null(await first.ReadAsync());
        await first.WriteAsync("guest-secret");
        Assert.DoesNotContain("guest-secret", js.Values["listhero.guest.v1"]!);
        var reopened = Store(js, protection);
        Assert.Equal("guest-secret", await reopened.ReadAsync());
        await reopened.ClearAsync();
        Assert.Null(await first.ReadAsync());
    }

    [Fact]
    public async Task Corrupt_or_unreadable_storage_recovers_without_claiming_another_guests_marks()
    {
        var js = new StorageRuntime();
        js.Values["listhero.guest.v1"] = "corrupt-protected-value";
        var store = Store(js, new EphemeralDataProtectionProvider());
        Assert.Null(await store.ReadAsync());
        Assert.Empty(js.Values);
        js.Fail = true;
        Assert.Null(await store.ReadAsync());
        Assert.Contains("browser storage", (await Assert.ThrowsAsync<ApiRequestException>(() => store.WriteAsync("guest-secret").AsTask())).Message);
    }

    [Fact]
    public async Task Lost_protection_keys_clear_an_unrecoverable_credential()
    {
        var js = new StorageRuntime();
        await Store(js, new EphemeralDataProtectionProvider()).WriteAsync("guest-secret");
        Assert.Null(await Store(js, new EphemeralDataProtectionProvider()).ReadAsync());
        Assert.Empty(js.Values);
    }

    [Theory]
    [InlineData("Development", "isolated-guest")]
    [InlineData("Production", "listhero.guest.v1")]
    public async Task Storage_key_overrides_are_confined_to_development(string environment, string expectedKey)
    {
        var js = new StorageRuntime();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Development:GuestStorageKey"] = "isolated-guest" }).Build();
        var store = new BrowserGuestCredentialStore(new(js, new EphemeralDataProtectionProvider()), config, new HostEnvironment(environment));
        await store.WriteAsync("guest-secret");
        Assert.True(js.Values.ContainsKey(expectedKey));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Web_token_provider_requires_authentication_and_uses_the_current_principal(bool authenticated)
    {
        var acquisition = DispatchProxy.Create<ITokenAcquisition, AcquisitionProxy>();
        var proxy = (AcquisitionProxy)acquisition;
        var state = new UserState(authenticated);
        var config = Configuration();
        var provider = new WebApiAccessTokenProvider(state, acquisition, config);
        using var cancellation = new CancellationTokenSource();
        if (!authenticated)
        {
            await Assert.ThrowsAsync<SignInRequiredException>(() => provider.GetAsync(cancellation.Token));
            Assert.Null(proxy.Arguments);
        }
        else
        {
            Assert.Equal("access-token", await provider.GetAsync(cancellation.Token));
            Assert.Contains(proxy.Arguments!, argument => ReferenceEquals(argument, state.User));
            Assert.Contains(proxy.Arguments!, argument => argument is IEnumerable<string> scopes && scopes.SequenceEqual(new[] { "api://test/access_as_user" }));
            var options = Assert.Single(proxy.Arguments!.OfType<TokenAcquisitionOptions>());
            Assert.Equal(cancellation.Token, options.CancellationToken);
        }
    }

    [Fact]
    public async Task Renewal_requiring_interaction_becomes_sign_in_required_and_does_not_hide_other_failures()
    {
        var acquisition = DispatchProxy.Create<ITokenAcquisition, AcquisitionProxy>();
        var proxy = (AcquisitionProxy)acquisition;
        var provider = new WebApiAccessTokenProvider(new UserState(true), acquisition, Configuration());
        proxy.Failure = new MsalUiRequiredException("interaction_required", "Test session requires renewal");
        await Assert.ThrowsAsync<SignInRequiredException>(() => provider.GetAsync());
        proxy.Failure = new MicrosoftIdentityWebChallengeUserException(new MsalUiRequiredException("interaction_required", "Test renewal"), ["api://test/access_as_user"], "OpenIdConnect");
        await Assert.ThrowsAsync<SignInRequiredException>(() => provider.GetAsync());
        proxy.Failure = new HttpRequestException("Test identity provider unavailable");
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetAsync());
    }

    [Fact]
    public async Task Missing_scopes_or_unconfigured_authentication_never_yield_a_token()
    {
        var acquisition = DispatchProxy.Create<ITokenAcquisition, AcquisitionProxy>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WebApiAccessTokenProvider(new UserState(true), acquisition, new ConfigurationBuilder().Build()).GetAsync());
        await Assert.ThrowsAsync<SignInRequiredException>(() => new UnconfiguredApiAccessTokenProvider().GetAsync());
    }

    private static BrowserGuestCredentialStore Store(StorageRuntime js, IDataProtectionProvider protection)
        => new(new ProtectedLocalStorage(js, protection), new ConfigurationBuilder().Build(), new HostEnvironment("Testing"));
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ListHeroApi:Scopes:0"] = "api://test/access_as_user" }).Build();

    public class AcquisitionProxy : DispatchProxy
    {
        public object?[]? Arguments { get; private set; }
        public Exception? Failure { get; set; }
        protected override object Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal("GetAccessTokenForUserAsync", targetMethod!.Name);
            Arguments = args;
            return Failure is null ? Task.FromResult("access-token") : Task.FromException<string>(Failure);
        }
    }
    private sealed class UserState : AuthenticationStateProvider
    {
        public ClaimsPrincipal User { get; }
        public UserState(bool authenticated) => User = new(new ClaimsIdentity([new("oid", Guid.NewGuid().ToString())], authenticated ? "Test" : null));
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(User));
    }
    private sealed class StorageRuntime : IJSRuntime
    {
        public Dictionary<string, string?> Values { get; } = [];
        public bool Fail { get; set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            if (Fail) throw new JSException("Test storage blocked");
            var key = (string)args![0]!;
            object? result = null;
            switch (identifier)
            {
                case "localStorage.getItem": Values.TryGetValue(key, out var value); result = value; break;
                case "localStorage.setItem": Values[key] = (string?)args[1]; break;
                case "localStorage.removeItem": Values.Remove(key); break;
                default: throw new InvalidOperationException(identifier);
            }
            return ValueTask.FromResult(result is null ? default! : (TValue)result);
        }
    }
    private sealed class HostEnvironment(string name) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "ListHero.Web";
        public string ContentRootPath { get; set; } = ".";
        public string WebRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
