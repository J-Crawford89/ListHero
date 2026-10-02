using System.Net.Http.Json;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.System;

namespace ListHero.Client.Api;

public sealed class AppStatusApiClient(HttpClient http) : IAppStatusApi
{
    public async Task<AppStatusResponse> GetAsync(CancellationToken cancellationToken = default)
        => await http.GetFromJsonAsync<AppStatusResponse>("api/status", cancellationToken)
            ?? throw new HttpRequestException("The API returned an empty status response.");
}
