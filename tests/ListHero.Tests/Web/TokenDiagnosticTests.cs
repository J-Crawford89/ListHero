using System.Reflection;
using ListHero.Client.Abstractions.Api;
using ListHero.Client.Services;
using ListHero.Tests.Diagnostics;
using ListHero.Web.Services;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;

namespace ListHero.Tests.Web;

public sealed partial class WebServiceTests
{
    [Theory]
    [InlineData("anonymous")] [InlineData("session-changed")] [InlineData("interaction-required")]
    [InlineData("challenge-required")] [InlineData("network")]
    public async Task Token_recovery_logs_allowlisted_reasons_without_identity_exception_or_token_values(string scenario)
    {
        const string secret = "private-token-identity-exception-canary";
        using var logs = new DiagnosticLogs(); using var factory = logs.Factory();
        var acquisition = DispatchProxy.Create<ITokenAcquisition, AcquisitionProxy>();
        var proxy = (AcquisitionProxy)acquisition;
        var user = new UserState(scenario != "anonymous");
        var state = new ClientSessionState();
        if (scenario == "session-changed") state.RequireSignIn();
        if (scenario == "interaction-required") proxy.Failure = new MsalUiRequiredException(secret, secret);
        if (scenario == "challenge-required") proxy.Failure = new MicrosoftIdentityWebChallengeUserException(new MsalUiRequiredException(secret, secret), [secret], secret);
        if (scenario == "network") proxy.Failure = new HttpRequestException(secret);
        var provider = new WebApiAccessTokenProvider(user, acquisition, Configuration(), state, factory);
        if (scenario == "network") Assert.Same(proxy.Failure, await Record.ExceptionAsync(() => provider.GetAsync()));
        else await Assert.ThrowsAsync<SignInRequiredException>(() => provider.GetAsync());
        Assert.Contains(logs.Entries, entry => entry.Message.Contains(scenario));
        Assert.DoesNotContain(secret, logs.Text);
        Assert.DoesNotContain(user.User.FindFirst("oid")!.Value, logs.Text);
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
    }
}
