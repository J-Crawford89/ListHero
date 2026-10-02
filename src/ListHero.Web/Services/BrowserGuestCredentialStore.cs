using ListHero.Client.Abstractions;
using ListHero.Client.Abstractions.Api;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace ListHero.Web.Services;

// Use after interactivity begins. Browser storage is unavailable during prerendering.
public sealed class BrowserGuestCredentialStore(ProtectedLocalStorage storage, IConfiguration configuration,
    IWebHostEnvironment environment) : IGuestCredentialStore
{
    private readonly string storageKey = environment.IsDevelopment()
        && configuration["Development:GuestStorageKey"] is { Length: > 0 } key ? key : "listhero.guest.v1";

    public async ValueTask<string?> ReadAsync()
    {
        try
        {
            var result = await storage.GetAsync<string>(storageKey);
            return result.Success ? result.Value : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            try { await storage.DeleteAsync(storageKey); } catch (JSException) { }
            return null;
        }
        catch (JSException) { return null; }
    }

    public async ValueTask WriteAsync(string credential)
    {
        try { await storage.SetAsync(storageKey, credential); }
        catch (JSException)
        {
            throw new ApiRequestException("Allow browser storage before marking a gift anonymously, or sign in to keep track of your marks.");
        }
    }
    public ValueTask ClearAsync() => storage.DeleteAsync(storageKey);
}
