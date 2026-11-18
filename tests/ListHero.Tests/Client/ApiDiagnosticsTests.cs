using System.Net;
using ListHero.Client.Api;
using ListHero.Tests.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ListHero.Tests.Client;

public sealed class ApiDiagnosticsTests
{
    [Theory]
    [InlineData(200, true)] [InlineData(503, false)]
    public async Task API_diagnostics_correlate_safe_ids_without_logging_private_urls_credentials_or_bodies(int status, bool validId)
    {
        const string secret = "private-url-body-bearer-cookie-canary";
        using var logs = new DiagnosticLogs(); using var factory = logs.Factory();
        var reference = validId ? Guid.NewGuid().ToString("N") : secret;
        using var handler = new ApiDiagnosticsHandler(factory.CreateLogger<ApiDiagnosticsHandler>()) { InnerHandler = new Reply((HttpStatusCode)status, reference) };
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.test/root/api/lists/" + Guid.NewGuid() + "?secret=" + secret)
        { Content = new StringContent(secret) };
        request.Headers.Authorization = new("Bearer", secret); request.Headers.Add("Cookie", secret);
        using var response = await http.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Contains(logs.Entries, entry => entry.Event.Id == 3100 && entry.Message.Contains(validId ? reference : "none"));
        Assert.DoesNotContain(secret, logs.Text);
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
    }

    [Theory]
    [InlineData("network")] [InlineData("canceled")] [InlineData("unexpected")]
    public async Task Transport_failures_are_classified_without_exception_details_or_automatic_retry(string kind)
    {
        using var logs = new DiagnosticLogs(); using var factory = logs.Factory();
        using var cancellation = new CancellationTokenSource();
        if (kind == "canceled") cancellation.Cancel();
        Exception failure = kind == "network" ? new HttpRequestException("private-network-diagnostic-canary")
            : kind == "canceled" ? new OperationCanceledException(cancellation.Token) : new InvalidOperationException("private-network-diagnostic-canary");
        using var handler = new ApiDiagnosticsHandler(factory.CreateLogger<ApiDiagnosticsHandler>()) { InnerHandler = new FailedReply(failure) };
        using var http = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.test/unknown/private-path");
        Assert.Same(failure, await Record.ExceptionAsync(() => http.SendAsync(request, cancellation.Token)));
        Assert.Contains(logs.Entries, entry => entry.Event.Id == 3101 && entry.Message.Contains(kind));
        Assert.DoesNotContain("private-network-diagnostic-canary", logs.Text);
        Assert.DoesNotContain("private-path", logs.Text);
    }
    private sealed class Reply(HttpStatusCode status, string id) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { var response = new HttpResponseMessage(status); response.Headers.Add("X-ListHero-Request-Id", id); return Task.FromResult(response); }
    }
    private sealed class FailedReply(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromException<HttpResponseMessage>(failure);
    }
}
