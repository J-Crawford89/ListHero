using System.Net;
using Microsoft.Playwright;
using Microsoft.Extensions.Configuration;

namespace ListHero.Infrastructure.Products;

public sealed record ProductPage(string Html, Uri Url);
public interface IProductPageFetcher
{
    Task<ProductPage?> HttpAsync(Uri uri, CancellationToken ct);
    Task<ProductPage?> BrowserAsync(Uri uri, CancellationToken ct);
}
public sealed class ProductPageFetcher(IPublicProductNetwork network, IConfiguration configuration) : IProductPageFetcher
{
    public async Task<ProductPage?> HttpAsync(Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = (context, cancel) => network.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancel)
        };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        for (var hop = 0; hop < 5; hop++)
        {
            uri = network.Validate(uri.AbsoluteUri);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next) { uri = new Uri(uri, next); continue; }
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not ("text/html" or "application/xhtml+xml")) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token); using var output = new MemoryStream();
            var buffer = new byte[16384]; int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0) { if (output.Length + count > 4 * 1024 * 1024) return null; output.Write(buffer, 0, count); }
            return new(System.Text.Encoding.UTF8.GetString(output.ToArray()), uri);
        }
        return null;
    }
    public async Task<ProductPage?> BrowserAsync(Uri uri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Asset path is app-relative; no downloads, installers or paid endpoints at runtime.
        var executable = configuration["ProductImport:BrowserPath"];
        if (string.IsNullOrEmpty(executable)) executable = Path.Combine(AppContext.BaseDirectory, "product-browser", "chrome-headless-shell.exe");
        if (!File.Exists(executable) && !string.IsNullOrEmpty(configuration["WEBSITE_INSTANCE_ID"])) return null;
        await using var tunnel = new ProductTunnel(network, ct);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true, ExecutablePath = File.Exists(executable) ? executable : null, Timeout = 12000,
            Proxy = new() { Server = tunnel.Address }, Args = ["--disable-quic", "--proxy-bypass-list=<-loopback>"]
        });
        await using var context = await browser.NewContextAsync(new()
        {
            JavaScriptEnabled = false, ServiceWorkers = ServiceWorkerPolicy.Block, AcceptDownloads = false, Locale = "en-US"
        });
        var page = await context.NewPageAsync(); var hops = 0;
        await context.RouteAsync("**/*", async route =>
        {
            try
            {
                _ = network.Validate(route.Request.Url);
                if (route.Request.ResourceType != "document" || !route.Request.IsNavigationRequest || route.Request.Frame != page.MainFrame || ++hops > 5) { await route.AbortAsync(); return; }
                await route.ContinueAsync();
            }
            catch (ArgumentException) { await route.AbortAsync(); }
        });
        using var cancel = ct.Register(() => { _ = CloseCancelledBrowserAsync(browser); });
        var response = await page.GotoAsync(uri.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 16000 });
        if (response is null || !response.Ok) return null;
        // The tunnel bounds downloaded TLS traffic; also bound the DOM serialized back to .NET.
        if (await page.EvaluateAsync<int>("document.documentElement.outerHTML.length") > 4 * 1024 * 1024) return null;
        return new(await page.ContentAsync(), network.Validate(page.Url));
    }
    private static async Task CloseCancelledBrowserAsync(IBrowser browser)
    {
        try { await browser.CloseAsync(); }
        catch (PlaywrightException) { } // Disposal may have closed the driver concurrently.
    }
}
