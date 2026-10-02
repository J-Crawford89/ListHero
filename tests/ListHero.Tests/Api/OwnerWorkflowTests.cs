using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ListHero.Application.Identity;
using ListHero.Application.Lists;
using ListHero.Application.Collaboration;
using ListHero.Client.Api;
using ListHero.Client.Abstractions.Api;
using ListHero.Contracts.Lists;
using ListHero.Domain.Entities;
using ListHero.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ListHero.Tests.Api;

public sealed partial class OwnerWorkflowTests
{
    internal static WebApplicationFactory<ListHero.Api.ApiAssemblyMarker> CreateBrowserApiFactory()
        => new OwnerApiFactory(IsolatedSqlConnection("_Browser"));
    [Fact]
    public async Task Owner_can_create_reopen_and_add_items_through_the_client_and_API()
    {
        await using var factory = new OwnerApiFactory();
        await ExerciseWorkflowAsync(factory);
    }

    [Fact]
    public async Task Anonymous_requests_and_tokens_without_the_API_scope_are_rejected()
    {
        await using var factory = new OwnerApiFactory();
        using var http = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/lists/mine")).StatusCode);
        http.DefaultRequestHeaders.Add("X-Test-User", Guid.NewGuid().ToString());
        http.DefaultRequestHeaders.Add("X-Test-Scope", "unrelated_scope");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/lists/mine")).StatusCode);
        http.DefaultRequestHeaders.Remove("X-Test-Scope");
        http.DefaultRequestHeaders.Remove("X-Test-User");
        http.DefaultRequestHeaders.Add("X-Test-User", "invalid-object-id");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/lists/mine")).StatusCode);
    }

    [Fact]
    public async Task Another_user_including_an_admin_cannot_read_or_append_to_an_owners_list()
    {
        await using var factory = new OwnerApiFactory();
        using var owner = SignedInClient(factory);
        var created = await (await owner.PostAsJsonAsync("/api/lists/", new CreateWishListRequest { Name = "Private" }))
            .Content.ReadFromJsonAsync<OwnerWishListResponse>();
        using var other = SignedInClient(factory);
        other.DefaultRequestHeaders.Add("X-Test-Role", "Admin");
        Assert.Empty((await other.GetFromJsonAsync<WishListSummaryResponse[]>("/api/lists/mine"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/lists/{created!.Id}/owner")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/lists/{created.Id}/items",
            new CreateWishListItemRequest { Name = "Intrusion" })).StatusCode);
    }

    [Fact]
    public async Task Invalid_drafts_are_rejected_before_they_are_saved()
    {
        await using var factory = new OwnerApiFactory();
        using var owner = SignedInClient(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/lists/",
            new CreateWishListRequest { Name = "   " })).StatusCode);
        var list = await (await owner.PostAsJsonAsync("/api/lists/", new CreateWishListRequest { Name = "Valid" }))
            .Content.ReadFromJsonAsync<OwnerWishListResponse>();
        foreach (var invalid in new[]
        {
            new CreateWishListItemRequest { Name = "Zero", DesiredQuantity = 0 },
            new CreateWishListItemRequest { Name = "Price", ApproximateUnitPrice = -1 },
            new CreateWishListItemRequest { Name = "Link", Url = "javascript:alert(1)" },
            new CreateWishListItemRequest { Name = new string('a', 201) }
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/lists/{list!.Id}/items", invalid)).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<OwnerWishListResponse>($"/api/lists/{list!.Id}/owner"))!.Items);
    }

    [Fact]
    public async Task Archived_lists_items_and_accounts_do_not_reappear_in_owner_workflows()
    {
        await using var factory = new OwnerApiFactory();
        using var owner = SignedInClient(factory);
        var list = await (await owner.PostAsJsonAsync("/api/lists/", new CreateWishListRequest { Name = "Archive" }))
            .Content.ReadFromJsonAsync<OwnerWishListResponse>();
        await owner.PostAsJsonAsync($"/api/lists/{list!.Id}/items", new CreateWishListItemRequest { Name = "Old" });
        var store = factory.Services.GetRequiredService<MemoryListStore>();
        store.Items.Single().Value.Archive(DateTimeOffset.UtcNow);
        Assert.Empty((await owner.GetFromJsonAsync<OwnerWishListResponse>($"/api/lists/{list.Id}/owner"))!.Items);
        store.Lists[list.Id].Archive(DateTimeOffset.UtcNow);
        Assert.Empty((await owner.GetFromJsonAsync<WishListSummaryResponse[]>("/api/lists/mine"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/lists/{list.Id}/owner")).StatusCode);
        factory.Services.GetRequiredService<MemoryUserStore>().Users.Single().Value.Archive(DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/lists/mine")).StatusCode);
    }

    [SqlServerFact]
    public async Task Owner_workflow_persists_in_real_SQL_Server_and_provisions_one_user()
    {
        var connection = Environment.GetEnvironmentVariable("LISTHERO_TEST_SQL_CONNECTION")!;
        var databaseName = new SqlConnectionStringBuilder(connection).InitialCatalog;
        Assert.StartsWith("ListHero_Integration_", databaseName);
        await using var factory = new OwnerApiFactory(connection);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.MigrateAsync();
        await ExerciseWorkflowAsync(factory);
        using var verification = factory.Services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<ListHeroDbContext>();
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(1, await db.WishLists.CountAsync());
        Assert.Equal(2, await db.WishListItems.CountAsync());
        Assert.All(await db.WishListItems.ToArrayAsync(), item => Assert.NotEmpty(item.RowVersion));
    }

    private static async Task ExerciseWorkflowAsync(OwnerApiFactory factory)
    {
        using var http = SignedInClient(factory);
        var api = new WishListApiClient(http, new TestTokenProvider());
        Assert.Empty(await api.GetMineAsync());
        var created = await api.CreateAsync(new() { Name = "Birthday", Description = "A few favorite things" });
        Assert.False(created.IsPublic);
        await api.AddItemAsync(created.Id, new() { Name = "Mug", Description = "Green", DesiredQuantity = 2,
            ApproximateUnitPrice = 24.50, DisplayOrder = 20, Priority = 3,
            Url = "https://example.com/mug", ImageUrl = "https://example.com/mug.jpg" });
        await api.AddItemAsync(created.Id, new() { Name = "Book", DisplayOrder = 10 });
        var reopened = await api.GetOwnerViewAsync(created.Id);
        Assert.Equal(new[] { "Book", "Mug" }, reopened.Items.Select(item => item.Name));
        Assert.Null(reopened.Items[0].ApproximateUnitPrice);
        Assert.Equal(1, reopened.Items[0].DesiredQuantity);
        Assert.Equal(24.50, reopened.Items[1].ApproximateUnitPrice);
        Assert.Equal(2, reopened.Items[1].DesiredQuantity);
        Assert.Equal(3, reopened.Items[1].Priority);
        Assert.Equal(created.Id, Assert.Single(await api.GetMineAsync()).Id);
        var response = await http.GetAsync($"/api/lists/{created.Id}/owner");
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("purchase", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fulfilled", json, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient SignedInClient(WebApplicationFactory<ListHero.Api.ApiAssemblyMarker> factory)
    {
        var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        http.DefaultRequestHeaders.Add("X-Test-User", Guid.NewGuid().ToString());
        return http;
    }

    private sealed class TestTokenProvider : IApiAccessTokenProvider
    {
        public Task<string> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult("test-only");
    }

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LISTHERO_TEST_SQL_CONNECTION")))
                Skip = "Set LISTHERO_TEST_SQL_CONNECTION to a fresh ListHero_Integration_* SQL Server database.";
        }
    }

    private sealed class OwnerApiFactory(string? sqlConnection = null, TimeProvider? clock = null,
        Action<IServiceCollection>? overrides = null) : WebApplicationFactory<ListHero.Api.ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development").UseSetting("Authentication:Enabled", "false");
            builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
            builder.ConfigureTestServices(services =>
            {
                if (clock is not null) { services.RemoveAll<TimeProvider>(); services.AddSingleton(clock); }
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "TestOnly";
                    options.DefaultChallengeScheme = "TestOnly";
                    options.DefaultForbidScheme = "TestOnly";
                }).AddScheme<AuthenticationSchemeOptions, TestIdentityHandler>("TestOnly", _ => { });
                if (sqlConnection is not null)
                {
                    services.RemoveAll<ListHeroDbContext>();
                    services.RemoveAll<DbContextOptions<ListHeroDbContext>>();
                    services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<ListHeroDbContext>>();
                    services.AddDbContext<ListHeroDbContext>(options => options.UseSqlServer(sqlConnection));
                }
                else
                {
                    services.RemoveAll<IUserStore>();
                    services.RemoveAll<IWishListStore>();
                    services.AddSingleton<MemoryUserStore>();
                    services.AddSingleton<IUserStore>(provider => provider.GetRequiredService<MemoryUserStore>());
                    services.AddSingleton<MemoryListStore>();
                    services.AddSingleton<IWishListStore>(provider => provider.GetRequiredService<MemoryListStore>());
                    services.RemoveAll<IListViewerStore>();
                    services.RemoveAll<IShareLinkStore>();
                    services.RemoveAll<IPurchaseStore>();
                    services.AddSingleton<MemoryCollaborationStore>();
                    services.AddSingleton<IListViewerStore>(provider => provider.GetRequiredService<MemoryCollaborationStore>());
                    services.AddSingleton<IShareLinkStore>(provider => provider.GetRequiredService<MemoryCollaborationStore>());
                    services.AddSingleton<IPurchaseStore>(provider => provider.GetRequiredService<MemoryCollaborationStore>());
                }
                overrides?.Invoke(services);
            });
        }
    }

    // This authentication handler is confined to the test assembly and TestServer.
    private sealed class TestIdentityHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers["X-Test-User"].ToString();
            var header = Request.Headers.Authorization.ToString();
            if (subject.Length == 0 && header.StartsWith("Bearer browser-", StringComparison.Ordinal))
                subject = header["Bearer browser-".Length..];
            if (subject.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            List<Claim> claims = [new("oid", subject), new("iss", "https://test.ciamlogin.com/test/v2.0"),
                new("name", "Test User"), new("scp", Request.Headers["X-Test-Scope"].FirstOrDefault() ?? "access_as_user")];
            if (Request.Headers["X-Test-Role"].FirstOrDefault() is string role) claims.Add(new("roles", role));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, "name", "roles"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class MemoryUserStore : IUserStore
    {
        public ConcurrentDictionary<(string, string), User> Users { get; } = new();
        public Task<User> GetOrCreateAsync(ExternalUserIdentity identity, DateTimeOffset now, CancellationToken cancellationToken = default)
            => Task.FromResult(Users.GetOrAdd((identity.Issuer, identity.ObjectId), _ => new(identity.Issuer, identity.ObjectId, identity.DisplayName, now)));
    }

    private sealed class MemoryListStore : IWishListStore
    {
        public ConcurrentDictionary<Guid, WishList> Lists { get; } = new();
        public ConcurrentDictionary<Guid, WishListItem> Items { get; } = new();
        public Func<Guid, bool> PurchaseExists { get; set; } = _ => false;
        public Task<IReadOnlyList<WishList>> GetByOwnerAsync(Guid ownerId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WishList>>(Lists.Values.Where(list => list.OwnerId == ownerId && !list.IsArchived).ToArray());
        public Task<WishList?> GetOwnedAsync(Guid listId, Guid ownerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Lists.TryGetValue(listId, out var list) && list.OwnerId == ownerId && !list.IsArchived ? list : null);
        public Task<IReadOnlyList<WishListItem>> GetItemsAsync(Guid listId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WishListItem>>(Items.Values.Where(item => item.WishListId == listId && !item.IsArchived)
                .OrderBy(item => item.DisplayOrder).ThenBy(item => item.Id).ToArray());
        public Task AddListAsync(WishList list, CancellationToken cancellationToken = default) { StampVersion(list); Lists[list.Id] = list; return Task.CompletedTask; }
        public Task AddItemAsync(WishListItem item, CancellationToken cancellationToken = default) { StampVersion(item); Items[item.Id] = item; return Task.CompletedTask; }
        public Task<WishListItem?> GetItemAsync(Guid listId, Guid itemId, CancellationToken cancellationToken = default)
            => Task.FromResult(Items.TryGetValue(itemId, out var item) && item.WishListId == listId && !item.IsArchived ? item : null);
        public Task SaveListAsync(WishList list, byte[] expectedVersion, CancellationToken cancellationToken = default)
        { StampVersion(list); Lists[list.Id] = list; return Task.CompletedTask; }
        public Task SaveItemAsync(WishListItem item, byte[] expectedVersion, CancellationToken cancellationToken = default)
        { StampVersion(item); Items[item.Id] = item; return Task.CompletedTask; }
        public Task<bool> HasPurchaseMarksAsync(Guid itemId, CancellationToken cancellationToken = default) => Task.FromResult(PurchaseExists(itemId));
    }
    private static long versionCounter;
    private static void StampVersion(object entity) => entity.GetType().GetProperty("RowVersion")!
        .SetValue(entity, BitConverter.GetBytes(Interlocked.Increment(ref versionCounter)));
}
