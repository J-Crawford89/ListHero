using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Products;

namespace ListHero.Client.Api;

public sealed class ProductLinkApiClient(HttpClient http, IApiAccessTokenProvider tokens) : IProductLinkApi
{
    public async Task<ProductLinkResult> ImportAsync(string url, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/item-metadata") { Content = JsonContent.Create(new ProductLinkRequest(url)) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(ct));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SignInRequiredException();
        if (!response.IsSuccessStatusCode) throw new ApiRequestException("We couldn't read that product link. Check the link or enter the details yourself.");
        return await ApiResponseReader.ReadRequiredAsync<ProductLinkResult>(response, ct, writeMayHaveSucceeded: false);
    }
}
