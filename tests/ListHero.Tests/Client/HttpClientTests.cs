using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ListHero.Client;
using ListHero.Client.Api;
using ListHero.Client.Abstractions;
using ListHero.Client.Abstractions.Api;
using ListHero.Client.Services;
using ListHero.Contracts.Lists;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.Client;

public sealed class HttpClientTests
{
    private static readonly Guid ListId = Guid.NewGuid(), ItemId = Guid.NewGuid(), MarkId = Guid.NewGuid();
    private static readonly OwnerWishListResponse Owner = new(ListId, "Wishes", "", false, [], "AQAAAAAAAAA=");

    [Fact]
    public async Task Every_owner_HTTP_operation_sends_the_correct_route_payload_and_current_token()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://example.test/root/") };
        var token = new CountingTokens();
        var api = new WishListApiClient(http, token);
        handler.Response = JsonContent.Create(Owner);
        await api.UpdateAsync(ListId, new() { Name = "Changed", IsPublic = true, RowVersion = Owner.RowVersion });
        AssertRequest(handler, HttpMethod.Put, $"api/lists/{ListId}", "token-1");
        Assert.True(handler.Body!.Value.GetProperty("isPublic").GetBoolean());
        Assert.Equal(Owner.RowVersion, handler.Body.Value.GetProperty("rowVersion").GetString());
        handler.Response = JsonContent.Create(Owner);
        await api.UpdateItemAsync(ListId, ItemId, new() { Name = "Shoes", DesiredQuantity = 3, ImageUrl = "https://example.test/shoes.png", RowVersion = Owner.RowVersion });
        AssertRequest(handler, HttpMethod.Put, $"api/lists/{ListId}/items/{ItemId}", "token-2");
        Assert.Equal(3, handler.Body!.Value.GetProperty("desiredQuantity").GetInt32());
        Assert.Equal("https://example.test/shoes.png", handler.Body.Value.GetProperty("imageUrl").GetString());
        handler.Response = JsonContent.Create(new ItemEditWarningResponse(true));
        Assert.True((await api.EditWarningAsync(ListId, ItemId)).MayHavePurchaseMarks);
        AssertRequest(handler, HttpMethod.Get, $"api/lists/{ListId}/items/{ItemId}/edit-warning", "token-3");
        Assert.Null(handler.Body);
        handler.Response = JsonContent.Create(Owner);
        await api.ArchiveItemAsync(ListId, ItemId, new() { RowVersion = Owner.RowVersion });
        AssertRequest(handler, HttpMethod.Post, $"api/lists/{ListId}/items/{ItemId}/archive", "token-4");
        Assert.Equal(Owner.RowVersion, handler.Body!.Value.GetProperty("rowVersion").GetString());
        handler.Status = HttpStatusCode.NoContent;
        await api.ArchiveAsync(ListId, new() { RowVersion = Owner.RowVersion });
        AssertRequest(handler, HttpMethod.Post, $"api/lists/{ListId}/archive", "token-5");
        Assert.Equal(5, token.Calls);
    }

    [Fact]
    public async Task Collaboration_HTTP_operations_keep_capabilities_in_headers_and_skip_tokens_for_guests()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://example.test/root/") };
        var tokens = new CountingTokens();
        var api = new ListCollaborationApiClient(http, tokens);
        var link = new ShareLinkResponse(MarkId, "secret-share", DateTimeOffset.UtcNow, null, null, true);
        handler.Response = JsonContent.Create(new[] { link });
        Assert.Single(await api.GetLinksAsync(ListId));
        AssertRequest(handler, HttpMethod.Get, $"api/lists/{ListId}/share-links/", "token-1");
        handler.Response = JsonContent.Create(link);
        var expiration = DateTimeOffset.UtcNow.AddDays(1);
        await api.CreateLinkAsync(ListId, new() { ExpiresAt = expiration });
        AssertRequest(handler, HttpMethod.Post, $"api/lists/{ListId}/share-links/", "token-2");
        Assert.Equal(expiration, handler.Body!.Value.GetProperty("expiresAt").GetDateTimeOffset());
        handler.Status = HttpStatusCode.NoContent;
        await api.RevokeLinkAsync(ListId, MarkId);
        AssertRequest(handler, HttpMethod.Post, $"api/lists/{ListId}/share-links/{MarkId}/revoke", "token-3");
        handler.Status = HttpStatusCode.OK;
        handler.Response = JsonContent.Create(new ListViewResponse(null, new(ListId, "Wishes", "", false, true, [], [])));
        var credentials = new ViewerCredentials(false, "secret-share", "secret-guest");
        var workflow = new ListViewerClient(api, new MemoryGuests());
        await workflow.LoadAsync(ListId, false, credentials.ShareToken);
        AssertRequest(handler, HttpMethod.Get, $"api/lists/{ListId}/view", null);
        AssertCapabilities(handler);
        handler.Response = JsonContent.Create(new GuestCredentialResponse("secret-guest"));
        Assert.Equal("secret-guest", (await api.IssueGuestAsync(ListId, credentials)).Credential);
        AssertRequest(handler, HttpMethod.Post, $"api/lists/{ListId}/guest-credential", null);
        AssertCapabilities(handler);
        var key = Guid.NewGuid();
        handler.Response = JsonContent.Create(new OwnPurchaseMarkResponse(MarkId, ItemId, 3, DateTimeOffset.UtcNow, null, Owner.RowVersion));
        var mark = await api.MarkAsync(ListId, ItemId, new() { Quantity = 3, IdempotencyKey = key }, credentials);
        Assert.Equal(MarkId, mark.Id);
        AssertRequest(handler, HttpMethod.Post, $"api/lists/{ListId}/items/{ItemId}/marks", null);
        AssertCapabilities(handler);
        Assert.Equal(key, handler.Body!.Value.GetProperty("idempotencyKey").GetGuid());
        Assert.Equal(3, handler.Body.Value.GetProperty("quantity").GetInt32());
        handler.Status = HttpStatusCode.NoContent;
        await workflow.UndoAsync(ListId, mark, true, credentials.ShareToken);
        AssertRequest(handler, HttpMethod.Post, $"api/lists/{ListId}/items/{ItemId}/marks/{MarkId}/undo", "token-4");
        AssertCapabilities(handler);
        Assert.Equal(mark.RowVersion, handler.Body!.Value.GetProperty("rowVersion").GetString());
        Assert.Equal(4, tokens.Calls);
    }

    [Theory]
    [InlineData(401, true)] [InlineData(403, true)] [InlineData(404, true)]
    [InlineData(400, true)] [InlineData(409, true)] [InlineData(500, true)]
    [InlineData(401, false)] [InlineData(403, false)] [InlineData(404, false)]
    [InlineData(400, false)] [InlineData(409, false)] [InlineData(500, false)]
    public async Task HTTP_failures_become_safe_actionable_errors(int status, bool owner)
    {
        var handler = new RecordingHandler { Status = (HttpStatusCode)status, Response = new StringContent("secret-server-stack") };
        using var http = new HttpClient(handler) { BaseAddress = new("https://example.test/") };
        Func<Task> action = owner
            ? () => new WishListApiClient(http, new CountingTokens()).GetOwnerViewAsync(ListId)
            : () => new ListCollaborationApiClient(http, new CountingTokens()).ViewAsync(ListId, new(false));
        var exception = await Record.ExceptionAsync(action);
        Assert.NotNull(exception);
        if (status == 401) Assert.IsType<SignInRequiredException>(exception);
        else if (status == 409 && owner) Assert.IsType<ApiConflictException>(exception);
        else Assert.IsType<ApiRequestException>(exception);
        Assert.DoesNotContain("secret-server-stack", exception.Message);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Empty_success_response_is_reported_instead_of_returning_a_null_model(bool owner)
    {
        using var http = new HttpClient(new RecordingHandler { Response = JsonContent.Create<object?>(null) }) { BaseAddress = new("https://example.test/") };
        Func<Task> action = owner ? () => new WishListApiClient(http, new CountingTokens()).GetOwnerViewAsync(ListId)
            : () => new ListCollaborationApiClient(http, new CountingTokens()).ViewAsync(ListId, new(false));
        Assert.Contains("empty response", (await Assert.ThrowsAsync<ApiRequestException>(action)).Message);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Caller_cancellation_reaches_transport_and_is_not_swallowed(bool owner)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new BlockingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://example.test/") };
        var operation = owner ? new WishListApiClient(http, new CountingTokens()).GetOwnerViewAsync(ListId, cancellation.Token)
            : (Task)new ListCollaborationApiClient(http, new CountingTokens()).ViewAsync(ListId, new(false), cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Theory]
    [InlineData("relative/")] [InlineData("ftp://example.test/")] [InlineData("https://example.test/api")]
    public void Invalid_API_base_addresses_are_rejected(string address)
        => Assert.Throws<ArgumentException>(() => new ServiceCollection().AddListHeroApiClient(new(address, UriKind.RelativeOrAbsolute)));

    [Fact]
    public void Client_registration_resolves_real_adapters_and_workflows()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IApiAccessTokenProvider>(new CountingTokens());
        services.AddSingleton<IGuestCredentialStore>(new MemoryGuests());
        services.AddListHeroClient().AddListHeroApiClient(new("https://example.test/api-root/"));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        Assert.IsType<WishListApiClient>(scope.ServiceProvider.GetRequiredService<IWishListApi>());
        Assert.IsType<ListCollaborationApiClient>(scope.ServiceProvider.GetRequiredService<IListCollaborationApi>());
        Assert.IsType<AppStatusApiClient>(scope.ServiceProvider.GetRequiredService<IAppStatusApi>());
        Assert.IsType<ListViewerClient>(scope.ServiceProvider.GetRequiredService<IListViewerClient>());
        Assert.IsType<AppStatusService>(scope.ServiceProvider.GetRequiredService<IAppStatusService>());
    }

    [Theory]
    [InlineData("relative/")] [InlineData("ftp://example.test/")]
    [InlineData("https://example.test/?secret=1")] [InlineData("https://example.test/#secret")]
    public void Share_builder_rejects_unsafe_host_addresses(string address)
        => Assert.Throws<ArgumentException>(() => new ListShareUrlBuilder(new(address, UriKind.RelativeOrAbsolute)));

    private static void AssertRequest(RecordingHandler handler, HttpMethod method, string path, string? token)
    {
        Assert.Equal(method, handler.Method);
        Assert.Equal("https://example.test/root/" + path, handler.Address!.AbsoluteUri);
        Assert.Equal(token is null ? null : "Bearer " + token, handler.Authorization);
    }
    private static void AssertCapabilities(RecordingHandler handler)
    {
        Assert.Equal("secret-share", handler.Share);
        Assert.Equal("secret-guest", handler.Guest);
        Assert.DoesNotContain("secret", handler.Address!.AbsoluteUri);
    }
    private sealed class CountingTokens : IApiAccessTokenProvider
    {
        public int Calls { get; private set; }
        public Task<string> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult("token-" + ++Calls);
    }
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public HttpContent? Response { get; set; }
        public HttpMethod? Method { get; private set; }
        public Uri? Address { get; private set; }
        public JsonElement? Body { get; private set; }
        public string? Authorization { get; private set; }
        public string? Share { get; private set; }
        public string? Guest { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method; Address = request.RequestUri; Authorization = request.Headers.Authorization?.ToString();
            Share = request.Headers.TryGetValues(CapabilityHeaders.Share, out var share) ? share.Single() : null;
            Guest = request.Headers.TryGetValues(CapabilityHeaders.Guest, out var guest) ? guest.Single() : null;
            Body = request.Content is null ? null : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement.Clone();
            return new(Status) { Content = Status == HttpStatusCode.NoContent ? null : Response };
        }
    }
    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new(HttpStatusCode.OK); }
    }
    private sealed class MemoryGuests : IGuestCredentialStore
    {
        public ValueTask<string?> ReadAsync() => ValueTask.FromResult<string?>("secret-guest");
        public ValueTask WriteAsync(string credential) => ValueTask.CompletedTask;
        public ValueTask ClearAsync() => ValueTask.CompletedTask;
    }
}
