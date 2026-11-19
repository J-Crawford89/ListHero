using System.Net;
using System.Net.Http.Json;
using ListHero.Application.Products;
using ListHero.Contracts.Products;
using ListHero.Infrastructure.Products;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.Api;

public sealed partial class OwnerWorkflowTests
{
    [Fact]
    public async Task Product_import_API_requires_an_active_scoped_account_and_validates_input_before_fetching()
    {
        var importer = new RecordingImporter();
        await using var factory = new OwnerApiFactory(overrides: services => services.AddSingleton<IProductLinkImporter>(importer));
        using var anonymous = factory.CreateClient();
        var request = new ProductLinkRequest("https://shop.example/item");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/item-metadata", request)).StatusCode);
        using var owner = SignedInClient(factory);
        owner.DefaultRequestHeaders.Add("X-Test-Scope", "unrelated");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/item-metadata", request)).StatusCode);
        owner.DefaultRequestHeaders.Remove("X-Test-Scope");
        foreach (var url in new[] { "", new string('x', 2049), "https://127.0.0.1/secret" })
        {
            var invalid = await owner.PostAsJsonAsync("/api/item-metadata", new ProductLinkRequest(url));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.True(invalid.Headers.CacheControl!.NoStore);
        }
        Assert.Equal(0, importer.Calls);
        var response = await owner.PostAsJsonAsync("/api/item-metadata", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("Gift", (await response.Content.ReadFromJsonAsync<ProductLinkResult>())!.Name);
        Assert.Equal(factory.Services.GetRequiredService<MemoryUserStore>().Users.Single().Value.Id, importer.User);
        factory.Services.GetRequiredService<MemoryUserStore>().Users.Single().Value.Archive(DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/item-metadata", request)).StatusCode);
        Assert.Equal(1, importer.Calls);
    }
    private sealed class RecordingImporter : IProductLinkImporter
    {
        public int Calls; public Guid User;
        public Task<ProductLinkResult> ImportAsync(Guid userId, string url, CancellationToken ct)
        {
            _ = new PublicProductNetwork().Validate(url); User = userId; Calls++;
            return Task.FromResult(new ProductLinkResult("found", "Gift", ImageUrl: "https://shop.example/gift.jpg"));
        }
    }
}
