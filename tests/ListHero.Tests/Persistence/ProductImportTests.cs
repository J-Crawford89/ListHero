using System.Net;
using System.Net.Sockets;
using System.Text;
using ListHero.Contracts.Products;
using ListHero.Infrastructure.Products;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ListHero.Tests.Persistence;

public sealed class ProductImportTests
{
    [Theory]
    [InlineData("0.1.2.3")] [InlineData("10.1.2.3")] [InlineData("127.1.2.3")]
    [InlineData("100.64.0.1")] [InlineData("100.127.255.255")] [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")] [InlineData("172.31.255.255")] [InlineData("192.168.1.1")]
    [InlineData("192.0.0.1")] [InlineData("192.0.2.1")] [InlineData("192.88.99.1")]
    [InlineData("198.18.0.1")] [InlineData("198.19.1.1")] [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")] [InlineData("224.0.0.1")] [InlineData("255.255.255.255")]
    [InlineData("168.63.129.16")] [InlineData("::1")] [InlineData("::")]
    [InlineData("::ffff:127.0.0.1")] [InlineData("fc00::1")] [InlineData("fe80::1")]
    [InlineData("ff02::1")] [InlineData("2001:db8::1")] [InlineData("2001::1")]
    [InlineData("2001:20::1")] [InlineData("2002:7f00:1::1")]
    public void Private_special_and_Azure_platform_addresses_are_never_public(string address)
        => Assert.False(PublicProductNetwork.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")] [InlineData("1.1.1.1")] [InlineData("100.63.255.255")]
    [InlineData("172.15.255.255")] [InlineData("172.32.0.1")] [InlineData("::ffff:8.8.8.8")]
    [InlineData("2606:4700:4700::1111")] [InlineData("2001:4860:4860::8888")]
    public void Global_addresses_are_eligible(string address)
        => Assert.True(PublicProductNetwork.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("")] [InlineData("http://example.com/item")] [InlineData("file:///C:/private")]
    [InlineData("https://user:pass@example.com/")] [InlineData("https://example.com:8443/")]
    [InlineData("https://localhost/")] [InlineData("https://127.0.0.1/")]
    [InlineData("https://2130706433/")] [InlineData("https://[::1]/")]
    [InlineData("https://x.azurewebsites.net/")] [InlineData("https://x.onmicrosoft.com/")]
    [InlineData("https://x.azurewebsites.net./")] [InlineData("https://localhost./")]
    [InlineData("https://host.local/")] [InlineData("https://host.internal/")]
    public void Unsupported_URL_shapes_are_rejected_before_network_access(string url)
        => Assert.Throws<ArgumentException>(() => new PublicProductNetwork().Validate(url));

    [Fact]
    public void URL_normalization_preserves_variant_queries_but_removes_fragment()
    {
        var network = new PublicProductNetwork();
        Assert.Equal("https://example.com/item?color=RED&size=2", network.Validate("https://example.com/item?color=RED&size=2#details").AbsoluteUri);
        Assert.Throws<ArgumentException>(() => network.Validate("https://example.com/" + new string('x', 2048)));
    }

    [Fact]
    public void Structured_product_data_is_sanitized_and_resolved_without_guessing_foreign_currency()
    {
        var parser = new ProductHtmlParser();
        var result = parser.Parse("""
            <title>Store</title><script type="application/ld+json">not-json</script>
            <script type="application/ld+json">{"@graph":[{"@type":["Thing","Product"],"name":" <b>Blue</b> mug ","description":"A &amp; B","image":[{"contentUrl":"/mug.jpg"}],"offers":[{"priceCurrency":"USD","price":"24.50"}]}]}</script>
            """, new("https://shop.example/item"), "http");
        Assert.Equal("Blue mug", result.Name); Assert.Equal("A & B", result.Description);
        Assert.Equal("https://shop.example/mug.jpg", result.ImageUrl); Assert.Equal(24.5, result.Price);
        Assert.Equal("found", result.Status); Assert.True(result.HasDetails); Assert.Equal("http", result.Transport);
        var foreign = parser.Parse("""<script type="application/ld+json">[{"@type":"Product","name":"Boots","offers":{"priceCurrency":"EUR","price":20}}]</script>""", new("https://shop.example/item"), "http");
        Assert.Null(foreign.Price); Assert.Equal("Boots", foreign.Name);
    }

    [Theory]
    [InlineData("Access Denied")] [InlineData("Robot Check")] [InlineData("Just a moment")]
    [InlineData("Amazon.com")] [InlineData("Amazon.com: Spend less. Smile more.")]
    public void Blocked_and_generic_Amazon_pages_do_not_become_product_details(string title)
        => Assert.False(new ProductHtmlParser().Parse($"<title>{title}</title><meta property='og:image' content='/logo.png'>", new("https://www.amazon.com/dp/ABC"), "http").HasDetails);

    [Fact]
    public void Amazon_product_DOM_and_common_social_metadata_supply_real_images()
    {
        var parser = new ProductHtmlParser();
        var amazon = parser.Parse("<span id='productTitle'> The TV </span><img id='landingImage' src='/small.jpg' data-old-hires='https://m.media-amazon.com/images/large.jpg'>", new("https://www.amazon.com/dp/ABC"), "browser");
        Assert.Equal("The TV", amazon.Name); Assert.EndsWith("large.jpg", amazon.ImageUrl);
        var social = parser.Parse("<meta name='twitter:title' content='A gift'><meta property='og:image' content='/gift.jpg'><meta name='description' content='Description'>", new("https://shop.example/item"), "http");
        Assert.Equal("A gift", social.Name); Assert.Equal("https://shop.example/gift.jpg", social.ImageUrl); Assert.Equal("Description", social.Description);
        Assert.Null(parser.Parse("<title>A gift</title><meta property='og:image' content='http://shop.example/image.jpg'>", new("https://shop.example/item"), "http").ImageUrl);
        Assert.False(parser.Parse("<title>Home</title>", new("https://shop.example/"), "http").HasDetails);
        var fallback = parser.Parse("<title>A gift</title><meta property='og:image' content='https://host.local/logo.jpg'><meta name='twitter:image' content='/actual.jpg'>", new("https://shop.example/item"), "http");
        Assert.Equal("https://shop.example/actual.jpg", fallback.ImageUrl);
        Assert.Null(parser.Parse("<title>A gift</title><meta property='og:image' content='https://host.internal/photo.jpg'>", new("https://shop.example/item"), "http").ImageUrl);
    }

    [Fact]
    public async Task Complete_HTTP_metadata_skips_browser_and_is_cached_across_users()
    {
        var fetcher = new FakeFetch { Http = (_, _) => Task.FromResult<ProductPage?>(Complete) };
        using var importer = Importer(fetcher);
        var first = await importer.ImportAsync(Guid.NewGuid(), Url, default);
        var second = await importer.ImportAsync(Guid.NewGuid(), Url + "#section", default);
        Assert.Equal(first, second); Assert.Equal("http", first.Transport);
        Assert.Equal(1, fetcher.HttpCalls); Assert.Equal(0, fetcher.BrowserCalls);
    }

    [Fact]
    public async Task Browser_fills_partial_HTTP_result_and_failure_keeps_useful_partial_details()
    {
        var fetcher = new FakeFetch { Http = (_, _) => Task.FromResult<ProductPage?>(new("<title>Useful title</title>", new(Url))), Browser = (_, _) => Task.FromResult<ProductPage?>(Complete) };
        using var importer = Importer(fetcher);
        var result = await importer.ImportAsync(Guid.NewGuid(), Url, default);
        Assert.Equal("Useful title", result.Name); Assert.NotNull(result.ImageUrl); Assert.Equal("browser", result.Transport);
        fetcher.Browser = (_, _) => throw new HttpRequestException("retailer unavailable");
        var partial = await importer.ImportAsync(Guid.NewGuid(), Url + "?other=1", default);
        Assert.Equal("Useful title", partial.Name); Assert.Null(partial.ImageUrl); Assert.Equal("http", partial.Transport);
    }

    [Fact]
    public async Task Concurrent_import_is_rejected_instead_of_queued_and_cancellation_releases_slot()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetcher = new FakeFetch { Http = async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return null; } };
        using var importer = Importer(fetcher); using var cancel = new CancellationTokenSource();
        var pending = importer.ImportAsync(Guid.NewGuid(), Url, cancel.Token); await entered.Task;
        Assert.Equal("busy", (await importer.ImportAsync(Guid.NewGuid(), Url, default)).Status);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        fetcher.Http = (_, _) => Task.FromResult<ProductPage?>(Complete);
        Assert.True((await importer.ImportAsync(Guid.NewGuid(), Url, default)).HasDetails);
    }

    [Fact]
    public async Task Per_user_limit_and_browser_budget_reset_with_their_windows()
    {
        var clock = new Clock(); var fetcher = new FakeFetch(); using var importer = Importer(fetcher, clock); var user = Guid.NewGuid();
        for (var i = 0; i < 10; i++) await importer.ImportAsync(user, Url, default);
        Assert.Equal("busy", (await importer.ImportAsync(user, Url, default)).Status);
        Assert.Equal(1, fetcher.BrowserCalls); // Negative results are cached too.
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal("unavailable", (await importer.ImportAsync(user, Url, default)).Status);
        for (var i = 1; i < 60; i++) await importer.ImportAsync(Guid.NewGuid(), Url + "?id=" + i, default);
        Assert.Equal(60, fetcher.BrowserCalls);
        Assert.Equal("busy", (await importer.ImportAsync(Guid.NewGuid(), Url + "?id=next", default)).Status);
        clock.Now += TimeSpan.FromHours(1);
        Assert.Equal("unavailable", (await importer.ImportAsync(Guid.NewGuid(), Url + "?id=next", default)).Status);
        Assert.Equal(61, fetcher.BrowserCalls);
    }

    [Theory]
    [InlineData("GET http://example.com/ HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT example.com:80 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT 127.0.0.1:443 HTTP/1.1\r\n\r\n")]
    public async Task Browser_tunnel_rejects_non_TLS_and_private_authorities(string request)
    {
        var network = new EchoNetwork(); await using var tunnel = new ProductTunnel(network, default);
        using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", new Uri(tunnel.Address).Port);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));
        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1])); Assert.Equal(0, network.Connections);
    }

    [Fact]
    public async Task Browser_tunnel_forwards_only_a_validated_connection_and_disposes_it()
    {
        var network = new EchoNetwork(); await using var tunnel = new ProductTunnel(network, default);
        using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", new Uri(tunnel.Address).Port);
        var stream = client.GetStream(); await stream.WriteAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n"u8.ToArray());
        var header = new byte[39]; await stream.ReadExactlyAsync(header);
        Assert.Equal("HTTP/1.1 200 Connection Established\r\n\r\n", Encoding.ASCII.GetString(header));
        await stream.WriteAsync("ping"u8.ToArray()); var echo = new byte[4]; await stream.ReadExactlyAsync(echo);
        Assert.Equal("ping", Encoding.ASCII.GetString(echo)); Assert.Equal(1, network.Connections);
    }

    [Fact]
    public async Task Hosted_browser_missing_assets_fails_without_trying_a_runtime_download()
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WEBSITE_INSTANCE_ID"] = "test-instance", ["ProductImport:BrowserPath"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe") }).Build();
        Assert.Null(await new ProductPageFetcher(new PublicProductNetwork(), settings).BrowserAsync(new(Url), default));
    }

    private const string Url = "https://shop.example/product";
    private static readonly ProductPage Complete = new("<title>Gift</title><meta property='og:image' content='/gift.jpg'>", new(Url));
    private static ProductLinkImporter Importer(FakeFetch fetcher, TimeProvider? clock = null) => new(new PublicProductNetwork(), fetcher, new(), clock ?? TimeProvider.System, NullLogger<ProductLinkImporter>.Instance);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-19T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FakeFetch : IProductPageFetcher
    {
        public int HttpCalls, BrowserCalls;
        public Func<Uri, CancellationToken, Task<ProductPage?>> Http { get; set; } = (_, _) => Task.FromResult<ProductPage?>(null);
        public Func<Uri, CancellationToken, Task<ProductPage?>> Browser { get; set; } = (_, _) => Task.FromResult<ProductPage?>(null);
        public Task<ProductPage?> HttpAsync(Uri uri, CancellationToken ct) { HttpCalls++; return Http(uri, ct); }
        public Task<ProductPage?> BrowserAsync(Uri uri, CancellationToken ct) { BrowserCalls++; return Browser(uri, ct); }
    }
    private sealed class EchoNetwork : IPublicProductNetwork
    {
        public int Connections;
        public Uri Validate(string url) => new PublicProductNetwork().Validate(url);
        public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct)
        {
            Connections++; Assert.Equal("example.com", host); Assert.Equal(443, port);
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
            var server = await listener.AcceptTcpClientAsync(ct); listener.Stop();
            _ = Task.Run(async () => { using (server) { var bytes = new byte[4]; await server.GetStream().ReadExactlyAsync(bytes, ct); await server.GetStream().WriteAsync(bytes, ct); } }, ct);
            return client.GetStream();
        }
    }
}
