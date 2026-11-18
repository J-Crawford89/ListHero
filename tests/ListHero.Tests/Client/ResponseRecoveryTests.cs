using System.Net;
using System.Text;
using ListHero.Client.Abstractions.Api;
using ListHero.Client.Api;
using ListHero.Contracts.Lists;

namespace ListHero.Tests.Client;

public sealed class ResponseRecoveryTests
{
    private static readonly string[] Clients = ["owner", "sharing", "discovery", "profile", "dashboard", "saved", "status"];
    public static IEnumerable<object[]> InvalidResponses()
    {
        foreach (var client in Clients)
        foreach (var body in new[] { "", "null", "{}", "<html>private-server-diagnostics</html>", "{\"broken\":private-server-diagnostics}" })
            yield return [client, body];
    }

    [Theory, MemberData(nameof(InvalidResponses))]
    public async Task Invalid_success_payloads_offer_safe_recovery_in_every_HTTP_adapter(string client, string body)
    {
        using var handler = new Reply(body);
        using var http = Http(handler);
        var error = await Assert.ThrowsAsync<ApiRequestException>(() => ReadAsync(client, http));
        Assert.Contains("response", error.Message);
        Assert.Contains("try again", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-server-diagnostics", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("owner")] [InlineData("sharing")] [InlineData("discovery")] [InlineData("profile")]
    [InlineData("dashboard")] [InlineData("saved")] [InlineData("status")]
    public async Task Unexpected_no_content_is_an_error_when_a_response_model_is_required(string client)
    {
        using var http = Http(new Reply("") { Status = HttpStatusCode.NoContent });
        Assert.Contains("empty response", (await Assert.ThrowsAsync<ApiRequestException>(() => ReadAsync(client, http))).Message);
    }

    [Theory]
    [InlineData("")] [InlineData("null")] [InlineData("[]")] [InlineData("{\"code\":42}")]
    [InlineData("{\"code\":null}")] [InlineData("<html>private-server-diagnostics</html>")]
    public async Task Unreadable_optional_error_details_preserve_conflict_and_validation_recovery(string body)
    {
        using var handler = new Reply(body) { Status = HttpStatusCode.Conflict };
        using var http = Http(handler);
        await Assert.ThrowsAsync<ApiConflictException>(() => new PublicProfileApiClient(http, new Tokens()).GetAsync());
        handler.Status = HttpStatusCode.BadRequest;
        var error = await Assert.ThrowsAsync<ApiRequestException>(() => new WishListApiClient(http, new Tokens())
            .UpdateAsync(Guid.NewGuid(), new()));
        Assert.Contains("Check the details", error.Message);
        Assert.DoesNotContain("private-server-diagnostics", error.Message);
    }

    [Fact]
    public async Task Missing_or_null_required_fields_are_rejected_but_nullable_owner_viewer_shapes_still_work()
    {
        using var handler = new Reply("{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"Wishes\",\"description\":\"\",\"isPublic\":false,\"items\":null}");
        using var http = Http(handler);
        await Assert.ThrowsAsync<ApiRequestException>(() => new WishListApiClient(http, new Tokens()).GetOwnerViewAsync(Guid.NewGuid()));
        var viewer = new ListCollaborationApiClient(http, new Tokens());
        handler.Body = "{\"viewer\":{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"Wishes\",\"description\":\"\",\"isPublic\":true,\"canMarkPurchases\":false,\"items\":[]}}";
        var result = await viewer.ViewAsync(Guid.NewGuid(), new(false));
        Assert.Null(result.Owner);
        Assert.NotNull(result.Viewer);
        Assert.Null(result.Viewer.MyMarks);
        handler.Body = "{\"owner\":{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"Wishes\",\"description\":\"\",\"isPublic\":false,\"items\":[]}}";
        result = await viewer.ViewAsync(Guid.NewGuid(), new(true));
        Assert.NotNull(result.Owner);
        Assert.Null(result.Viewer);
    }

    [Fact]
    public async Task Unreadable_write_responses_never_replay_creates_or_purchase_marks()
    {
        using var handler = new Reply("<html>unreadable response after a write</html>");
        using var http = Http(handler);
        var error = await Assert.ThrowsAsync<ApiRequestException>(() => new WishListApiClient(http, new Tokens()).CreateAsync(new() { Name = "Birthday" }));
        Assert.Contains("may have succeeded", error.Message);
        Assert.Contains("Check the result", error.Message);
        Assert.Equal(1, handler.Calls);
        var mark = new CreatePurchaseMarkRequest { Quantity = 2, IdempotencyKey = Guid.NewGuid() };
        var key = mark.IdempotencyKey;
        error = await Assert.ThrowsAsync<ApiRequestException>(() => new ListCollaborationApiClient(http, new Tokens())
            .MarkAsync(Guid.NewGuid(), Guid.NewGuid(), mark, new(false, "share-key", "guest-key")));
        Assert.Contains("may have succeeded", error.Message);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(key, mark.IdempotencyKey);
        Assert.Equal(2, mark.Quantity);
    }

    [Theory]
    [InlineData("unknown-encoding")] [InlineData("utf-7")]
    public async Task Unsupported_response_encodings_do_not_escape_status_or_payload_recovery(string charset)
    {
        using var handler = new Reply("{}") { Charset = charset };
        using var http = Http(handler);
        await Assert.ThrowsAsync<ApiRequestException>(() => new DashboardApiClient(http, new Tokens()).GetAsync());
        handler.Status = HttpStatusCode.Conflict;
        await Assert.ThrowsAsync<ApiConflictException>(() => new PublicProfileApiClient(http, new Tokens()).GetAsync());
    }

    private static Task ReadAsync(string client, HttpClient http) => client switch
    {
        "owner" => new WishListApiClient(http, new Tokens()).GetOwnerViewAsync(Guid.NewGuid()),
        "sharing" => new ListCollaborationApiClient(http, new Tokens()).GetLinksAsync(Guid.NewGuid()),
        "discovery" => new DiscoveryApiClient(http).GetListsAsync(new()),
        "profile" => new PublicProfileApiClient(http, new Tokens()).GetAsync(),
        "dashboard" => new DashboardApiClient(http, new Tokens()).GetAsync(),
        "saved" => new DashboardApiClient(http, new Tokens()).SavedAsync(),
        "status" => new AppStatusApiClient(http).GetAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(client))
    };
    private static HttpClient Http(HttpMessageHandler handler) => new(handler) { BaseAddress = new("https://api.test/") };
    private sealed class Tokens : IApiAccessTokenProvider
    {
        public Task<string> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult("test-token");
    }
    private sealed class Reply(string body) : HttpMessageHandler
    {
        public string Body { get; set; } = body;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Charset { get; set; } = "utf-8";
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var content = new StringContent(Body, Encoding.UTF8, "application/problem+json");
            content.Headers.ContentType!.CharSet = Charset;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = content });
        }
    }
}
