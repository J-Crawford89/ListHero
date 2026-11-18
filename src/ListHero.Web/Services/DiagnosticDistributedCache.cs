using System.Diagnostics;
using ListHero.Hosting;
using Microsoft.Extensions.Caching.Distributed;

namespace ListHero.Web.Services;

public sealed class DiagnosticDistributedCache(IDistributedCache inner, ILoggerFactory loggers, bool ownsInner = true) : IDistributedCache, IDisposable
{
    private readonly ILogger logger = loggers.CreateLogger("ListHero.Diagnostics.TokenCache");
    public byte[]? Get(string key) => Run("read", () => inner.Get(key), value => value is null ? "miss" : "hit");
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        => RunAsync("read", () => inner.GetAsync(key, token), value => value is null ? "miss" : "hit", token);
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        => Run("write", () => { inner.Set(key, value, options); return true; }, _ => "success");
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        => RunAsync("write", async () => { await inner.SetAsync(key, value, options, token); return true; }, _ => "success", token);
    public void Refresh(string key) => Run("refresh", () => { inner.Refresh(key); return true; }, _ => "success");
    public Task RefreshAsync(string key, CancellationToken token = default)
        => RunAsync("refresh", async () => { await inner.RefreshAsync(key, token); return true; }, _ => "success", token);
    public void Remove(string key) => Run("remove", () => { inner.Remove(key); return true; }, _ => "success");
    public Task RemoveAsync(string key, CancellationToken token = default)
        => RunAsync("remove", async () => { await inner.RemoveAsync(key, token); return true; }, _ => "success", token);

    private T Run<T>(string operation, Func<T> action, Func<T, string> outcome)
    {
        var clock = Stopwatch.StartNew();
        try { var result = action(); Report(operation, outcome(result), clock, null); return result; }
        catch (Exception exception) { Report(operation, "failure", clock, exception); throw; }
    }
    private async Task<T> RunAsync<T>(string operation, Func<Task<T>> action, Func<T, string> outcome, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try { var result = await action(); Report(operation, outcome(result), clock, null); return result; }
        catch (Exception exception) { Report(operation, exception is OperationCanceledException && ct.IsCancellationRequested ? "canceled" : "failure", clock, exception); throw; }
    }
    private void Report(string operation, string outcome, Stopwatch clock, Exception? failure)
        => logger.Log(outcome == "failure" ? LogLevel.Warning : LogLevel.Debug, new EventId(2200, "TokenCacheOperation"),
            "Distributed token cache {Operation} {Outcome} {ElapsedMs} {Failure}", operation, outcome, clock.ElapsedMilliseconds, OperationalDiagnostics.Failure(failure));
    public void Dispose() { if (ownsInner && inner is IDisposable disposable) disposable.Dispose(); }
}

public static class TokenCacheDiagnosticsRegistration
{
    public static void ObserveDistributedTokenCache(this IServiceCollection services)
    {
        var original = services.Last(descriptor => descriptor.ServiceType == typeof(IDistributedCache));
        services.Remove(original);
        services.Add(new ServiceDescriptor(typeof(IDistributedCache), provider =>
        {
            var cache = original.ImplementationInstance ?? original.ImplementationFactory?.Invoke(provider)
                ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!);
            return new DiagnosticDistributedCache((IDistributedCache)cache, provider.GetRequiredService<ILoggerFactory>(), original.ImplementationInstance is null);
        }, original.Lifetime));
    }
}
