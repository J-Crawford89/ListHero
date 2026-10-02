using ListHero.Client.Abstractions;

namespace ListHero.Web.Services;

public sealed class WebHostPresentation(IConfiguration configuration, IWebHostEnvironment environment) : IHostPresentation
{
    public bool CanSignIn => configuration.GetValue<bool>("Authentication:Enabled");
    public bool IsDevelopment => environment.IsDevelopment();
    public string SignInUrl => "/account/sign-in";
    public string SignOutUrl => "/account/sign-out";
}
