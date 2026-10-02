using ListHero.Client.Abstractions.Api;
using ListHero.Client.Services;
using ListHero.Contracts.System;

namespace ListHero.Tests.Client;

public sealed class ConnectionStatusTests
{
    [Theory]
    [InlineData("not-ready", "not ready")]
    [InlineData("network", "Could not reach")]
    [InlineData("timeout", "timed out")]
    public async Task Unavailable_API_is_reported_with_the_correct_reason(string outcome, string message)
    {
        var status = await new AppStatusService(new StatusApi(outcome)).CheckAsync();
        Assert.False(status.IsAvailable); Assert.Contains(message, status.Message);
    }
    [Fact]
    public async Task Caller_cancellation_is_propagated_instead_of_reported_as_a_timeout()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AppStatusService(new StatusApi("timeout")).CheckAsync(cancellation.Token));
    }
    private sealed class StatusApi(string outcome) : IAppStatusApi
    {
        public Task<AppStatusResponse> GetAsync(CancellationToken ct = default) => outcome switch
        {
            "network" => throw new HttpRequestException(),
            "timeout" => throw new OperationCanceledException(ct),
            _ => Task.FromResult(new AppStatusResponse("List Hero", outcome))
        };
    }
}
