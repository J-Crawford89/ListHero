using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ListHero.Client.Abstractions;
using ListHero.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.JSInterop;

namespace ListHero.Tests.Web;

public sealed class DraftRecoveryStorageTests
{
    private const string Context = "list:synthetic-private-list:edit-item";
    private const string Secret = "private-draft-canary";
    private static ClaimsPrincipal User(string? id = "account-A", string tenant = "tenant-1") => new(new ClaimsIdentity(id is null ? [] : [new("oid", id), new("tid", tenant)], "fixture"));
    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    [Fact]
    public async Task Drafts_are_encrypted_context_bound_and_recover_across_circuits_until_exact_expiry()
    {
        var js = new Storage(); var keys = new EphemeralDataProtectionProvider(); var clock = new Clock();
        var store = new BrowserDraftRecoveryStore(js, keys, new Identity(User()), clock);
        await store.WriteAsync(Context, Secret);
        var entry = Assert.Single(js.Values);
        Assert.DoesNotContain(Secret, entry.Value.Cipher); Assert.DoesNotContain("account-A", entry.Key); Assert.DoesNotContain("private-list", entry.Key);
        var renewed = new BrowserDraftRecoveryStore(js, keys, new Identity(User()), clock);
        Assert.Equal(Secret, await renewed.ReadAsync(Context));
        Assert.Null(await renewed.ReadAsync("different-form")); Assert.Single(js.Values);
        clock.Now = clock.Now.AddHours(24).AddTicks(-1); Assert.Equal(Secret, await renewed.ReadAsync(Context));
        clock.Now = clock.Now.AddTicks(1); Assert.Null(await renewed.ReadAsync(Context)); Assert.Empty(js.Values);
    }

    [Fact]
    public async Task Other_accounts_tenants_and_anonymous_sessions_cannot_read_an_accounts_draft()
    {
        var js = new Storage(); var keys = new EphemeralDataProtectionProvider(); var clock = new Clock();
        await new BrowserDraftRecoveryStore(js, keys, new Identity(User()), clock).WriteAsync(Context, Secret);
        Assert.Null(await new BrowserDraftRecoveryStore(js, keys, new Identity(User("account-B")), clock).ReadAsync(Context, true));
        Assert.Null(await new BrowserDraftRecoveryStore(js, keys, new Identity(User(tenant: "other-tenant")), clock).ReadAsync(Context));
        Assert.Null(await new BrowserDraftRecoveryStore(js, keys, new Identity(Anonymous()), clock).ReadAsync(Context, true));
        Assert.Single(js.Values);
    }

    [Fact]
    public async Task Anonymous_purchase_drafts_can_be_explicitly_recovered_after_sign_in_and_migrate_on_write()
    {
        var js = new Storage(); var keys = new EphemeralDataProtectionProvider(); var clock = new Clock();
        var guest = new BrowserDraftRecoveryStore(js, keys, new Identity(Anonymous()), clock);
        await guest.WriteAsync(Context, "anonymous-quantity", true);
        var signedIn = new BrowserDraftRecoveryStore(js, keys, new Identity(User()), clock);
        Assert.Null(await signedIn.ReadAsync(Context));
        Assert.Equal("anonymous-quantity", await signedIn.ReadAsync(Context, true));
        await signedIn.WriteAsync(Context, "reviewed-quantity", true); Assert.Single(js.Values);
        Assert.Null(await guest.ReadAsync(Context, true));
        Assert.Equal("reviewed-quantity", await signedIn.ReadAsync(Context, true));
        await signedIn.RemoveAsync(Context, true); Assert.Empty(js.Values);
    }

    [Fact]
    public async Task A_circuit_cannot_rebind_old_forms_to_a_changed_identity()
    {
        var js = new Storage(); var identity = new Identity(User()); var store = new BrowserDraftRecoveryStore(js, new EphemeralDataProtectionProvider(), identity, new Clock());
        await store.WriteAsync(Context, Secret); identity.Principal = User("account-B");
        await Assert.ThrowsAsync<DraftStorageException>(async () => await store.ReadAsync(Context));
        await Assert.ThrowsAsync<DraftStorageException>(async () => await store.WriteAsync(Context, "must-not-rebind"));
        await Assert.ThrowsAsync<DraftStorageException>(async () => await store.RemoveAsync(Context));
        Assert.Single(js.Values);
    }

    [Theory]
    [InlineData("missing-identity")] [InlineData("anonymous-owner-form")]
    [InlineData("invalid-context")] [InlineData("large-context")] [InlineData("oversized")]
    public async Task Invalid_settings_are_refused_without_writing(string issue)
    {
        var js = new Storage(); var principal = issue == "missing-identity" ? User(null) : issue == "anonymous-owner-form" ? Anonymous() : User();
        var store = new BrowserDraftRecoveryStore(js, new EphemeralDataProtectionProvider(), new Identity(principal), new Clock());
        await Assert.ThrowsAsync<DraftStorageException>(async () => await store.WriteAsync(issue == "invalid-context" ? " " : issue == "large-context" ? new string('c', 257) : Context,
            issue == "oversized" ? new string('x', BrowserDraftRecoveryStore.MaximumBytes + 1) : Secret));
        Assert.Empty(js.Values);
    }

    [Theory]
    [InlineData("tamper")] [InlineData("lost-key")]
    [InlineData("bad-json")] [InlineData("wrong-version")] [InlineData("null-content")]
    public async Task Unreadable_or_unsupported_drafts_are_removed_without_revealing_plaintext(string issue)
    {
        var js = new Storage(); var keys = new EphemeralDataProtectionProvider(); var clock = new Clock();
        var store = new BrowserDraftRecoveryStore(js, keys, new Identity(User()), clock); await store.WriteAsync(Context, Secret);
        var entry = Assert.Single(js.Values);
        if (issue == "tamper") js.Values[entry.Key] = ("corrupted", entry.Value.Expires);
        else if (issue != "lost-key")
        {
            var parts = entry.Key.Split('.');
            var payload = issue == "bad-json" ? "{" : JsonSerializer.Serialize(new { Version = issue == "wrong-version" ? 2 : 1, Expires = clock.Now.AddHours(1), Content = (string?)null });
            js.Values[entry.Key] = (keys.CreateProtector("ListHero.DraftRecovery.v1", parts[3], parts[4]).Protect(payload), entry.Value.Expires);
        }
        var reader = issue == "lost-key" ? new BrowserDraftRecoveryStore(js, new EphemeralDataProtectionProvider(), new Identity(User()), clock) : store;
        Assert.Null(await reader.ReadAsync(Context)); Assert.Empty(js.Values);
    }

    [Fact]
    public async Task Renaming_ciphertext_to_another_forms_key_cannot_change_its_scope()
    {
        var js = new Storage(); var keys = new EphemeralDataProtectionProvider(); var store = new BrowserDraftRecoveryStore(js, keys, new Identity(User()), new Clock());
        await store.WriteAsync(Context, Secret); var entry = Assert.Single(js.Values);
        var copied = entry.Key[..^64] + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("other-form")));
        js.Values[copied] = entry.Value;
        Assert.Null(await store.ReadAsync("other-form")); Assert.Single(js.Values); Assert.Equal(Secret, await store.ReadAsync(Context));
    }

    [Fact]
    public async Task Storage_limits_expired_cleanup_and_failed_storage_have_safe_recovery()
    {
        var js = new Storage(); var clock = new Clock(); var store = new BrowserDraftRecoveryStore(js, new EphemeralDataProtectionProvider(), new Identity(User()), clock);
        for (var index = 0; index < 32; index++) await store.WriteAsync("form-" + index, Secret);
        await Assert.ThrowsAsync<DraftStorageException>(async () => await store.WriteAsync("too-many", Secret)); Assert.Equal(32, js.Values.Count);
        clock.Now = clock.Now.AddHours(24);
        await store.WriteAsync("new", new string('x', BrowserDraftRecoveryStore.MaximumBytes)); Assert.Single(js.Values);
        Assert.Equal(BrowserDraftRecoveryStore.MaximumBytes, (await store.ReadAsync("new"))!.Length);
        js.Blocked = true;
        foreach (var action in new Func<Task>[] { async () => { await store.ReadAsync("new"); }, async () => await store.WriteAsync("new", Secret), async () => await store.RemoveAsync("new") })
        {
            var error = await Assert.ThrowsAsync<DraftStorageException>(action); Assert.DoesNotContain("private-storage-canary", error.Message);
        }
    }

    private sealed class Identity(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public ClaimsPrincipal Principal = principal;
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(Principal));
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Storage : IJSRuntime
    {
        public Dictionary<string, (string Cipher, long Expires)> Values { get; } = [];
        public bool Blocked;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            if (Blocked) throw new JSException("private-storage-canary");
            object? result = null;
            var key = args?.Length > 0 ? (string)args[0]! : "";
            switch (identifier)
            {
                case "ListHeroDraftStorage.anonymous": result = "synthetic-browser-namespace"; break;
                case "ListHeroDraftStorage.get": result = Values.GetValueOrDefault(key).Cipher; break;
                case "ListHeroDraftStorage.remove": Values.Remove(key); break;
                case "ListHeroDraftStorage.prune":
                    foreach (var old in Values.Where(entry => entry.Key.StartsWith(key) && entry.Value.Expires <= (long)args![1]!).Select(entry => entry.Key).ToArray()) Values.Remove(old);
                    break;
                case "ListHeroDraftStorage.set":
                    if (!Values.ContainsKey(key) && Values.Keys.Count(value => value.StartsWith((string)args![2]!)) >= 32) throw new JSException("quota");
                    Values[key] = ((string)args![1]!, (long)args[3]!); break;
                default: throw new InvalidOperationException(identifier);
            }
            return ValueTask.FromResult(result is null ? default! : (TValue)result);
        }
    }
}
