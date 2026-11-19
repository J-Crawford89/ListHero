using ListHero.Infrastructure.Products;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;

namespace ListHero.Tests.Web;

public sealed partial class WebHostTests
{
    [BrowserFact]
    [Trait("Category", "Browser")]
    public async Task Real_import_browser_rejects_private_navigation_and_uses_only_the_validated_tunnel()
    {
        var network = new RecordingProductNetwork();
        var fetcher = new ProductPageFetcher(network, new ConfigurationBuilder().Build());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAnyAsync<PlaywrightException>(() => fetcher.BrowserAsync(new("https://127.0.0.1/"), deadline.Token));
        Assert.Empty(network.Connections);
        await Assert.ThrowsAnyAsync<PlaywrightException>(() => fetcher.BrowserAsync(new("https://shop.example/item"), deadline.Token));
        Assert.NotEmpty(network.Connections);
        Assert.All(network.Connections, host => Assert.Equal("shop.example", host));
    }
    private sealed class RecordingProductNetwork : IPublicProductNetwork
    {
        public List<string> Connections { get; } = [];
        public Uri Validate(string url) => new PublicProductNetwork().Validate(url);
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct)
        {
            lock (Connections) Connections.Add(host);
            Assert.Equal(443, port);
            throw new HttpRequestException("Synthetic unavailable retailer; this test never uses the Internet.");
        }
    }
}
