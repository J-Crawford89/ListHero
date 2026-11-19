using System.Text;
using ListHero.Infrastructure.Products;
using Microsoft.Extensions.Configuration;

namespace ListHero.Tests.Persistence;

public sealed class ProductPageFetcherTests
{
    [Fact]
    public async Task HTTP_fetch_follows_validated_relative_redirects_and_reads_HTML()
    {
        var network = new Responses("HTTP/1.1 302 Found\r\nLocation: /variant?color=blue\r\nContent-Length: 0\r\n\r\n", Html("<title>Blue mug</title>"));
        var page = await Fetcher(network).HttpAsync(new("https://shop.example/item"), default);
        Assert.NotNull(page); Assert.Contains("Blue mug", page.Html); Assert.Equal("/variant?color=blue", page.Url.PathAndQuery);
        Assert.Equal(2, network.Connections); Assert.All(network.Validated, url => Assert.StartsWith("https://", url));
    }

    [Fact]
    public async Task Redirect_to_private_destination_is_rejected_before_another_connection()
    {
        var network = new Responses("HTTP/1.1 302 Found\r\nLocation: https://127.0.0.1/secret\r\nContent-Length: 0\r\n\r\n");
        await Assert.ThrowsAsync<ArgumentException>(() => Fetcher(network).HttpAsync(new("https://shop.example/item"), default));
        Assert.Equal(1, network.Connections);
    }

    [Theory]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Type: text/html\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Type: application/pdf\r\nContent-Length: 0\r\n\r\n")]
    public async Task HTTP_failure_or_non_HTML_has_no_page(string response)
        => Assert.Null(await Fetcher(new Responses(response)).HttpAsync(new("https://shop.example/item"), default));

    [Fact]
    public async Task HTTP_fetch_bounds_redirects_and_body_size()
    {
        var redirects = new Responses(Enumerable.Repeat("HTTP/1.1 302 Found\r\nLocation: https://shop.example/loop\r\nContent-Length: 0\r\n\r\n", 5).ToArray());
        Assert.Null(await Fetcher(redirects).HttpAsync(new("https://shop.example/item"), default)); Assert.Equal(5, redirects.Connections);
        var oversized = new Responses(Html(new string('a', 4 * 1024 * 1024 + 1)));
        Assert.Null(await Fetcher(oversized).HttpAsync(new("https://shop.example/item"), default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fetcher(new Responses()).HttpAsync(new("https://shop.example/item"), cancelled.Token));
    }

    [Fact]
    public async Task Hosted_browser_never_attempts_a_runtime_download_when_asset_is_missing()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["WEBSITE_INSTANCE_ID"] = "test-instance", ["ProductImport:BrowserPath"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe") }).Build();
        var network = new Responses();
        Assert.Null(await new ProductPageFetcher(network, config).BrowserAsync(new("https://shop.example/item"), default));
        Assert.Equal(0, network.Connections);
    }

    private static ProductPageFetcher Fetcher(Responses responses) => new(responses, new ConfigurationBuilder().Build());
    private static string Html(string body) => $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";

    // A scripted wire transport. Validation still uses the production policy; only the returned
    // URI is changed to HTTP so these deterministic fixtures do not need a test trust-store change.
    private sealed class Responses(params string[] responses) : IPublicProductNetwork
    {
        public int Connections;
        public List<string> Validated { get; } = [];
        public Uri Validate(string url)
        {
            // Relative redirects retain the fixture's synthetic HTTP transport scheme.
            if (url.StartsWith("http://shop.example/", StringComparison.Ordinal)) url = "https://" + url[7..];
            Validated.Add(url); var safe = new PublicProductNetwork().Validate(url);
            return new UriBuilder(safe) { Scheme = "http", Port = 80 }.Uri;
        }
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Assert.Equal("shop.example", host);
            return ValueTask.FromResult<Stream>(new WireStream(Encoding.UTF8.GetBytes(responses[Connections++])));
        }
    }
    private sealed class WireStream(byte[] response) : Stream
    {
        private readonly MemoryStream input = new(response);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => input.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.CompletedTask;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
}
