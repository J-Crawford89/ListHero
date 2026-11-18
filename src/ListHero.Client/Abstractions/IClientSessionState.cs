namespace ListHero.Client.Abstractions;

public interface IClientSessionState
{
    bool RequiresSignIn { get; }
    void RequireSignIn();
}
