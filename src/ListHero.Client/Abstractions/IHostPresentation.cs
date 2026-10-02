namespace ListHero.Client.Abstractions;

public interface IHostPresentation
{
    bool CanSignIn { get; }
    bool IsDevelopment { get; }
    string SignInUrl { get; }
    string SignOutUrl { get; }
}
