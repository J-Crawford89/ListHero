using ListHero.Client.Abstractions.Api;
using Microsoft.AspNetCore.Components;

namespace ListHero.UI.Components;

public abstract class DiscoveryPageBase : ComponentBase, IDisposable
{
    private CancellationTokenSource? current;
    private bool disposed;
    protected bool IsBusy { get; private set; }
    protected string? Error { get; private set; }
    protected bool RequiresSignIn { get; private set; }

    protected void ResetLoad()
    {
        current?.Cancel();
        current = null;
        IsBusy = false;
        Error = null;
        RequiresSignIn = false;
    }

    protected async Task LoadAsync<T>(Func<CancellationToken, Task<T>> read, Action<T> apply)
    {
        if (disposed) return;
        current?.Cancel();
        var source = new CancellationTokenSource();
        current = source;
        IsBusy = true; Error = null; RequiresSignIn = false; StateHasChanged();
        try
        {
            var result = await read(source.Token);
            if (!disposed && current == source && !source.IsCancellationRequested) apply(result);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception e) when (e is SignInRequiredException or ApiRequestException or HttpRequestException or OperationCanceledException)
        {
            if (!disposed && current == source)
            {
                RequiresSignIn = e is SignInRequiredException;
                Error = e is ApiRequestException or SignInRequiredException ? e.Message : "We couldn't load this page. Please try again.";
            }
        }
        finally
        {
            if (current == source) { current = null; IsBusy = false; if (!disposed) StateHasChanged(); }
            source.Dispose();
        }
    }
    public void Dispose() { disposed = true; current?.Cancel(); GC.SuppressFinalize(this); }
}
