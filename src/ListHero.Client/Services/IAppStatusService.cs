namespace ListHero.Client.Services;

public sealed record ConnectionStatus(bool IsAvailable, string Message);

public interface IAppStatusService
{
    Task<ConnectionStatus> CheckAsync(CancellationToken cancellationToken = default);
}
