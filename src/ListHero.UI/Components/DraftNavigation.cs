using Microsoft.JSInterop;

namespace ListHero.UI.Components;

internal static class DraftNavigation
{
    public static async Task AcceptAsync(IJSRuntime js, string? formId = null)
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/ListHero.UI/draft-navigation.js");
            await module.InvokeVoidAsync("accept", formId);
        }
        catch (JSException) { /* The write succeeded; a guard failure must not report it as unsaved. */ }
    }

    public static async Task<bool> ConfirmAsync(IJSRuntime js, string formId)
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/ListHero.UI/draft-navigation.js");
            return await module.InvokeAsync<bool>("confirmDiscard", "Switch items and discard unsaved changes to this item?", formId);
        }
        catch (JSException) { return false; }
    }
}
