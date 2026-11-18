using System.Diagnostics;
using System.Data.Common;
using System.Reflection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Routing;

namespace ListHero.Hosting;

public static class OperationalDiagnostics
{
    public const string RequestHeader = "X-ListHero-Request-Id";
    public static string Instance { get; } = Guid.NewGuid().ToString("N");

    public static void AddOperationalDiagnostics(this WebApplicationBuilder builder)
    {
        // Replace payload/URL/exception logging in these categories with the redacted events below.
        foreach (var category in new[] { "System.Net.Http.HttpClient", "Microsoft.EntityFrameworkCore",
            "Microsoft.Identity.Web", "Microsoft.Identity.Client", "Microsoft.AspNetCore.Authentication",
            "Microsoft.AspNetCore.Diagnostics", "Microsoft.AspNetCore.Hosting.Diagnostics",
            "Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost" })
            builder.Logging.AddFilter(category, LogLevel.None);
        builder.Logging.AddJsonConsole(options => { options.IncludeScopes = true; options.UseUtcTimestamp = true; options.TimestampFormat = "O"; });
    }

    public static void UseOperationalDiagnostics(this WebApplication app, string host)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ListHero.Diagnostics.Http");
        var version = typeof(OperationalDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var release = version is { Length: <= 128 } && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+') ? version : "unknown";
        var certificate = app.Services.GetService<System.Security.Cryptography.X509Certificates.X509Certificate2>();
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            logger.LogInformation(new EventId(1000, "HostStarted"), "Host started {Host} {Instance} {Release}", host, Instance, release);
            if (certificate is not null)
            {
                var days = (int)(certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays;
                logger.Log(days <= 30 ? LogLevel.Warning : LogLevel.Information, new EventId(1002, "KeyCertificateExpiry"),
                    "Key-protection certificate expiry {ExpiresUtc} {DaysRemaining}", certificate.NotAfter.ToUniversalTime(), days);
            }
        });
        app.Use(async (context, next) =>
        {
            // Never echo a caller-supplied correlation header, URL, identity, or capability.
            var requestId = Guid.NewGuid().ToString("N");
            context.TraceIdentifier = requestId;
            context.Response.OnStarting(() => { context.Response.Headers[RequestHeader] = requestId; return Task.CompletedTask; });
            using var scope = logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = requestId, ["Host"] = host, ["Instance"] = Instance, ["Release"] = release });
            var clock = Stopwatch.StartNew();
            Exception? unhandled = null;
            try { await next(context); }
            catch (Exception exception) { unhandled = exception; throw; }
            finally
            {
                var error = unhandled ?? context.Features.Get<IExceptionHandlerFeature>()?.Error;
                var status = unhandled is null ? context.Response.StatusCode : context.RequestAborted.IsCancellationRequested ? 499 : 500;
                var level = status >= 500 ? LogLevel.Error : clock.ElapsedMilliseconds >= 1000 ? LogLevel.Information : LogLevel.Debug;
                var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
                logger.Log(level, new EventId(1001, "RequestCompleted"),
                    "Request completed {Route} {StatusCode} {ElapsedMs} {Failure}", route, status, clock.ElapsedMilliseconds, Failure(error));
            }
        });
    }

    public static string Failure(Exception? exception) => exception switch
    {
        null => "none",
        DbException => "database",
        { InnerException: DbException } => "database",
        TimeoutException => "timeout",
        OperationCanceledException => "canceled",
        HttpRequestException => "network",
        _ => "unexpected"
    };

}
