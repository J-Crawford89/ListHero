using System.Text.Json;
using ListHero.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ListHero.Tests.Web;

public sealed partial class WebHostTests
{
    private static async Task VerifyDraftNavigationAsync(IBrowser browser, string origin)
    {
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        await context.APIRequest.GetAsync(origin + "/test/session");
        var page = await context.NewPageAsync();
        var dialogs = new DialogChoice(page);
        await page.GotoAsync(origin + "/lists");
        await Expect(page.GetByText("Your first wish starts here", new() { Exact = false })).ToBeVisibleAsync();
        await page.Locator("#list-name").FillAsync("Keep this draft");
        await page.Locator("header").GetByRole(AriaRole.Link, new() { Name = "Dashboard", Exact = true }).ClickAsync();
        await dialogs.WaitForCountAsync(1);
        await Expect(page).ToHaveURLAsync(origin + "/lists");
        await Expect(page.Locator("#list-name")).ToHaveValueAsync("Keep this draft");

        await page.EvaluateAsync("setTimeout(() => location.reload(), 0)");
        await dialogs.WaitForCountAsync(2);
        Assert.Equal("beforeunload", dialogs.Types[^1]);
        await Expect(page.Locator("#list-name")).ToHaveValueAsync("Keep this draft");
        await page.CloseAsync(new() { RunBeforeUnload = true });
        await dialogs.WaitForCountAsync(3);
        Assert.False(page.IsClosed);

        dialogs.Accept = true;
        await page.Locator("header").GetByRole(AriaRole.Link, new() { Name = "Dashboard", Exact = true }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Your dashboard");
        await page.Locator("header").GetByRole(AriaRole.Link, new() { Name = "My lists", Exact = true }).ClickAsync();
        await Expect(page.Locator("#list-name")).ToHaveValueAsync("");
        dialogs.Accept = false;
        await page.Locator("#list-name").FillAsync("Back button draft");
        await page.EvaluateAsync("history.back()");
        await dialogs.WaitForCountAsync(5);
        await Expect(page).ToHaveURLAsync(origin + "/lists");
        await Expect(page.Locator("#list-name")).ToHaveValueAsync("Back button draft");

        // Reverting the field removes the warning; search filters never become business drafts.
        await page.Locator("#list-name").FillAsync("");
        await page.Locator("header").GetByRole(AriaRole.Link, new() { Name = "Discover", Exact = true }).ClickAsync();
        await Expect(page.Locator("#discovery-query")).ToBeVisibleAsync();
        await page.Locator("#discovery-query").FillAsync("Search text");
        await page.Locator("header").GetByRole(AriaRole.Link, new() { Name = "My lists", Exact = true }).ClickAsync();
        await Expect(page.Locator("#list-name")).ToBeVisibleAsync();
        Assert.Equal(5, dialogs.Types.Count);

        // Successful create navigates directly; item saves and profile saves clear their baselines.
        await page.Locator("#list-name").FillAsync("Navigation guard list");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create list", Exact = true }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Navigation guard list");
        Assert.Equal(5, dialogs.Types.Count);
        await page.Locator("#item-name").FillAsync("Guarded mug");
        await page.Locator("#item-quantity").FillAsync("0");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add item", Exact = true }).ClickAsync();
        Assert.True(await HasDraftsAsync(page));
        await page.Locator("#item-quantity").FillAsync("1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add item", Exact = true }).ClickAsync();
        await Expect(page.Locator(".wish-card h2")).ToHaveTextAsync("Guarded mug");
        Assert.False(await HasDraftsAsync(page));
        await page.Locator("#create-share-link input[type=checkbox]").CheckAsync();
        Assert.True(await HasDraftsAsync(page));
        await page.GetByRole(AriaRole.Button, new() { Name = "Create share link", Exact = true }).ClickAsync();
        await Expect(page.Locator(".share-entry input")).ToHaveCountAsync(1);
        Assert.False(await HasDraftsAsync(page));
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit list", Exact = true }).ClickAsync();
        await page.Locator("#edit-list-name").FillAsync("Saved guard list");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save list", Exact = true }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Saved guard list");
        Assert.False(await HasDraftsAsync(page));
        await page.Locator("header").GetByRole(AriaRole.Link, new() { Name = "Public profile", Exact = true }).ClickAsync();
        await Expect(page.Locator("#public-name")).ToBeVisibleAsync();
        await page.Locator("#public-name").FillAsync("Guard User");
        await page.Locator("#public-handle").FillAsync("guard-user");
        await page.Locator("#public-profile-form input[type=checkbox]").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save public profile", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("saved");
        Assert.False(await HasDraftsAsync(page));
        await page.Locator("#public-name").FillAsync("Unsaved profile name");
        await page.Locator("header").GetByRole(AriaRole.Link, new() { Name = "Dashboard", Exact = true }).ClickAsync();
        await dialogs.WaitForCountAsync(6);
        await Expect(page.Locator("#public-name")).ToHaveValueAsync("Unsaved profile name");
        await page.GetByRole(AriaRole.Button, new() { Name = "Reload saved profile", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Discard changes and reload", Exact = true }).ClickAsync();
        await Expect(page.Locator("#public-name")).ToHaveValueAsync("Guard User");
        Assert.False(await HasDraftsAsync(page));
        await page.Locator("header").GetByRole(AriaRole.Link, new() { Name = "Dashboard", Exact = true }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Your dashboard");
        Assert.Equal(6, dialogs.Types.Count);
        await page.Locator("header").GetByRole(AriaRole.Link, new() { Name = "My lists", Exact = true }).ClickAsync();
        await Expect(page.Locator("#list-name")).ToBeVisibleAsync();
        await page.Locator("#list-name").FillAsync("Sign-out draft");
        await page.Locator("header").GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true }).ClickAsync();
        await dialogs.WaitForCountAsync(7);
        var stillAuthenticated = await context.APIRequest.GetAsync(origin + "/test/authenticated");
        Assert.True(JsonSerializer.Deserialize<bool>(await stillAuthenticated.TextAsync()));
        await Expect(page.Locator("#list-name")).ToHaveValueAsync("Sign-out draft");
    }

    private static async Task VerifyCrossTabSessionsAsync(IBrowser browser, string origin, IServiceProvider apiServices)
    {
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        await context.APIRequest.GetAsync(origin + "/test/session");
        var foreground = await context.NewPageAsync();
        var control = await context.NewPageAsync();
        var dialogs = new DialogChoice(foreground);
        await foreground.GotoAsync(origin + "/lists");
        await Expect(foreground.GetByText("Your first wish starts here", new() { Exact = false })).ToBeVisibleAsync();
        await control.GotoAsync(origin + "/welcome");
        await foreground.Locator("#list-name").FillAsync("Do not submit this draft");
        var originalSession = await foreground.Locator("#session-context").GetAttributeAsync("data-session-id");
        await control.APIRequest.GetAsync(origin + "/test/session");
        await FocusCheckAsync(foreground);
        await dialogs.WaitForCountAsync(1);
        await Expect(foreground.Locator("#session-recovery")).ToBeVisibleAsync();
        await Expect(foreground.Locator("#list-name")).ToHaveValueAsync("Do not submit this draft");
        await foreground.GetByRole(AriaRole.Button, new() { Name = "Create list", Exact = true }).ClickAsync();
        await Expect(foreground.GetByText("Please sign in again to continue.", new() { Exact = true })).ToBeVisibleAsync();
        using (var scope = apiServices.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().WishLists.CountAsync(list => list.Name == "Do not submit this draft"));
        await FocusCheckAsync(foreground);
        await dialogs.WaitForCountAsync(2);
        dialogs.Accept = true;
        await foreground.GetByRole(AriaRole.Button, new() { Name = "Reload to continue", Exact = true }).ClickAsync();
        await Expect(foreground.Locator("#list-name")).ToHaveValueAsync("");
        await Expect(foreground.Locator("#session-recovery")).ToHaveCountAsync(0);
        Assert.NotEqual(originalSession, await foreground.Locator("#session-context").GetAttributeAsync("data-session-id"));
        Assert.Equal(3, dialogs.Types.Count);
        Assert.All(dialogs.Types, type => Assert.Equal("confirm", type));

        // Real cookie sign-out in the other tab; the public page can refresh without an IdP stub.
        await foreground.GotoAsync(origin + "/welcome");
        var tokenResponse = await control.APIRequest.GetAsync(origin + "/test/antiforgery");
        var token = JsonSerializer.Deserialize<string>(await tokenResponse.TextAsync())!;
        var form = control.APIRequest.CreateFormData();
        form.Set("__RequestVerificationToken", token);
        var logout = await control.APIRequest.PostAsync(origin + "/account/sign-out", new()
        {
            Form = form, MaxRedirects = 0
        });
        Assert.Equal(302, logout.Status);
        await FocusCheckAsync(foreground);
        await Expect(foreground.Locator("#session-context")).ToHaveAttributeAsync("data-session", "anonymous");
        await Expect(foreground.Locator("header").GetByRole(AriaRole.Link, new() { Name = "Sign in", Exact = true })).ToBeVisibleAsync();
        await control.APIRequest.GetAsync(origin + "/test/session");
        await FocusCheckAsync(foreground);
        await Expect(foreground.Locator("#session-context")).ToHaveAttributeAsync("data-session", "signed-in");
        await Expect(foreground.Locator("header").GetByRole(AriaRole.Link, new() { Name = "My lists", Exact = true })).ToBeVisibleAsync();

        // A stable cookie/fresh antiforgery context must not cause a reload loop.
        await foreground.EvaluateAsync("window.sessionTestMarker = 'unchanged'");
        await FocusCheckAsync(foreground);
        Assert.Equal("unchanged", await foreground.EvaluateAsync<string>("window.sessionTestMarker"));
        Assert.Equal(3, dialogs.Types.Count);
    }

    private static Task<bool> HasDraftsAsync(IPage page)
        => page.EvaluateAsync<bool>("import('/_content/ListHero.UI/draft-navigation.js').then(module => module.hasChanges())");

    private static async Task FocusCheckAsync(IPage page)
    {
        var response = page.WaitForResponseAsync(result => new Uri(result.Url).AbsolutePath == "/account/session");
        await page.BringToFrontAsync();
        await page.EvaluateAsync("window.dispatchEvent(new Event('focus'))");
        await response;
    }

    private sealed class DialogChoice
    {
        public bool Accept;
        public List<string> Types { get; } = [];
        public DialogChoice(IPage page)
        {
            page.Dialog += async (_, dialog) =>
            {
                Types.Add(dialog.Type);
                if (Accept) await dialog.AcceptAsync(); else await dialog.DismissAsync();
            };
        }
        public async Task WaitForCountAsync(int count)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (Types.Count < count && DateTime.UtcNow < until) await Task.Delay(10);
            Assert.Equal(count, Types.Count);
        }
    }
}
