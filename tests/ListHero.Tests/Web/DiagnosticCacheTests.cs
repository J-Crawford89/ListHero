using System.Text;
using ListHero.Tests.Diagnostics;
using ListHero.Web.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ListHero.Tests.Web;

public sealed class DiagnosticCacheTests
{
    private const string Canary = "private-cache-key-value-and-exception-canary";
    [Fact]
    public async Task Cache_diagnostics_preserve_operations_and_log_only_outcomes_not_keys_values_or_errors()
    {
        using var logs = new DiagnosticLogs(); using var factory = logs.Factory();
        var inner = new TestCache();
        using var cache = new DiagnosticDistributedCache(inner, factory);
        Assert.Null(cache.Get(Canary));
        Assert.Null(await cache.GetAsync(Canary));
        var value = Encoding.UTF8.GetBytes(Canary);
        cache.Set(Canary, value, new()); Assert.Same(value, cache.Get(Canary));
        cache.Refresh(Canary); cache.Remove(Canary);
        await cache.SetAsync(Canary, value, new()); Assert.Same(value, await cache.GetAsync(Canary));
        await cache.RefreshAsync(Canary); await cache.RemoveAsync(Canary);
        inner.Failure = new HttpRequestException(Canary);
        Assert.Same(inner.Failure, await Record.ExceptionAsync(() => cache.GetAsync(Canary)));
        Assert.Same(inner.Failure, Record.Exception(() => cache.Get(Canary)));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(Canary, canceled.Token));
        Assert.DoesNotContain(Canary, logs.Text);
        Assert.Contains(logs.Entries, entry => entry.Event.Id == 2200 && entry.Message.Contains("miss"));
        Assert.Contains(logs.Entries, entry => entry.Event.Id == 2200 && entry.Message.Contains("hit"));
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("network"));
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("canceled"));
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
    }

    [Theory]
    [InlineData("instance")] [InlineData("factory")] [InlineData("type")]
    public void Cache_decoration_preserves_registration_and_disposal_ownership(string registration)
    {
        var services = new ServiceCollection(); services.AddLogging();
        var supplied = new TestCache();
        if (registration == "instance") services.AddSingleton<IDistributedCache>(supplied);
        else if (registration == "factory") services.AddSingleton<IDistributedCache>(_ => supplied);
        else services.AddSingleton<IDistributedCache, TestCache>();
        services.ObserveDistributedTokenCache();
        using (var provider = services.BuildServiceProvider())
        {
            var cache = Assert.IsType<DiagnosticDistributedCache>(provider.GetRequiredService<IDistributedCache>());
            cache.Set("key", [1], new()); Assert.Equal(new byte[] { 1 }, cache.Get("key"));
        }
        Assert.Equal(registration == "factory", supplied.Disposed);
    }

    public sealed class TestCache : IDistributedCache, IDisposable
    {
        private byte[]? value;
        public Exception? Failure;
        public bool Disposed;
        public byte[]? Get(string key) { if (Failure is not null) throw Failure; return value; }
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) { token.ThrowIfCancellationRequested(); return Task.FromResult(Get(key)); }
        public void Set(string key, byte[] bytes, DistributedCacheEntryOptions options) => value = bytes;
        public Task SetAsync(string key, byte[] bytes, DistributedCacheEntryOptions options, CancellationToken token = default) { Set(key, bytes, options); return Task.CompletedTask; }
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => value = null;
        public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
        public void Dispose() => Disposed = true;
    }
}
