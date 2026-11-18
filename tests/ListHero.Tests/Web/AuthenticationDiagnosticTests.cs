using System.Net;
using ListHero.Tests.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace ListHero.Tests.Web;

public sealed partial class WebHostTests
{
    [Fact]
    public async Task Login_and_antiforgery_diagnostics_do_not_record_return_queries_cookies_or_identity_payloads()
    {
        const string secret = "private-cookie-query-return-destination-canary";
        using var logs = new DiagnosticLogs();
        await using var factory = new WebFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("Logging:LogLevel:Default", "Debug").ConfigureLogging(logging => logging.AddProvider(logs)));
        using var http = factory.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        http.DefaultRequestHeaders.Add("Cookie", secret);
        var challenge = await http.GetAsync("/account/sign-in?rememberMe=true&returnUrl=" + Uri.EscapeDataString("/discover?q=" + secret));
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/account/session?private=" + secret, new StringContent(secret))).StatusCode);
        Assert.DoesNotContain(secret, logs.Text);
        Assert.Contains(logs.Entries, entry => entry.Event.Id == 2000 && entry.Message.Contains("True"));
        Assert.Contains(logs.Entries, entry => entry.Event.Id == 2103 && entry.Message.Contains("antiforgery-rejected"));
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
    }
}
