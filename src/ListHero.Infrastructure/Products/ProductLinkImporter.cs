using ListHero.Application.Products;
using ListHero.Contracts.Products;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace ListHero.Infrastructure.Products;

public sealed class ProductLinkImporter(IPublicProductNetwork network, IProductPageFetcher fetcher, ProductHtmlParser parser,
    TimeProvider clock, ILogger<ProductLinkImporter> log) : IProductLinkImporter, IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 512 });
    private readonly SemaphoreSlim gate = new(1);
    private DateTimeOffset browserWindow;
    private int browserUses;
    public async Task<ProductLinkResult> ImportAsync(Guid userId, string url, CancellationToken ct)
    {
        var uri = network.Validate(url);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        // An import is nonessential: reject excess/concurrent work rather than grow a queue on F1.
        if (!await gate.WaitAsync(0, ct)) return new("busy");
        try
        {
            var now = clock.GetUtcNow(); var userKey = "user:" + userId;
            var attempts = cache.Get<Window>(userKey);
            if (attempts is not null && now - attempts.Start < TimeSpan.FromMinutes(1) && attempts.Count >= 10) return new("busy");
            var next = attempts is not null && now - attempts.Start < TimeSpan.FromMinutes(1) ? attempts with { Count = attempts.Count + 1 } : new Window(now, 1);
            cache.Set(userKey, next, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1), Size = 1 });
            if (cache.TryGetValue<ProductLinkResult>(key, out var cached)) return cached!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(35));
            var result = new ProductLinkResult("unavailable");
            try
            {
                var page = await fetcher.HttpAsync(uri, timeout.Token);
                if (page is not null) result = parser.Parse(page.Html, page.Url, "http");
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or ArgumentException)
            { LogFetchFailure("http", e); }
            // A useful title alone should not prevent a browser attempt to find the product image.
            if ((result.Name is null || result.ImageUrl is null) && !timeout.IsCancellationRequested)
            {
                if (now - browserWindow >= TimeSpan.FromHours(1)) { browserWindow = now; browserUses = 0; }
                if (browserUses < 60)
                {
                    browserUses++;
                    try
                    {
                        var page = await fetcher.BrowserAsync(uri, timeout.Token);
                        if (page is not null)
                        {
                            var other = parser.Parse(page.Html, page.Url, "browser");
                            if (other.HasDetails) result = new("found", result.Name ?? other.Name, result.Description ?? other.Description,
                                result.ImageUrl ?? other.ImageUrl, result.Price ?? other.Price, "browser");
                        }
                    }
                    catch (Exception e) when (e is Microsoft.Playwright.PlaywrightException or IOException or HttpRequestException or OperationCanceledException or ArgumentException)
                    { LogFetchFailure("browser", e); }
                }
                else if (!result.HasDetails) return new("busy");
            }
            ct.ThrowIfCancellationRequested();
            if (result.HasDetails) result = result with { Status = "found" };
            cache.Set(key, result, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = result.HasDetails ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(2), Size = 1 });
            log.LogInformation(new EventId(4000, "ProductImport"), "Product import {Outcome} {Transport}", result.Status, result.Transport ?? "none");
            return result;
        }
        finally { gate.Release(); }
    }
    public void Dispose() { cache.Dispose(); gate.Dispose(); }
    private void LogFetchFailure(string transport, Exception error)
        => log.LogInformation(new EventId(4001, "ProductFetchFailure"), "Product fetch failed {Transport} {FailureType}", transport, error.GetType().Name);
    private sealed record Window(DateTimeOffset Start, int Count);
}
