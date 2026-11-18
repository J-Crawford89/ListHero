using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ListHero.Application.Identity;
using ListHero.Domain.Entities;
using ListHero.Infrastructure.Persistence;
using ListHero.Tests.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ListHero.Tests.Api;

public sealed partial class OwnerWorkflowTests
{
    [Fact]
    public async Task Request_diagnostics_ignore_caller_correlation_private_paths_queries_payloads_and_credentials()
    {
        const string secret = "private-header-query-title-and-credential-canary";
        using var logs = new DiagnosticLogs();
        await using var factory = new OwnerApiFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("Logging:LogLevel:Default", "Debug").ConfigureLogging(logging => logging.AddProvider(logs)));
        using var http = SignedInClient(factory);
        http.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        http.DefaultRequestHeaders.Add("Cookie", secret); http.DefaultRequestHeaders.Add("X-ListHero-Share-Key", secret);
        http.DefaultRequestHeaders.Add("X-ListHero-Request-Id", secret);
        var response = await http.PostAsJsonAsync("/api/lists/?query=" + secret, new { Name = secret });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = Assert.Single(response.Headers.GetValues("X-ListHero-Request-Id"));
        Assert.True(Guid.TryParseExact(id, "N", out _));
        var listId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/lists/{listId}/owner?query={secret}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/live")).StatusCode);
        Assert.DoesNotContain(secret, logs.Text);
        Assert.DoesNotContain(listId.ToString(), logs.Text);
        Assert.Contains(logs.Entries, entry => entry.Event.Id == 1001 && entry.Scopes.Any(scope => scope.Contains(id)));
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
    }

    [Theory]
    [InlineData("unexpected")] [InlineData("database")] [InlineData("wrapped-database")]
    [InlineData("timeout")] [InlineData("canceled")] [InlineData("network")]
    public async Task Unexpected_API_errors_have_safe_diagnostics_and_a_matching_response_reference(string kind)
    {
        using var logs = new DiagnosticLogs();
        await using var factory = new OwnerApiFactory(overrides: services => services.AddScoped<IUserService>(_ => new FailingDiagnosticUser(kind)))
            .WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var http = SignedInClient(factory);
        var response = await http.GetAsync("/api/lists/mine?private=private-exception-canary");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-ListHero-Request-Id")), problem.GetProperty("requestId").GetString());
        Assert.DoesNotContain("private-exception-canary", logs.Text);
        Assert.Contains(logs.Entries, entry => entry.Event.Id == 1001 && entry.Level == LogLevel.Error && entry.Message.Contains("500"));
        Assert.Contains(logs.Entries, entry => entry.Event.Id == 1001 && entry.Message.Contains(kind == "wrapped-database" ? "database" : kind));
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
    }

    [SqlServerFact]
    public async Task Database_readiness_reports_unavailable_without_breaking_liveness_or_disclosing_connection_details()
    {
        var connection = IsolatedSqlConnection("_Readiness");
        using var logs = new DiagnosticLogs();
        await using var factory = new OwnerApiFactory(connection).WithWebHostBuilder(builder =>
            builder.UseSetting("Logging:LogLevel:Default", "Debug").ConfigureLogging(logging => logging.AddProvider(logs)));
        using var http = SignedInClient(factory);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/live")).StatusCode);
        var missing = await http.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
        Assert.Equal("unavailable", await missing.Content.ReadAsStringAsync());
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.MigrateAsync();
        var ready = await http.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode); Assert.Equal("ready", await ready.Content.ReadAsStringAsync());
        Assert.Contains("no-store", ready.Headers.CacheControl!.ToString());
        Assert.Contains(logs.Entries, entry => entry.Event.Id == 3000 && entry.Level == LogLevel.Warning);
        Assert.DoesNotContain(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection).InitialCatalog, logs.Text);
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
    }

    private sealed class FailingDiagnosticUser(string kind) : IUserService
    {
        public Task<User> ResolveAsync(ExternalUserIdentity identity, CancellationToken ct = default)
            => throw (kind switch
            {
                "database" => new DiagnosticDatabaseFailure(),
                "wrapped-database" => new InvalidOperationException("private-exception-canary", new DiagnosticDatabaseFailure()),
                "timeout" => new TimeoutException("private-exception-canary"),
                "canceled" => new OperationCanceledException("private-exception-canary"),
                "network" => new HttpRequestException("private-exception-canary"),
                _ => new InvalidOperationException("private-exception-canary")
            });
    }
    private sealed class DiagnosticDatabaseFailure() : System.Data.Common.DbException("private-exception-canary");
}
