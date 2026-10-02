namespace ListHero.Client.Abstractions;

// Hosts provide storage. The credential is separate from a shared list's access token.
public interface IGuestCredentialStore
{
    ValueTask<string?> ReadAsync();
    ValueTask WriteAsync(string credential);
    ValueTask ClearAsync();
}
