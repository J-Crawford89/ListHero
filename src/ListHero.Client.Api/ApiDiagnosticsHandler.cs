using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ListHero.Client.Api;

// This pooled handler holds no user/circuit state and never reads credentials or payloads.
public sealed class ApiDiagnosticsHandler(ILogger<ApiDiagnosticsHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var group = request.RequestUri?.AbsolutePath.Split('/').SkipWhile(part => part != "api").Skip(1).FirstOrDefault();
        if (group is not ("lists" or "discovery" or "dashboard" or "profile" or "status")) group = "other";
        using var operation = logger.BeginScope(new Dictionary<string, object> { ["ApiOperationId"] = Guid.NewGuid().ToString("N") });
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var reference = response.Headers.TryGetValues("X-ListHero-Request-Id", out var values)
                && Guid.TryParseExact(values.FirstOrDefault(), "N", out var id) ? id.ToString("N") : "none";
            var status = (int)response.StatusCode;
            logger.Log(status >= 500 ? LogLevel.Warning : clock.ElapsedMilliseconds >= 1000 ? LogLevel.Information : LogLevel.Debug,
                new EventId(3100, "ApiCallCompleted"), "API call completed {Group} {StatusCode} {ElapsedMs} {ApiRequestId}",
                group, status, clock.ElapsedMilliseconds, reference);
            return response;
        }
        catch (Exception exception)
        {
            logger.Log(exception is OperationCanceledException && cancellationToken.IsCancellationRequested ? LogLevel.Debug : LogLevel.Warning,
                new EventId(3101, "ApiCallFailed"), "API call failed {Group} {Failure} {ElapsedMs}", group,
                exception is OperationCanceledException ? "canceled" : exception is HttpRequestException ? "network" : "unexpected", clock.ElapsedMilliseconds);
            throw;
        }
    }
}
