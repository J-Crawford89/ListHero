using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ListHero.Tests.Api;
using ListHero.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ListHero.Tests.Web;

public sealed partial class WebHostTests
{
    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task Browser_owner_and_anonymous_guest_complete_the_core_journey_with_real_SQL()
    {
        await using var api = OwnerWorkflowTests.CreateBrowserApiFactory();
        using (var scope = api.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.MigrateAsync();
        await using var web = new WebFactory(apiHandler: () => api.Server.CreateHandler());
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Import the key for Schannel as well as OpenSSL; neither host needs a trusted machine certificate.
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        web.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        web.StartServer();
        using var hostClient = web.CreateClient();
        var origin = hostClient.BaseAddress!.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var owner = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        await using var guest = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        var page = await owner.NewPageAsync();
        var guestPage = await guest.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        guestPage.PageError += (_, error) => errors.Add(error);
        await guestPage.GotoAsync(origin + "/");
        await Expect(guestPage.Locator(".hero-mascot")).ToBeVisibleAsync();
        await guestPage.Locator(".hero-mascot").EvaluateAsync("image => image.decode()");
        await CaptureResponsiveReviewAsync(guestPage, "home");
        await owner.APIRequest.GetAsync(origin + "/test/session");
        await page.GotoAsync(origin + "/lists");
        // SSR inputs exist before the interactive circuit is ready; wait for its initial API load.
        await Expect(page.GetByText("Your first wish starts here", new() { Exact = false })).ToBeVisibleAsync();
        await CaptureResponsiveReviewAsync(page, "my-lists");
        await page.Locator("#list-name").FillAsync("Browser birthday");
        await page.Locator("#list-description").FillAsync("Created in the browser regression test");
        await page.Locator("#list-description").PressAsync("Tab");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create list", Exact = true }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Browser birthday");
        var listAddress = page.Url;
        await page.Locator("#item-name").FillAsync("Blue mug");
        await page.Locator("#item-quantity").FillAsync("2");
        await page.Locator("#item-quantity").PressAsync("Tab");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add item", Exact = true }).ClickAsync();
        await Expect(page.Locator(".wish-card h2")).ToHaveTextAsync("Blue mug");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create share link", Exact = true }).ClickAsync();
        await Expect(page.Locator(".share-entry input")).ToHaveCountAsync(1);
        var shareAddress = await page.Locator(".share-entry input").InputValueAsync();
        await CaptureResponsiveReviewAsync(page, "owner-list");
        Assert.StartsWith(origin + "/view/", shareAddress);
        Assert.Contains('#', shareAddress);
        await guestPage.GotoAsync(shareAddress);
        await Expect(guestPage.Locator(".mark-form")).ToHaveCountAsync(1);
        await guestPage.Locator(".mark-form input[type=number]").FillAsync("3");
        await guestPage.Locator(".mark-form input[type=number]").PressAsync("Tab");
        await guestPage.GetByRole(AriaRole.Button, new() { Name = "Mark as purchased", Exact = true }).ClickAsync();
        await Expect(guestPage.Locator(".fulfillment")).ToContainTextAsync("Fulfilled");
        await Expect(guestPage.Locator(".fulfillment")).ToContainTextAsync("1 over");
        await CaptureResponsiveReviewAsync(guestPage, "guest-list");
        await guestPage.ReloadAsync();
        await Expect(guestPage.GetByRole(AriaRole.Button, new() { Name = "Undo my mark" })).ToHaveCountAsync(1);
        await page.ReloadAsync();
        await Expect(page.Locator(".wish-card h2")).ToHaveTextAsync("Blue mug");
        await Expect(page.Locator(".fulfillment")).ToHaveCountAsync(0);
        await page.GotoAsync(shareAddress);
        await Expect(page).ToHaveURLAsync(listAddress);
        await Expect(page.Locator(".fulfillment")).ToHaveCountAsync(0);
        await guestPage.GetByRole(AriaRole.Button, new() { Name = "Undo my mark" }).ClickAsync();
        await Expect(guestPage.Locator(".own-mark")).ToHaveCountAsync(0);
        await Expect(guestPage.Locator(".fulfillment")).ToContainTextAsync("0 marked");
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit list", Exact = true }).ClickAsync();
        await CaptureResponsiveReviewAsync(page, "edit-list");
        await page.Locator("#edit-list-name").FillAsync("Updated browser birthday");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save list", Exact = true }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Updated browser birthday");
        await page.Locator(".share-entry button").ClickAsync();
        await Expect(page.Locator(".share-entry")).ToContainTextAsync("Revoked");
        await guestPage.ReloadAsync();
        await Expect(guestPage.GetByRole(AriaRole.Alert)).ToContainTextAsync("unavailable");
        await page.GetByRole(AriaRole.Button, new() { Name = "Remove item", Exact = true }).ClickAsync();
        await page.Locator(".confirmation").GetByRole(AriaRole.Button, new() { Name = "Remove item", Exact = true }).ClickAsync();
        await Expect(page.Locator(".wish-card")).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Button, new() { Name = "Remove list", Exact = true }).ClickAsync();
        await page.Locator(".confirmation").GetByRole(AriaRole.Button, new() { Name = "Remove list", Exact = true }).ClickAsync();
        await Expect(page).ToHaveURLAsync(origin + "/lists");
        Assert.Empty(errors);
    }
    private static async Task CaptureResponsiveReviewAsync(IPage page, string name)
    {
        var reviewDirectory = Environment.GetEnvironmentVariable("LISTHERO_UI_REVIEW_DIR");
        foreach (var width in new[] { 1440, 390 })
        {
            await page.SetViewportSizeAsync(width, 900);
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"), $"{name} overflows at {width}px");
            if (!string.IsNullOrWhiteSpace(reviewDirectory))
            {
                Directory.CreateDirectory(reviewDirectory);
                await page.ScreenshotAsync(new() { Path = Path.Combine(reviewDirectory, $"{name}-{width}.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
            }
        }
        await page.SetViewportSizeAsync(1280, 900);
    }
    private sealed class BrowserFactAttribute : FactAttribute
    {
        public BrowserFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("LISTHERO_BROWSER_TESTS") != "1" || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LISTHERO_TEST_SQL_CONNECTION")))
                Skip = "Enable LISTHERO_BROWSER_TESTS=1 with isolated SQL and install Playwright Chromium to run the browser regression.";
        }
    }
}
