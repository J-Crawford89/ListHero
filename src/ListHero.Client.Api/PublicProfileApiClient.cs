using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Discovery;

namespace ListHero.Client.Api;

public sealed class PublicProfileApiClient(HttpClient http, IApiAccessTokenProvider tokens) : IPublicProfileApi
{
    public Task<OwnProfileResponse> GetAsync(CancellationToken ct = default) => SendAsync(null, ct);
    public Task<OwnProfileResponse> UpdateAsync(UpdatePublicProfileRequest request, CancellationToken ct = default) => SendAsync(request, ct);
    private async Task<OwnProfileResponse> SendAsync(UpdatePublicProfileRequest? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Put, "api/profile/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(ct));
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SignInRequiredException();
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            if (await ApiResponseReader.ReadProblemCodeAsync(response, ct) == "handle_unavailable")
                throw new ApiRequestException("That handle is already taken. Choose another.");
            throw new ApiConflictException();
        }
        if (!response.IsSuccessStatusCode)
            throw new ApiRequestException(response.StatusCode == HttpStatusCode.BadRequest
                ? "Check your public name, handle and discovery confirmation. Handles cannot be changed after setup."
                : "We couldn't load or save your public profile. Please try again.");
        return await ApiResponseReader.ReadRequiredAsync<OwnProfileResponse>(response, ct, writeMayHaveSucceeded: body is not null);
    }
}
