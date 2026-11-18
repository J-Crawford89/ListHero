using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Lists;
namespace ListHero.Client.Api;
public sealed class DashboardApiClient(HttpClient http, IApiAccessTokenProvider tokens) : IDashboardApi
{
    public async Task<DashboardResponse> GetAsync(CancellationToken ct = default)
    { using var response = await SendAsync(HttpMethod.Get, "api/dashboard/", null, null, ct); return await response.Content.ReadFromJsonAsync<DashboardResponse>(ct) ?? throw new ApiRequestException("Empty dashboard response."); }
    public async Task<SavedListPage> SavedAsync(int page = 1, CancellationToken ct = default)
    { using var response = await SendAsync(HttpMethod.Get, $"api/dashboard/saved?page={page}", null, null, ct); return await response.Content.ReadFromJsonAsync<SavedListPage>(ct) ?? throw new ApiRequestException("Empty saved lists response."); }
    public async Task SaveAsync(Guid listId, bool pinned, string? shareToken = null, CancellationToken ct = default)
    { using var response = await SendAsync(HttpMethod.Put, $"api/dashboard/saved/{listId}", new SaveListRequest { IsPinned = pinned }, shareToken, ct); }
    public async Task RemoveAsync(Guid listId, CancellationToken ct = default)
    { using var response = await SendAsync(HttpMethod.Delete, $"api/dashboard/saved/{listId}", null, null, ct); }
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? share, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(ct));
        if (body is not null) request.Content = JsonContent.Create(body);
        if (share is not null) request.Headers.Add(CapabilityHeaders.Share, share);
        var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode; response.Dispose();
        if (status == HttpStatusCode.Unauthorized) throw new SignInRequiredException();
        throw new ApiRequestException(status == HttpStatusCode.NotFound ? "This list is unavailable. Reopen a valid share link to save it again." : "We couldn't update your saved lists. Please try again.");
    }
}
