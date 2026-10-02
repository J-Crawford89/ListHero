using ListHero.Client.Abstractions.Api;
using Microsoft.AspNetCore.Components;

namespace ListHero.UI.Components;

public abstract class RequestPageBase : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    protected RequestPageBase() => Cancellation = lifetime.Token;
    protected CancellationToken Cancellation { get; }
    protected bool IsBusy { get; private set; }
    protected string? Error { get; private set; }
    protected bool RequiresSignIn { get; private set; }
    protected bool IsDisposed { get; private set; }
    protected async Task RunAsync(Func<Task> action)
    {
        if (IsBusy || IsDisposed) return;
        IsBusy = true; Error = null; RequiresSignIn = false; StateHasChanged();
        try { await action(); }
        catch (SignInRequiredException exception) { Error = exception.Message; RequiresSignIn = true; }
        catch (ApiRequestException exception) { Error = exception.Message; }
        catch (HttpRequestException) { Error = "We couldn't reach List Hero. Please try again."; }
        catch (OperationCanceledException) when (!IsDisposed) { Error = "The request took too long. Please try again."; }
        catch (OperationCanceledException) when (IsDisposed) { }
        finally { IsBusy = false; if (!IsDisposed) StateHasChanged(); }
    }
    public virtual void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true; lifetime.Cancel(); lifetime.Dispose(); GC.SuppressFinalize(this);
    }
}
