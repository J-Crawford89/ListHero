using ListHero.Domain.Entities;
using ListHero.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ListHero.Tests.Web;

public sealed partial class WebHostTests
{
    private static async Task VerifyMyListsPaginationAsync(IBrowser browser, string origin, IServiceProvider services)
    {
        await using var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        await context.APIRequest.GetAsync(origin + "/test/session");
        var page = await context.NewPageAsync();
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(origin + "/lists");
        await Expect(page.GetByText("Your first wish starts here", new() { Exact = false })).ToBeVisibleAsync();
        await page.Locator("#list-name").FillAsync("Pagination seed");
        await page.Locator("#list-name").PressAsync("Tab");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create list", Exact = true }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Pagination seed");
        var id = Guid.Parse(new Uri(page.Url).Segments.Last());
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ListHeroDbContext>();
            var seed = await db.WishLists.SingleAsync(list => list.Id == id);
            db.AddRange(Enumerable.Range(1, 56).Select(index => new WishList(seed.OwnerId, $"Paged wish {index}", "Browser pagination fixture", seed.CreatedAt.AddMinutes(index))));
            await db.SaveChangesAsync();
        }
        var cards = page.Locator(".owned-list-browser .list-card");
        var pages = page.GetByRole(AriaRole.Navigation, new() { Name = "My list pages" });
        await page.GotoAsync(origin + "/lists");
        await Expect(cards).ToHaveCountAsync(15);
        await Expect(page.Locator("#owned-page-size")).ToHaveValueAsync("15");
        await pages.GetByRole(AriaRole.Link, new() { Name = "Next →" }).ClickAsync();
        await Expect(page).ToHaveURLAsync(origin + "/lists?page=2&pageSize=15");
        await Expect(pages).ToContainTextAsync("Page 2"); await Expect(cards).ToHaveCountAsync(15);
        await page.GoBackAsync(); await Expect(pages).ToContainTextAsync("Page 1");
        await page.GoForwardAsync(); await Expect(pages).ToContainTextAsync("Page 2");
        await page.Locator("#owned-page-size").SelectOptionAsync("30");
        await Expect(page).ToHaveURLAsync(origin + "/lists?page=1&pageSize=30");
        await Expect(cards).ToHaveCountAsync(30);
        await page.ReloadAsync(); await Expect(cards).ToHaveCountAsync(30);
        await Expect(page.Locator("#owned-page-size")).ToHaveValueAsync("30");
        await pages.GetByRole(AriaRole.Link, new() { Name = "Next →" }).ClickAsync();
        await Expect(cards).ToHaveCountAsync(27);
        await Expect(pages.GetByRole(AriaRole.Link, new() { Name = "Next →" })).ToHaveCountAsync(0);
        await pages.GetByRole(AriaRole.Link, new() { Name = "← Previous" }).ClickAsync();
        await Expect(cards).ToHaveCountAsync(30);
        await page.Locator("#owned-page-size").SelectOptionAsync("50"); await Expect(cards).ToHaveCountAsync(50);
        await page.Locator("#owned-page-size").SelectOptionAsync("100"); await Expect(cards).ToHaveCountAsync(57);
        await Expect(pages).ToHaveCountAsync(0);
        await page.GotoAsync(origin + "/lists?page=99&pageSize=30");
        await Expect(page.GetByText("No lists on this page.", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Go to page 1", Exact = true }).ClickAsync();
        await Expect(page).ToHaveURLAsync(origin + "/lists?page=1&pageSize=30"); await Expect(cards).ToHaveCountAsync(30);
        await page.GotoAsync(origin + "/lists?page=1&page=2&pageSize=30");
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Choose a page"); await Expect(cards).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Link, new() { Name = "Go to page 1", Exact = true }).ClickAsync();
        await Expect(cards).ToHaveCountAsync(15);
        Assert.Empty(errors);
    }
}
