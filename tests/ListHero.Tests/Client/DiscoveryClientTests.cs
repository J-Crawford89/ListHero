using System.Net;
using System.Text;
using System.Text.Json;
using ListHero.Client.Api;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Discovery;
using ListHero.Contracts.Lists;

namespace ListHero.Tests.Client;

public sealed class DiscoveryClientTests
{
    [Fact]
    public async Task Anonymous_discovery_encodes_queries_and_reads_each_response_shape()
    {
        using var handler = new Reply(); using var http = new HttpClient(handler) { BaseAddress = new("https://api.test/") };
        var api = new DiscoveryApiClient(http);
        handler.Body = JsonSerializer.Serialize(new DiscoveryPage<PublicListSummary>([], 2, 3, true));
        Assert.True((await api.GetListsAsync(new("100%_ &", 2, 3))).HasMore);
        Assert.Contains("q=100%25_%20%26&page=2&pageSize=3", handler.Path);
        Assert.Null(handler.Authorization);
        handler.Body = JsonSerializer.Serialize(new DiscoveryPage<PublicPersonSummary>([new("Jane", "jane")], 1, 20, false));
        Assert.Equal("Jane", Assert.Single((await api.GetPeopleAsync(new())).Items).DisplayName);
        handler.Body = JsonSerializer.Serialize(new PublicProfileResponse(new("Jane", "jane"), new([], 1, 20, false)));
        Assert.Equal("jane", (await api.GetProfileAsync("jane", 2)).Author.Handle);
        Assert.Equal("/api/discovery/people/jane?page=2", handler.Path);
    }

    [Theory]
    [InlineData(404, "unavailable")] [InlineData(400, "2–100")] [InlineData(500, "try again")]
    [InlineData(200, "empty response")]
    public async Task Discovery_returns_useful_errors(int status, string message)
    {
        using var handler = new Reply { Status = (HttpStatusCode)status, Body = "null" };
        using var http = new HttpClient(handler) { BaseAddress = new("https://api.test/") };
        var error = await Assert.ThrowsAsync<ApiRequestException>(() => new DiscoveryApiClient(http).GetListsAsync(new()));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task Profile_requests_acquire_a_fresh_token_and_send_only_the_explicit_profile_draft()
    {
        using var handler = new Reply { Body = JsonSerializer.Serialize(new OwnProfileResponse(null, null, "Private name", "AQAAAAAAAAA=")) };
        using var http = new HttpClient(handler) { BaseAddress = new("https://api.test/") };
        var tokens = new Tokens(); var api = new PublicProfileApiClient(http, tokens);
        var current = await api.GetAsync();
        Assert.Equal("Bearer token-1", handler.Authorization);
        var draft = new UpdatePublicProfileRequest { DisplayName = "Public Jane", Handle = "jane", RowVersion = current.RowVersion, ConfirmDiscovery = true };
        handler.Body = JsonSerializer.Serialize(new OwnProfileResponse("Public Jane", "jane", "Private name", "AgAAAAAAAAA="));
        Assert.Equal("jane", (await api.UpdateAsync(draft)).Handle);
        Assert.Equal(HttpMethod.Put, handler.Method); Assert.Equal("/api/profile/me", handler.Path);
        Assert.Equal("Bearer token-2", handler.Authorization);
        using var body = JsonDocument.Parse(handler.SentBody!);
        Assert.Equal("Public Jane", body.RootElement.GetProperty("displayName").GetString());
        Assert.True(body.RootElement.GetProperty("confirmDiscovery").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("userId", out _));
    }

    [Theory]
    [InlineData(401, "{}", "sign in")]
    [InlineData(409, "{\"code\":\"handle_unavailable\"}", "already taken")]
    [InlineData(409, "{}", "record changed")]
    [InlineData(400, "{}", "Check your public name")]
    [InlineData(403, "{}", "try again")]
    [InlineData(200, "null", "empty response")]
    public async Task Profile_errors_distinguish_handle_conflicts_and_expired_sign_in(int status, string body, string expected)
    {
        using var handler = new Reply { Status = (HttpStatusCode)status, Body = body };
        using var http = new HttpClient(handler) { BaseAddress = new("https://api.test/") };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new PublicProfileApiClient(http, new Tokens()).GetAsync());
        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("profile_required", "public profile")]
    [InlineData("other", "Check the details")]
    public async Task Publishing_without_profile_offers_the_profile_setup_path(string code, string expected)
    {
        using var handler = new Reply { Status = HttpStatusCode.BadRequest, Body = "{\"code\":\"" + code + "\"}", MediaType = "application/problem+json" };
        using var http = new HttpClient(handler) { BaseAddress = new("https://api.test/") };
        var error = await Assert.ThrowsAsync<ApiRequestException>(() => new WishListApiClient(http, new Tokens()).UpdateAsync(Guid.NewGuid(), new UpdateWishListRequest()));
        Assert.Contains(expected, error.Message);
    }

    private sealed class Tokens : IApiAccessTokenProvider
    {
        private int calls;
        public Task<string> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult("token-" + ++calls);
    }
    private sealed class Reply : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{}";
        public string MediaType { get; set; } = "application/json";
        public string? Path { get; private set; }
        public string? Authorization { get; private set; }
        public string? SentBody { get; private set; }
        public HttpMethod? Method { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.PathAndQuery; Authorization = request.Headers.Authorization?.ToString(); Method = request.Method;
            SentBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new(Status) { Content = new StringContent(Body, Encoding.UTF8, MediaType) };
        }
    }
}
