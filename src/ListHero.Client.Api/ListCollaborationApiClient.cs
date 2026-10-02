using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Lists;

namespace ListHero.Client.Api;

public sealed class ListCollaborationApiClient(HttpClient http, IApiAccessTokenProvider tokens) : IListCollaborationApi
{
    public Task<IReadOnlyList<ShareLinkResponse>> GetLinksAsync(Guid listId, CancellationToken ct = default)
        => SendAsync<IReadOnlyList<ShareLinkResponse>>(HttpMethod.Get, $"api/lists/{listId}/share-links/", null, new(true), ct);
    public Task<ShareLinkResponse> CreateLinkAsync(Guid listId, CreateShareLinkRequest request, CancellationToken ct = default)
        => SendAsync<ShareLinkResponse>(HttpMethod.Post, $"api/lists/{listId}/share-links/", request, new(true), ct);
    public async Task RevokeLinkAsync(Guid listId, Guid linkId, CancellationToken ct = default)
        => _ = await SendAsync<object>(HttpMethod.Post, $"api/lists/{listId}/share-links/{linkId}/revoke", null, new(true), ct);
    public Task<ListViewResponse> ViewAsync(Guid listId, ViewerCredentials credentials, CancellationToken ct = default)
        => SendAsync<ListViewResponse>(HttpMethod.Get, $"api/lists/{listId}/view", null, credentials, ct);
    public Task<GuestCredentialResponse> IssueGuestAsync(Guid listId, ViewerCredentials credentials, CancellationToken ct = default)
        => SendAsync<GuestCredentialResponse>(HttpMethod.Post, $"api/lists/{listId}/guest-credential", null, credentials, ct);
    public Task<OwnPurchaseMarkResponse> MarkAsync(Guid listId, Guid itemId, CreatePurchaseMarkRequest request, ViewerCredentials credentials, CancellationToken ct = default)
        => SendAsync<OwnPurchaseMarkResponse>(HttpMethod.Post, $"api/lists/{listId}/items/{itemId}/marks", request, credentials, ct);
    public async Task UndoAsync(Guid listId, Guid itemId, Guid markId, ArchiveRequest request, ViewerCredentials credentials, CancellationToken ct = default)
        => _ = await SendAsync<object>(HttpMethod.Post, $"api/lists/{listId}/items/{itemId}/marks/{markId}/undo", request, credentials, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, ViewerCredentials credentials, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        // Signed-in callers must reauthenticate if token acquisition fails; silently downgrading
        // an owner to anonymous could expose purchase information to them.
        if (credentials.IsSignedIn)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(ct));
        if (credentials.ShareToken is not null) request.Headers.Add(CapabilityHeaders.Share, credentials.ShareToken);
        if (credentials.GuestCredential is not null) request.Headers.Add(CapabilityHeaders.Guest, credentials.GuestCredential);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SignInRequiredException();
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new ApiRequestException("This request conflicts with an earlier change. Reload the list. If retrying a purchase mark, use the same quantity.");
        if (!response.IsSuccessStatusCode)
            throw new ApiRequestException(response.StatusCode switch
            {
                HttpStatusCode.NotFound => "This list or link is unavailable. It may have been removed, revoked, or expired.",
                HttpStatusCode.Forbidden => "This action is unavailable. Sign in or use an active share link to mark gifts.",
                HttpStatusCode.BadRequest => "Check the quantity or expiration date and try again.",
                _ => "We couldn't complete that request. Please try again."
            });
        if (response.StatusCode == HttpStatusCode.NoContent) return default!;
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new ApiRequestException("The API returned an empty response.");
    }
}
