using ListHero.Client.Abstractions.Api;
using ListHero.Client.Abstractions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using ListHero.Hosting;
using System.Diagnostics;

namespace ListHero.Web.Services;

public sealed class WebApiAccessTokenProvider(AuthenticationStateProvider authentication,
    ITokenAcquisition acquisition, IConfiguration configuration, IClientSessionState session, ILoggerFactory loggers) : IApiAccessTokenProvider
{
    private readonly ILogger logger = loggers.CreateLogger("ListHero.Diagnostics.Tokens");
    public async Task<string> GetAsync(CancellationToken cancellationToken = default)
    {
        using var operation = AuthenticationDiagnostics.BeginOperation(logger);
        if (session.RequiresSignIn) { Reauthentication("session-changed"); throw new SignInRequiredException(); }
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (user.Identity?.IsAuthenticated != true) { Reauthentication("anonymous"); throw new SignInRequiredException(); }
        if (long.TryParse(user.FindFirst(SessionPolicy.Started)?.Value, out var started)
            && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(started) >= TimeSpan.FromDays(90)) { Reauthentication("absolute-expiry"); throw new SignInRequiredException(); }
        var scopes = configuration.GetSection("ListHeroApi:Scopes").Get<string[]>()
            ?? throw new InvalidOperationException("ListHeroApi:Scopes is required.");
        var clock = Stopwatch.StartNew();
        try
        {
            var token = await acquisition.GetAccessTokenForUserAsync(scopes,
                authenticationScheme: OpenIdConnectDefaults.AuthenticationScheme, user: user,
                tokenAcquisitionOptions: new TokenAcquisitionOptions { CancellationToken = cancellationToken });
            logger.LogDebug(new EventId(2004, "TokenAcquired"), "API token acquired {ElapsedMs}", clock.ElapsedMilliseconds);
            return token;
        }
        catch (MicrosoftIdentityWebChallengeUserException) { Reauthentication("challenge-required"); throw new SignInRequiredException(); }
        catch (MsalUiRequiredException) { Reauthentication("interaction-required"); throw new SignInRequiredException(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(new EventId(2006, "TokenAcquisitionFailed"), "API token acquisition failed {Failure} {ElapsedMs}", OperationalDiagnostics.Failure(exception), clock.ElapsedMilliseconds);
            throw;
        }
    }
    private void Reauthentication(string reason) => logger.LogInformation(new EventId(2005, "SignInRecoveryRequired"), "Sign-in recovery required {Reason}", reason);
}

public sealed class UnconfiguredApiAccessTokenProvider : IApiAccessTokenProvider
{
    public Task<string> GetAsync(CancellationToken cancellationToken = default) => throw new SignInRequiredException();
}
