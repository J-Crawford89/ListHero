using ListHero.Client.Abstractions;
using ListHero.Client.Abstractions.Api;
using ListHero.Client.Api;
using ListHero.Client.Services;
using ListHero.Contracts.Lists;

namespace ListHero.Tests.Client;

public sealed class ViewerClientTests
{
    [Fact]
    public async Task Guest_ownership_is_persisted_before_a_mark_is_sent()
    {
        var storage = new GuestStorage();
        var api = new CollaborationFake(storage);
        var workflow = new ListViewerClient(api, storage);
        await workflow.MarkAsync(Guid.NewGuid(), Guid.NewGuid(), false, "share-token", new() { IdempotencyKey = Guid.NewGuid() });
        Assert.Equal("guest-token", storage.Value);
        Assert.Equal("guest-token", api.SentCredentials!.GuestCredential);
        Assert.Equal("share-token", api.SentCredentials.ShareToken);
        Assert.True(api.WasPersistedWhenSent);
    }

    [Fact]
    public async Task Unavailable_browser_storage_prevents_an_unrecoverable_anonymous_mark()
    {
        var storage = new GuestStorage { RejectWrites = true };
        var api = new CollaborationFake(storage);
        var workflow = new ListViewerClient(api, storage);
        await Assert.ThrowsAsync<ApiRequestException>(() => workflow.MarkAsync(Guid.NewGuid(), Guid.NewGuid(), false, "share-token", new()));
        Assert.Null(api.SentCredentials);
    }

    [Fact]
    public async Task Token_failure_for_a_signed_in_viewer_never_downgrades_to_anonymous()
    {
        var handler = new NeverSendHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://localhost") };
        var api = new ListCollaborationApiClient(http, new ExpiredTokenProvider());
        await Assert.ThrowsAsync<SignInRequiredException>(() => api.ViewAsync(Guid.NewGuid(), new(true, "share-token")));
        Assert.False(handler.Sent);
    }
    private sealed class ExpiredTokenProvider : IApiAccessTokenProvider
    {
        public Task<string> GetAsync(CancellationToken cancellationToken = default) => throw new SignInRequiredException();
    }
    private sealed class NeverSendHandler : HttpMessageHandler
    {
        public bool Sent { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Sent = true; throw new InvalidOperationException("The request must not be sent."); }
    }
    private sealed class GuestStorage : IGuestCredentialStore
    {
        public string? Value { get; private set; }
        public bool RejectWrites { get; set; }
        public ValueTask<string?> ReadAsync() => ValueTask.FromResult(Value);
        public ValueTask WriteAsync(string credential)
        { if (RejectWrites) throw new ApiRequestException("Storage unavailable."); Value = credential; return ValueTask.CompletedTask; }
        public ValueTask ClearAsync() { Value = null; return ValueTask.CompletedTask; }
    }
    private sealed class CollaborationFake(GuestStorage storage) : IListCollaborationApi
    {
        public ViewerCredentials? SentCredentials { get; private set; }
        public bool WasPersistedWhenSent { get; private set; }
        public Task<GuestCredentialResponse> IssueGuestAsync(Guid listId, ViewerCredentials credentials, CancellationToken ct = default) => Task.FromResult(new GuestCredentialResponse("guest-token"));
        public Task<OwnPurchaseMarkResponse> MarkAsync(Guid listId, Guid itemId, CreatePurchaseMarkRequest request, ViewerCredentials credentials, CancellationToken ct = default)
        {
            WasPersistedWhenSent = storage.Value == "guest-token";
            SentCredentials = credentials;
            return Task.FromResult(new OwnPurchaseMarkResponse(Guid.NewGuid(), itemId, request.Quantity, DateTimeOffset.UtcNow, null, "AQAAAAAAAAA="));
        }
        public Task<IReadOnlyList<ShareLinkResponse>> GetLinksAsync(Guid listId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ShareLinkResponse> CreateLinkAsync(Guid listId, CreateShareLinkRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RevokeLinkAsync(Guid listId, Guid linkId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ListViewResponse> ViewAsync(Guid listId, ViewerCredentials credentials, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UndoAsync(Guid listId, Guid itemId, Guid markId, ArchiveRequest request, ViewerCredentials credentials, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
