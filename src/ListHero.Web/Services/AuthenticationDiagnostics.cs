using ListHero.Hosting;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace ListHero.Web.Services;

public static class AuthenticationDiagnostics
{
    public static ILogger Logger(HttpContext context, string area)
        => context.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger("ListHero.Diagnostics." + area)
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public static IDisposable? BeginOperation(ILogger logger)
        => logger.BeginScope(new Dictionary<string, object> { ["OperationId"] = Guid.NewGuid().ToString("N"), ["Instance"] = OperationalDiagnostics.Instance });
    public static void SessionEvent(HttpContext context, string operation, string outcome)
        => Logger(context, "Session").LogInformation(new EventId(2103, "SessionRecovery"),
            "Session recovery {Operation} {Outcome}", operation, outcome);

    public static void Configure(OpenIdConnectOptions options)
    {
        var redirect = options.Events.OnRedirectToIdentityProvider;
        options.Events.OnRedirectToIdentityProvider = async context =>
        {
            await redirect(context);
            Logger(context.HttpContext, "Identity").LogInformation(new EventId(2000, "SignInChallenge"),
                "Sign-in challenge {Persistent} {ProtectedReturn}", context.Properties.IsPersistent, context.HttpContext.Request.Cookies.ContainsKey("ListHero.PendingReturn"));
        };
        var validated = options.Events.OnTokenValidated;
        options.Events.OnTokenValidated = async context =>
        {
            await validated(context);
            Logger(context.HttpContext, "Identity").LogInformation(new EventId(2001, "IdentityValidated"), "Identity validation completed");
        };
        var failed = options.Events.OnAuthenticationFailed;
        options.Events.OnAuthenticationFailed = async context =>
        {
            Logger(context.HttpContext, "Identity").LogWarning(new EventId(2002, "IdentityFailed"), "Identity validation failed");
            await failed(context);
        };
        var remote = options.Events.OnRemoteFailure;
        options.Events.OnRemoteFailure = async context =>
        {
            Logger(context.HttpContext, "Identity").LogWarning(new EventId(2003, "IdentityRemoteFailure"), "Identity provider returned a failure");
            await remote(context);
        };
    }
}
