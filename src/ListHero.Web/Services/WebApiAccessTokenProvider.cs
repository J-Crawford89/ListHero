using ListHero.Client.Abstractions.Api;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;

namespace ListHero.Web.Services;

public sealed class WebApiAccessTokenProvider(AuthenticationStateProvider authentication,
    ITokenAcquisition acquisition, IConfiguration configuration) : IApiAccessTokenProvider
{
    public async Task<string> GetAsync(CancellationToken cancellationToken = default)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        if (user.Identity?.IsAuthenticated != true) throw new SignInRequiredException();
        var scopes = configuration.GetSection("ListHeroApi:Scopes").Get<string[]>()
            ?? throw new InvalidOperationException("ListHeroApi:Scopes is required.");
        try
        {
            return await acquisition.GetAccessTokenForUserAsync(scopes,
                authenticationScheme: OpenIdConnectDefaults.AuthenticationScheme, user: user,
                tokenAcquisitionOptions: new TokenAcquisitionOptions { CancellationToken = cancellationToken });
        }
        catch (MicrosoftIdentityWebChallengeUserException) { throw new SignInRequiredException(); }
        catch (MsalUiRequiredException) { throw new SignInRequiredException(); }
    }
}

public sealed class UnconfiguredApiAccessTokenProvider : IApiAccessTokenProvider
{
    public Task<string> GetAsync(CancellationToken cancellationToken = default) => throw new SignInRequiredException();
}
