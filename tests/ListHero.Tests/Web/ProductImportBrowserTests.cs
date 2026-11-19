using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ListHero.Infrastructure.Persistence;
using ListHero.Infrastructure.Products;
using ListHero.Tests.Api;
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
    public async Task Imported_API_details_preserve_unblurred_typing_survive_draft_recovery_and_save_to_SQL()
    {
        var pages = new ControlledProductPages();
        await using var api = OwnerWorkflowTests.CreateBrowserApiFactory(services => services.AddSingleton<IProductPageFetcher>(pages));
        using (var scope = api.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.MigrateAsync();
        await using var web = new WebFactory(apiHandler: api.Server.CreateHandler);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        web.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate))); web.StartServer();
        using var client = web.CreateClient(); var origin = client.BaseAddress!.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var owner = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
        await owner.APIRequest.GetAsync(origin + "/test/session");
        var page = await owner.NewPageAsync(); var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(origin + "/lists");
        await Expect(page.GetByText("Your first wish starts here", new() { Exact = false })).ToBeVisibleAsync();
        await page.Locator("#list-name").FillAsync("Link import test");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create list", Exact = true }).ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Link import test");
        var listId = Guid.Parse(new Uri(page.Url).Segments.Last());
        await page.Locator("#item-link").FillAsync("https://shop.example/item?color=blue"); await page.Locator("#item-link").PressAsync("Tab");
        await pages.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await page.Locator("#item-name").FillAsync("My unblurred title");
        pages.Release.SetResult();
        await Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Added the available details" })).ToBeVisibleAsync();
        await Expect(page.Locator("#item-name")).ToHaveValueAsync("My unblurred title");
        await Expect(page.Locator("#item-description")).ToHaveValueAsync("Imported description");
        await Expect(page.Locator("#item-price")).ToHaveValueAsync("24.5");
        await Expect(page.Locator("#item-image")).ToHaveValueAsync("https://shop.example/mug.jpg");
        await FlushRecovery(page); await page.ReloadAsync(); await RestoreRecovery(page, "new item");
        await Expect(page.Locator("#item-name")).ToHaveValueAsync("My unblurred title");
        await Expect(page.Locator("#item-image")).ToHaveValueAsync("https://shop.example/mug.jpg");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add item", Exact = true }).ClickAsync();
        await Expect(page.Locator(".wish-card h2")).ToHaveTextAsync("My unblurred title");
        using (var scope = api.Services.CreateScope())
        {
            var item = await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().WishListItems.SingleAsync(item => item.WishListId == listId);
            Assert.Equal(24.5, item.ApproximateUnitPrice); Assert.Equal("https://shop.example/mug.jpg", item.ImageUrl);
            Assert.Equal("https://shop.example/item?color=blue", item.Url); Assert.Equal("Imported description", item.Description);
        }
        Assert.Empty(errors);
    }
    private sealed class ControlledProductPages : IProductPageFetcher
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProductPage?> HttpAsync(Uri uri, CancellationToken ct)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(ct);
            return new("""<script type="application/ld+json">{"@type":"Product","name":"Imported title","description":"Imported description","image":"/mug.jpg","offers":{"priceCurrency":"USD","price":24.5}}</script>""", uri);
        }
        public Task<ProductPage?> BrowserAsync(Uri uri, CancellationToken ct) => throw new InvalidOperationException("Complete HTTP metadata should not launch Chromium.");
    }
}
