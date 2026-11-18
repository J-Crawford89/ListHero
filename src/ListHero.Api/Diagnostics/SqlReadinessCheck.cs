using System.Diagnostics;
using ListHero.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ListHero.Api.Diagnostics;

public sealed class SqlReadinessCheck(IServiceScopeFactory scopes, ILoggerFactory loggers) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var ready = false;
        var outcome = "unavailable";
        try
        {
            using var scope = scopes.CreateScope();
            ready = await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.CanConnectAsync(timeout.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ready && timeout.IsCancellationRequested) outcome = "timeout";
            if (ready) outcome = "ready";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { outcome = "timeout"; }
        catch (Exception) { outcome = "failure"; }
        loggers.CreateLogger("ListHero.Diagnostics.Database").Log(ready ? clock.ElapsedMilliseconds >= 1000 ? LogLevel.Information : LogLevel.Debug : LogLevel.Warning,
            new EventId(3000, "DatabaseReadiness"), "Database readiness {Outcome} {ElapsedMs}", outcome, clock.ElapsedMilliseconds);
        return ready ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
    }
}
