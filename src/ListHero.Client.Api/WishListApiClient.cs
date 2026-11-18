using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Lists;

namespace ListHero.Client.Api;

// Token acquisition happens in the caller's scope, never in a pooled HTTP handler.
public sealed class WishListApiClient(HttpClient http, IApiAccessTokenProvider tokens) : IWishListApi
{
    public Task<OwnedListPage> GetMineAsync(int page = 1, int pageSize = OwnedListPaging.DefaultPageSize, CancellationToken cancellationToken = default)
        => SendAsync<OwnedListPage>(HttpMethod.Get, $"api/lists/mine?page={page}&pageSize={pageSize}", null, cancellationToken);

    public Task<OwnerWishListResponse> CreateAsync(CreateWishListRequest request, CancellationToken cancellationToken = default)
        => SendAsync<OwnerWishListResponse>(HttpMethod.Post, "api/lists/", request, cancellationToken);

    public Task<OwnerWishListResponse> GetOwnerViewAsync(Guid listId, CancellationToken cancellationToken = default)
        => SendAsync<OwnerWishListResponse>(HttpMethod.Get, $"api/lists/{listId}/owner", null, cancellationToken);

    public Task<OwnerWishListResponse> AddItemAsync(Guid listId, CreateWishListItemRequest request, CancellationToken cancellationToken = default)
        => SendAsync<OwnerWishListResponse>(HttpMethod.Post, $"api/lists/{listId}/items", request, cancellationToken);

    public Task<OwnerWishListResponse> UpdateAsync(Guid listId, UpdateWishListRequest request, CancellationToken cancellationToken = default)
        => SendAsync<OwnerWishListResponse>(HttpMethod.Put, $"api/lists/{listId}", request, cancellationToken);
    public async Task ArchiveAsync(Guid listId, ArchiveRequest request, CancellationToken cancellationToken = default)
        => _ = await SendAsync<object>(HttpMethod.Post, $"api/lists/{listId}/archive", request, cancellationToken);
    public Task<OwnerWishListResponse> UpdateItemAsync(Guid listId, Guid itemId, UpdateWishListItemRequest request, CancellationToken cancellationToken = default)
        => SendAsync<OwnerWishListResponse>(HttpMethod.Put, $"api/lists/{listId}/items/{itemId}", request, cancellationToken);
    public Task<OwnerWishListResponse> ArchiveItemAsync(Guid listId, Guid itemId, ArchiveRequest request, CancellationToken cancellationToken = default)
        => SendAsync<OwnerWishListResponse>(HttpMethod.Post, $"api/lists/{listId}/items/{itemId}/archive", request, cancellationToken);
    public Task<ItemEditWarningResponse> EditWarningAsync(Guid listId, Guid itemId, CancellationToken cancellationToken = default)
        => SendAsync<ItemEditWarningResponse>(HttpMethod.Get, $"api/lists/{listId}/items/{itemId}/edit-warning", null, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(cancellationToken));
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SignInRequiredException();
        if (response.StatusCode == HttpStatusCode.Conflict) throw new ApiConflictException();
        if (response.StatusCode == HttpStatusCode.NoContent && typeof(T) == typeof(object)) return default!;
        if (response.StatusCode == HttpStatusCode.BadRequest && response.Content.Headers.ContentType?.MediaType == "application/problem+json")
        {
            if (await ApiResponseReader.ReadProblemCodeAsync(response, cancellationToken) == "profile_required")
                throw new ApiRequestException("Set up your public profile before making this list public. Use the Public profile link to choose your name and handle.");
        }
        if (!response.IsSuccessStatusCode)
            throw new ApiRequestException(response.StatusCode switch
            {
                HttpStatusCode.Forbidden => "Your account cannot perform this action.",
                HttpStatusCode.NotFound => "This list is unavailable.",
                HttpStatusCode.BadRequest => "Check the details and try again. Names are required, quantities must be positive, and links must start with http:// or https://.",
                _ => "We couldn't save or load your list. Please try again."
            });
        return await ApiResponseReader.ReadRequiredAsync<T>(response, cancellationToken, writeMayHaveSucceeded: method != HttpMethod.Get);
    }
}
