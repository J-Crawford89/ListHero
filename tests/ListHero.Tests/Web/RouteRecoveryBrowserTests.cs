using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ListHero.Tests.Web;

public sealed partial class WebHostTests
{
    private static async Task VerifyInvalidPageRecoveryAsync(IPage anonymous, IPage signedIn, string origin)
    {
        foreach (var value in new[] { "abc", "1&page=2" })
        {
            var response = await anonymous.GotoAsync(origin + "/discover?tab=people&q=Browser&page=" + value);
            Assert.Equal(200, response!.Status);
            await Expect(anonymous.GetByRole(AriaRole.Alert)).ToContainTextAsync("Choose a page from 1–100");
            await anonymous.GetByRole(AriaRole.Link, new() { Name = "Go to page 1", Exact = true }).ClickAsync();
            await Expect(anonymous.Locator(".person-card")).ToHaveCountAsync(1);
            await Expect(anonymous.Locator("#discovery-query")).ToHaveValueAsync("Browser");
            await Expect(anonymous.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);

            response = await anonymous.GotoAsync(origin + "/people/browser-jane?page=" + value);
            Assert.Equal(200, response!.Status);
            await Expect(anonymous.GetByRole(AriaRole.Alert)).ToContainTextAsync("Choose a page");
            await anonymous.GetByRole(AriaRole.Link, new() { Name = "Go to page 1", Exact = true }).ClickAsync();
            await Expect(anonymous.Locator("h1")).ToHaveTextAsync("Browser Jane");
            await Expect(anonymous.Locator(".public-list-card")).ToHaveCountAsync(1);

            response = await signedIn.GotoAsync(origin + "/saved?page=" + value);
            Assert.Equal(200, response!.Status);
            await Expect(signedIn.GetByRole(AriaRole.Alert)).ToContainTextAsync("Choose a page from 1–1000");
            await signedIn.GetByRole(AriaRole.Link, new() { Name = "Go to page 1", Exact = true }).ClickAsync();
            await Expect(signedIn.GetByRole(AriaRole.Navigation, new() { Name = "Saved list pages" })).ToContainTextAsync("Page 1");
            await Expect(signedIn.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
        }
        // Return the owner to the list before continuing the core journey.
        await signedIn.GetByRole(AriaRole.Link, new() { Name = "Dashboard", Exact = false }).First.ClickAsync();
        await Expect(signedIn.Locator("h1")).ToHaveTextAsync("Your dashboard");
        await signedIn.Locator(".list-cards h3 a").First.ClickAsync();
        await Expect(signedIn.Locator(".wish-card h2")).ToHaveTextAsync("Blue mug");
    }
}
