namespace ListHero.Client.Abstractions;

// Hosts protect drafts and bind them to the current account; the shared UI never stores plaintext.
public interface IDraftRecoveryStore
{
    ValueTask<string?> ReadAsync(string context, bool allowAnonymous = false);
    ValueTask WriteAsync(string context, string content, bool allowAnonymous = false);
    ValueTask RemoveAsync(string context, bool allowAnonymous = false);
}

public sealed class DraftStorageException() : Exception("Draft recovery is unavailable. Keep this page open or copy your work before leaving.");
