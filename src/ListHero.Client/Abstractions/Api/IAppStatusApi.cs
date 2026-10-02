using ListHero.Contracts.System;

namespace ListHero.Client.Abstractions.Api;

public interface IAppStatusApi
{
    Task<AppStatusResponse> GetAsync(CancellationToken cancellationToken = default);
}
