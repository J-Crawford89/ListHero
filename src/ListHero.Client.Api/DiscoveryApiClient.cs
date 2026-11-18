using System.Net;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Discovery;

namespace ListHero.Client.Api;

public sealed class DiscoveryApiClient(HttpClient http) : IDiscoveryApi
{
    public Task<DiscoveryPage<PublicListSummary>> GetListsAsync(DiscoveryQuery query, CancellationToken ct = default)
        => GetAsync<DiscoveryPage<PublicListSummary>>("api/discovery/lists" + Query(query), ct);
    public Task<DiscoveryPage<PublicPersonSummary>> GetPeopleAsync(DiscoveryQuery query, CancellationToken ct = default)
        => GetAsync<DiscoveryPage<PublicPersonSummary>>("api/discovery/people" + Query(query), ct);
    public Task<PublicProfileResponse> GetProfileAsync(string handle, int page = 1, CancellationToken ct = default)
        => GetAsync<PublicProfileResponse>($"api/discovery/people/{Uri.EscapeDataString(handle)}?page={page}", ct);
    private static string Query(DiscoveryQuery q)
        => $"?q={Uri.EscapeDataString(q.Query ?? "")}&page={q.Page}&pageSize={q.PageSize}";
    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        if (!response.IsSuccessStatusCode)
            throw new ApiRequestException(response.StatusCode switch
            {
                HttpStatusCode.NotFound => "This public profile is unavailable.",
                HttpStatusCode.BadRequest => "Search using 2–100 characters and a valid page number.",
                _ => "We couldn't load discovery. Please try again."
            });
        return await ApiResponseReader.ReadRequiredAsync<T>(response, ct);
    }
}
