using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ListHero.Client.Abstractions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.JSInterop;

namespace ListHero.Web.Services;

public sealed class BrowserDraftRecoveryStore(IJSRuntime js, IDataProtectionProvider protection,
    AuthenticationStateProvider authentication, TimeProvider clock) : IDraftRecoveryStore
{
    public const int MaximumBytes = 64 * 1024;
    private readonly SemaphoreSlim gate = new(1);
    private string? circuitIdentity;
    private string? anonymousIdentity;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Prefix(string owner) => "listhero.draft.v1." + owner + ".";
    private IDataProtector Protector(string owner, string context) => protection.CreateProtector("ListHero.DraftRecovery.v1", owner, context);
    private sealed record Envelope(int Version, DateTimeOffset Expires, string Content);

    private async Task<(string Owner, bool Authenticated)> Identity(bool allowAnonymous)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        string identity;
        var authenticated = user.Identity?.IsAuthenticated == true;
        if (authenticated)
        {
            var subject = user.FindFirst("oid")?.Value ?? user.FindFirst("sub")?.Value;
            if (string.IsNullOrWhiteSpace(subject)) throw new DraftStorageException();
            identity = JsonSerializer.Serialize(new[] { "account", user.FindFirst("tid")?.Value ?? user.FindFirst("iss")?.Value ?? user.Identity!.AuthenticationType, subject });
        }
        else
        {
            if (!allowAnonymous) throw new DraftStorageException();
            identity = await AnonymousIdentity();
        }
        // A circuit must never rebind old form data to a newly selected account.
        if (circuitIdentity is not null && circuitIdentity != identity) throw new DraftStorageException();
        circuitIdentity = identity;
        return (Hash(identity), authenticated);
    }

    private static string Context(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) throw new DraftStorageException();
        return Hash(value);
    }
    private string? Decode(string owner, string context, string cipher)
    {
        try
        {
            if (cipher.Length > MaximumBytes * 4) return null;
            var envelope = JsonSerializer.Deserialize<Envelope>(Protector(owner, context).Unprotect(cipher));
            return envelope is { Version: 1 } && envelope.Expires > clock.GetUtcNow()
                && Encoding.UTF8.GetByteCount(envelope.Content) <= MaximumBytes ? envelope.Content : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException) { return null; }
    }
    private async ValueTask<string?> Read(string owner, string context)
    {
        var key = Prefix(owner) + context;
        var cipher = await js.InvokeAsync<string?>("ListHeroDraftStorage.get", key);
        if (cipher is null) return null;
        var content = Decode(owner, context, cipher);
        if (content is null) await js.InvokeVoidAsync("ListHeroDraftStorage.remove", key);
        return content;
    }
    private async Task<string> AnonymousIdentity() => anonymousIdentity ??= "anonymous:" + await js.InvokeAsync<string>("ListHeroDraftStorage.anonymous");
    private async Task<string> AnonymousOwner() => Hash(await AnonymousIdentity());

    public async ValueTask<string?> ReadAsync(string context, bool allowAnonymous = false)
    {
        await gate.WaitAsync();
        try
        {
            var (owner, authenticated) = await Identity(allowAnonymous); var key = Context(context);
            return await Read(owner, key) ?? (allowAnonymous && authenticated ? await Read(await AnonymousOwner(), key) : null);
        }
        catch (Exception e) when (e is JSException or InvalidOperationException or CryptographicException) { throw new DraftStorageException(); }
        finally { gate.Release(); }
    }
    public async ValueTask WriteAsync(string context, string content, bool allowAnonymous = false)
    {
        await gate.WaitAsync();
        try
        {
            if (Encoding.UTF8.GetByteCount(content) > MaximumBytes) throw new DraftStorageException();
            var (owner, authenticated) = await Identity(allowAnonymous); var key = Context(context);
            var prefix = Prefix(owner);
            await js.InvokeVoidAsync("ListHeroDraftStorage.prune", prefix, clock.GetUtcNow().ToUnixTimeMilliseconds());
            var expires = clock.GetUtcNow().AddHours(24);
            var cipher = Protector(owner, key).Protect(JsonSerializer.Serialize(new Envelope(1, expires, content)));
            await js.InvokeVoidAsync("ListHeroDraftStorage.set", prefix + key, cipher, prefix, expires.ToUnixTimeMilliseconds());
            if (allowAnonymous && authenticated) await js.InvokeVoidAsync("ListHeroDraftStorage.remove", Prefix(await AnonymousOwner()) + key);
        }
        catch (Exception e) when (e is JSException or InvalidOperationException or CryptographicException) { throw new DraftStorageException(); }
        finally { gate.Release(); }
    }
    public async ValueTask RemoveAsync(string context, bool allowAnonymous = false)
    {
        await gate.WaitAsync();
        try
        {
            var (owner, authenticated) = await Identity(allowAnonymous); var key = Context(context);
            await js.InvokeVoidAsync("ListHeroDraftStorage.remove", Prefix(owner) + key);
            if (allowAnonymous && authenticated) await js.InvokeVoidAsync("ListHeroDraftStorage.remove", Prefix(await AnonymousOwner()) + key);
        }
        catch (Exception e) when (e is JSException or InvalidOperationException or CryptographicException) { throw new DraftStorageException(); }
        finally { gate.Release(); }
    }
}
