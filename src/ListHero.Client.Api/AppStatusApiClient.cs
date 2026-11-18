using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.System;

namespace ListHero.Client.Api;

public sealed class AppStatusApiClient(HttpClient http) : IAppStatusApi
{
    public async Task<AppStatusResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/status", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ApiResponseReader.ReadRequiredAsync<AppStatusResponse>(response, cancellationToken);
    }
}
