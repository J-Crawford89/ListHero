using ListHero.Client.Abstractions.Api;

namespace ListHero.Client.Services;

public sealed class AppStatusService(IAppStatusApi api) : IAppStatusService
{
    public async Task<ConnectionStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await api.GetAsync(cancellationToken);
            return status.Status == "available"
                ? new(true, "List Hero API is available.")
                : new(false, "List Hero API is not ready.");
        }
        catch (HttpRequestException)
        {
            return new(false, "Could not reach the List Hero API.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "The API connection timed out.");
        }
    }
}
