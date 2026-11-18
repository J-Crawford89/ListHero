using ListHero.Api.Diagnostics;
using ListHero.Tests.Diagnostics;
using ListHero.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ListHero.Tests.Web;

public sealed class InfrastructureDiagnosticTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Circuit_failures_are_redacted_and_rethrown_without_changing_circuit_behavior(bool canceled)
    {
        using var logs = new DiagnosticLogs(); using var factory = logs.Factory();
        var handler = new DiagnosticCircuitHandler(factory);
        Exception failure = canceled ? new OperationCanceledException("private-circuit-canary") : new InvalidOperationException("private-circuit-canary");
        var activity = handler.CreateInboundActivityHandler(_ => Task.FromException(failure));
        Assert.Same(failure, await Record.ExceptionAsync(() => activity(null!)));
        Assert.Contains(logs.Entries, entry => entry.Event.Id == 2300);
        Assert.DoesNotContain("private-circuit-canary", logs.Text);
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Readiness_timeouts_and_configuration_failures_return_generic_results(bool timeout)
    {
        using var logs = new DiagnosticLogs(); using var factory = logs.Factory();
        Exception failure = timeout ? new OperationCanceledException("private-database-canary") : new InvalidOperationException("private-database-canary");
        var check = new SqlReadinessCheck(new FailingScope(failure), factory);
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new())).Status);
        Assert.DoesNotContain("private-database-canary", logs.Text);
        Assert.Contains(logs.Entries, entry => entry.Event.Id == 3000 && entry.Message.Contains(timeout ? "timeout" : "failure"));
        if (timeout)
        {
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.CheckHealthAsync(new(), canceled.Token));
        }
    }
    private sealed class FailingScope(Exception failure) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => throw failure;
        public void Dispose() { }
    }
}
