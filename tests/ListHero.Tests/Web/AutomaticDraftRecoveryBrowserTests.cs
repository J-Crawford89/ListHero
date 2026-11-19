using ListHero.Domain.Entities;
using ListHero.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ListHero.Tests.Web;

public sealed partial class WebHostTests
{
    private sealed class DraftResponseDrop { public bool NextMark; }
    private sealed class DraftDropHandler(HttpMessageHandler inner, DraftResponseDrop state) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (state.NextMark && request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/marks") && response.IsSuccessStatusCode)
            { state.NextMark = false; response.Dispose(); throw new HttpRequestException("Synthetic lost response after a committed mark."); }
            return response;
        }
    }
    private static async Task FlushRecovery(IPage page)
    {
        await page.EvaluateAsync("import('/_content/ListHero.UI/draft-recovery.js').then(module => module.flushAll())");
        await Expect(page.Locator(".draft-status").Filter(new() { HasText = "Recovery draft saved" }).First).ToBeVisibleAsync();
    }
    private static ILocator Recovery(IPage page, string label) => page.GetByRole(AriaRole.Region, new() { Name = "Recover " + label, Exact = true });
    private static async Task RestoreRecovery(IPage page, string label)
    {
        await Expect(Recovery(page, label)).ToBeVisibleAsync();
        await Recovery(page, label).GetByRole(AriaRole.Button, new() { Name = "Restore draft", Exact = true }).ClickAsync();
    }
    private static async Task VerifyAutomaticDraftRecoveryAsync(IBrowser browser, string origin, IServiceProvider services, DraftResponseDrop drop)
    {
        await using var owner = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        await owner.APIRequest.GetAsync(origin + "/test/session");
        var page = await owner.NewPageAsync(); var dialogs = new DialogChoice(page) { Accept = true };
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(origin + "/lists");
        await Expect(page.GetByText("Your first wish starts here", new() { Exact = false })).ToBeVisibleAsync();
        await page.Locator("#list-name").FillAsync("Recovered private list");
        await page.Locator("#list-description").FillAsync("Unblurred recovery description");
        await FlushRecovery(page);
        var encrypted = await page.EvaluateAsync<string>("JSON.stringify({...sessionStorage})");
        Assert.DoesNotContain("Recovered private list", encrypted); Assert.DoesNotContain("Unblurred recovery description", encrypted);
        await page.ReloadAsync(); await RestoreRecovery(page, "new list");
        await Expect(page.Locator("#list-name")).ToHaveValueAsync("Recovered private list");
        await Expect(page.Locator("#list-description")).ToHaveValueAsync("Unblurred recovery description");
        Assert.True(await HasDraftsAsync(page));
        using (var scope = services.CreateScope()) Assert.False(await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().WishLists.AnyAsync(list => list.Name == "Recovered private list"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Create list", Exact = true }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Recovered private list");
        var address = page.Url; var listId = Guid.Parse(new Uri(address).Segments.Last());

        var description = new string('秘', 4000);
        await page.Locator("#item-name").FillAsync("Recovered gift");
        await page.Locator("#item-quantity").FillAsync("2");
        await page.Locator("#item-price").FillAsync("19.95");
        await page.Locator("#item-link").FillAsync("https://example.test/private-gift");
        await page.Locator("#item-description").FillAsync(description);
        await FlushRecovery(page); await page.ReloadAsync(); await RestoreRecovery(page, "new item");
        await Expect(page.Locator("#item-description")).ToHaveValueAsync(description);
        await Expect(page.Locator("#item-quantity")).ToHaveValueAsync("2");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add item", Exact = true }).ClickAsync();
        await Expect(page.Locator(".wish-card h2")).ToHaveTextAsync("Recovered gift");
        Guid itemId;
        using (var scope = services.CreateScope())
        {
            var item = await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().WishListItems.SingleAsync(item => item.WishListId == listId);
            itemId = item.Id; Assert.Equal(description, item.Description); Assert.Equal(2, item.DesiredQuantity); Assert.Equal(19.95, item.ApproximateUnitPrice);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Edit list", Exact = true }).ClickAsync();
        await page.Locator("#edit-list-name").FillAsync("Recovered edited list"); await FlushRecovery(page);
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ListHeroDbContext>(); var list = await db.WishLists.SingleAsync(list => list.Id == listId);
            list.Update("Concurrent saved title", list.Description, false); await db.SaveChangesAsync();
        }
        await page.ReloadAsync(); await RestoreRecovery(page, "list details");
        await Expect(page.Locator("#edit-list-name")).ToHaveValueAsync("Recovered edited list");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save list", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("record changed");
        using (var scope = services.CreateScope()) Assert.Equal("Concurrent saved title", (await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().WishLists.SingleAsync(list => list.Id == listId)).Name);
        await page.GetByRole(AriaRole.Button, new() { Name = "Use refreshed version and keep my draft", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save list", Exact = true }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Recovered edited list");

        await page.GetByRole(AriaRole.Button, new() { Name = "Edit item", Exact = true }).ClickAsync();
        await page.Locator("#edit-item-name").FillAsync("Recovered edited item"); await FlushRecovery(page);
        await page.ReloadAsync(); await RestoreRecovery(page, "item changes");
        await Expect(page.Locator("#edit-item-name")).ToHaveValueAsync("Recovered edited item");
        await page.Locator("#edit-item-form").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await page.ReloadAsync(); await Expect(Recovery(page, "item changes")).ToHaveCountAsync(0);
        await Expect(page.Locator(".wish-card h2")).ToHaveTextAsync("Recovered gift");

        await page.GotoAsync(origin + "/profile"); await Expect(page.Locator("#public-name")).ToBeVisibleAsync();
        await page.Locator("#public-name").FillAsync("Recovered public name");
        await page.Locator("#public-handle").FillAsync("recovery-" + Guid.NewGuid().ToString("N")[..8]);
        await page.Locator("#profile-discovery-consent").CheckAsync(); await FlushRecovery(page);
        await page.ReloadAsync(); await RestoreRecovery(page, "public profile");
        await Expect(page.Locator("#public-name")).ToHaveValueAsync("Recovered public name");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save public profile", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("saved");

        await page.GotoAsync(address); await Expect(page.Locator("#share-expires")).ToBeVisibleAsync();
        await page.Locator("#share-expires").CheckAsync(); await Expect(page.Locator("#share-expiry")).ToBeVisibleAsync(); await FlushRecovery(page);
        await page.ReloadAsync(); await RestoreRecovery(page, "share options");
        await Expect(page.Locator("#share-expires")).ToBeCheckedAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Create share link", Exact = true }).ClickAsync();
        await Expect(page.Locator(".share-entry input")).ToHaveCountAsync(1);
        var share = await page.Locator(".share-entry input").InputValueAsync();

        // A different account in this tab must not receive the original account's draft.
        await page.GotoAsync(origin + "/lists"); await Expect(page.Locator("[data-recovery-form='create-list-form']")).ToHaveAttributeAsync("data-recovery-ready", "true");
        await page.Locator("#list-name").FillAsync("Original account only"); await FlushRecovery(page);
        var cookies = await owner.CookiesAsync();
        await owner.APIRequest.GetAsync(origin + "/test/session"); await page.ReloadAsync();
        await Expect(page.GetByText("Your first wish starts here", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(Recovery(page, "new list")).ToHaveCountAsync(0); await Expect(page.Locator("#list-name")).ToHaveValueAsync("");
        await owner.AddCookiesAsync(cookies.Select(cookie => new Cookie { Name = cookie.Name, Value = cookie.Value, Domain = cookie.Domain, Path = cookie.Path, Expires = cookie.Expires, Secure = cookie.Secure, HttpOnly = cookie.HttpOnly, SameSite = cookie.SameSite }));
        await page.ReloadAsync(); await Expect(Recovery(page, "new list")).ToBeVisibleAsync();
        await Recovery(page, "new list").GetByRole(AriaRole.Button, new() { Name = "Discard draft" }).ClickAsync();
        await page.ReloadAsync(); await Expect(Recovery(page, "new list")).ToHaveCountAsync(0);

        await using var guest = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true }); var gift = await guest.NewPageAsync();
        var guestDialogs = new DialogChoice(gift) { Accept = true }; gift.PageError += (_, error) => errors.Add(error);
        await gift.GotoAsync(share); await Expect(gift.Locator(".mark-form")).ToHaveCountAsync(1);
        await gift.Locator(".mark-form input[type=number]").FillAsync("3"); await FlushRecovery(gift);
        await gift.ReloadAsync(); await RestoreRecovery(gift, "purchase quantity"); await Expect(gift.Locator(".mark-form input[type=number]")).ToHaveValueAsync("3");
        drop.NextMark = true;
        await gift.GetByRole(AriaRole.Button, new() { Name = "Mark as purchased", Exact = true }).ClickAsync();
        await Expect(gift.GetByRole(AriaRole.Alert)).ToContainTextAsync("couldn't reach");
        await gift.ReloadAsync(); await RestoreRecovery(gift, "purchase quantity");
        await Expect(gift.Locator(".mark-form input[type=number]")).ToBeDisabledAsync();
        await gift.GetByRole(AriaRole.Button, new() { Name = "Retry this mark", Exact = true }).ClickAsync();
        await Expect(gift.Locator(".own-mark")).ToHaveCountAsync(1);
        using (var scope = services.CreateScope())
        {
            var marks = await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().ItemPurchases.Where(mark => mark.WishListItemId == itemId).ToArrayAsync();
            Assert.Equal(3, Assert.Single(marks).Quantity);
        }
        await gift.Locator(".mark-form input[type=number]").FillAsync("5"); await FlushRecovery(gift);
        await gift.GetByRole(AriaRole.Link, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Expect(gift.Locator("h1")).ToHaveTextAsync("Welcome back, hero");
        await guest.APIRequest.GetAsync(origin + "/test/session"); await gift.GotoAsync(origin + "/account/resume");
        await RestoreRecovery(gift, "purchase quantity"); await Expect(gift.Locator(".mark-form input[type=number]")).ToHaveValueAsync("5");
        using (var scope = services.CreateScope()) Assert.Single(await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().ItemPurchases.Where(mark => mark.WishListItemId == itemId).ToArrayAsync());
        Assert.DoesNotContain("Recovered", await gift.EvaluateAsync<string>("JSON.stringify({...sessionStorage})"));
        // A retained edit must never be applied to a different item after its target is archived.
        await page.GotoAsync(address); await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Edit item", Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit item", Exact = true }).ClickAsync();
        await page.Locator("#edit-item-name").FillAsync("Unavailable target draft"); await FlushRecovery(page);
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ListHeroDbContext>();
            var item = await db.WishListItems.SingleAsync(item => item.Id == itemId); item.Archive(DateTimeOffset.UtcNow); await db.SaveChangesAsync();
        }
        await page.ReloadAsync(); await RestoreRecovery(page, "item changes");
        await Expect(page.Locator("#edit-item-form")).ToHaveCountAsync(0);
        await Expect(page.Locator(".draft-status").Filter(new() { HasText = "item is unavailable" })).ToBeVisibleAsync();
        using (var scope = services.CreateScope()) Assert.Equal("Recovered gift", (await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().WishListItems.SingleAsync(item => item.Id == itemId)).Name);
        Assert.Empty(errors);
    }
}
