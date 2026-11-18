using ListHero.Client.Abstractions;

namespace ListHero.Client.Services;

// Circuit/WebView scoped. Only a fresh page/session can clear a detected identity change.
public sealed class ClientSessionState : IClientSessionState
{
    public bool RequiresSignIn { get; private set; }
    public void RequireSignIn() => RequiresSignIn = true;
}
