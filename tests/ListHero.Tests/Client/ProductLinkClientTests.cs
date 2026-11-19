using System.Net;
using System.Net.Http.Json;
using ListHero.Client.Abstractions.Api;
using ListHero.Client.Api;
using ListHero.Contracts.Products;

namespace ListHero.Tests.Client;

public sealed class ProductLinkClientTests
{
    [Theory]
    [InlineData(200)] [InlineData(401)] [InlineData(403)] [InlineData(500)]
    public async Task Import_posts_URL_in_body_with_bearer_token_and_reports_safe_failures(int status)
    {
        using var handler = new Handler(status); using var http = new HttpClient(handler) { BaseAddress = new("https://api.example/") };
        var api = new ProductLinkApiClient(http, new Tokens());
        if (status == 200) Assert.Equal("Gift", (await api.ImportAsync("https://shop.example/item?variant=red")).Name);
        else if (status == 401) await Assert.ThrowsAsync<SignInRequiredException>(() => api.ImportAsync("https://shop.example/item?variant=red"));
        else Assert.DoesNotContain("private-error", (await Assert.ThrowsAsync<ApiRequestException>(() => api.ImportAsync("https://shop.example/item?variant=red"))).Message);
        Assert.Equal("https://shop.example/item?variant=red", handler.Url);
    }
    private sealed class Tokens : IApiAccessTokenProvider { public Task<string> GetAsync(CancellationToken ct = default) => Task.FromResult("test-token"); }
    private sealed class Handler(int status) : HttpMessageHandler
    {
        public string? Url;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("/api/item-metadata", request.RequestUri!.PathAndQuery);
            Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            Url = (await request.Content!.ReadFromJsonAsync<ProductLinkRequest>(ct))!.Url;
            return new((HttpStatusCode)status) { Content = status == 200 ? JsonContent.Create(new ProductLinkResult("found", "Gift")) : new StringContent("private-error") };
        }
    }
}
