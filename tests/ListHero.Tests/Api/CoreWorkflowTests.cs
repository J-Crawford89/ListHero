using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using ListHero.Application.Collaboration;
using ListHero.Contracts.Lists;
using ListHero.Domain.Entities;
using ListHero.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.Api;

public sealed partial class OwnerWorkflowTests
{
    [Fact]
    public async Task Owner_edits_reject_stale_versions_and_archival_preserves_history()
    {
        await using var factory = new OwnerApiFactory();
        using var owner = SignedInClient(factory);
        using var other = SignedInClient(factory);
        var list = await CreateListAsync(owner);
        var update = new UpdateWishListRequest { Name = "Updated", Description = "New description", IsPublic = true, RowVersion = list.RowVersion };
        var updated = await JsonAsync<OwnerWishListResponse>(await owner.PutAsJsonAsync($"/api/lists/{list.Id}", update));
        Assert.True(updated.IsPublic);
        Assert.Equal("Updated", updated.Name);
        Assert.NotEqual(list.RowVersion, updated.RowVersion);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/lists/{list.Id}", update)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/lists/{list.Id}", update)).StatusCode);
        var item = Assert.Single(updated.Items);
        var edit = new UpdateWishListItemRequest { Name = "A different mug", DesiredQuantity = 2, RowVersion = item.RowVersion };
        var edited = await JsonAsync<OwnerWishListResponse>(await owner.PutAsJsonAsync($"/api/lists/{list.Id}/items/{item.Id}", edit));
        Assert.Equal("A different mug", Assert.Single(edited.Items).Name);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/lists/{list.Id}/items/{item.Id}", edit)).StatusCode);
        var withoutItem = await JsonAsync<OwnerWishListResponse>(await owner.PostAsJsonAsync($"/api/lists/{list.Id}/items/{item.Id}/archive",
            new ArchiveRequest { RowVersion = edited.Items[0].RowVersion }));
        Assert.Empty(withoutItem.Items);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/lists/{list.Id}/archive", new ArchiveRequest { RowVersion = withoutItem.RowVersion })).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<WishListSummaryResponse[]>("/api/lists/mine"))!);
        var records = factory.Services.GetRequiredService<MemoryListStore>();
        Assert.True(records.Lists[list.Id].IsArchived);
        Assert.True(records.Items[item.Id].IsArchived);
        Assert.Single(records.Items);
    }

    [Fact]
    public async Task Private_guest_workflow_is_idempotent_and_owner_responses_hide_marks()
    {
        await using var factory = new OwnerApiFactory();
        await ExerciseSharingAsync(factory);
    }

    [Fact]
    public async Task Public_viewing_does_not_grant_anonymous_purchase_permission()
    {
        await using var factory = new OwnerApiFactory();
        using var owner = SignedInClient(factory);
        using var buyer = SignedInClient(factory);
        using var outsider = SignedInClient(factory);
        using var anonymous = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var list = await CreateListAsync(owner);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/lists/{list.Id}/view")).StatusCode);
        list = await JsonAsync<OwnerWishListResponse>(await owner.PutAsJsonAsync($"/api/lists/{list.Id}", new UpdateWishListRequest
            { Name = list.Name, IsPublic = true, RowVersion = list.RowVersion }));
        var item = list.Items[0];
        var view = await anonymous.GetFromJsonAsync<ListViewResponse>($"/api/lists/{list.Id}/view");
        Assert.False(view!.Viewer!.CanMarkPurchases);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsync($"/api/lists/{list.Id}/guest-credential", null)).StatusCode);
        var body = new CreatePurchaseMarkRequest { Quantity = 2, IdempotencyKey = Guid.NewGuid() };
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsJsonAsync($"/api/lists/{list.Id}/items/{item.Id}/marks", body)).StatusCode);
        var mark = await JsonAsync<OwnPurchaseMarkResponse>(await buyer.PostAsJsonAsync($"/api/lists/{list.Id}/items/{item.Id}/marks", body));
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/api/lists/{list.Id}/items/{item.Id}/marks/{mark.Id}/undo",
            new ArchiveRequest { RowVersion = mark.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/lists/{list.Id}/items/{item.Id}/marks", body)).StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/lists/{list.Id}/view")).StatusCode);
        outsider.DefaultRequestHeaders.Add("X-Test-Scope", "unrelated_scope");
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/lists/{list.Id}/view")).StatusCode);
    }

    [Fact]
    public async Task Share_tokens_are_stable_list_specific_revocable_and_expire_at_the_exact_instant()
    {
        var clock = new TestClock();
        await using var factory = new OwnerApiFactory(clock: clock);
        using var owner = SignedInClient(factory);
        using var anonymous = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var list = await CreateListAsync(owner);
        var other = await CreateListAsync(owner);
        var first = await JsonAsync<ShareLinkResponse>(await owner.PostAsJsonAsync($"/api/lists/{list.Id}/share-links/", new CreateShareLinkRequest()));
        var expiring = await JsonAsync<ShareLinkResponse>(await owner.PostAsJsonAsync($"/api/lists/{list.Id}/share-links/", new CreateShareLinkRequest { ExpiresAt = clock.Now.AddMinutes(1) }));
        var links = await owner.GetFromJsonAsync<ShareLinkResponse[]>($"/api/lists/{list.Id}/share-links/");
        Assert.Equal(first.Token, links!.Single(link => link.Id == first.Id).Token);
        anonymous.DefaultRequestHeaders.Add(CapabilityHeaders.Share, first.Token);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/lists/{other.Id}/view")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/lists/{list.Id}/view")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/lists/{other.Id}/share-links/{first.Id}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/lists/{list.Id}/share-links/{first.Id}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/lists/{list.Id}/view")).StatusCode);
        anonymous.DefaultRequestHeaders.Remove(CapabilityHeaders.Share);
        anonymous.DefaultRequestHeaders.Add(CapabilityHeaders.Share, expiring.Token);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/lists/{list.Id}/view")).StatusCode);
        clock.Now = expiring.ExpiresAt!.Value;
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/lists/{list.Id}/view")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsync($"/api/lists/{list.Id}/guest-credential", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/lists/{list.Id}/share-links/", new CreateShareLinkRequest { ExpiresAt = clock.Now })).StatusCode);
        Assert.All((await owner.GetFromJsonAsync<ShareLinkResponse[]>($"/api/lists/{list.Id}/share-links/"))!, link => { Assert.False(link.IsActive); Assert.Null(link.Token); });
    }

    [SqlServerFact]
    public async Task Core_workflows_and_concurrent_retries_use_real_SQL_Server()
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("LISTHERO_TEST_SQL_CONNECTION")!);
        Assert.StartsWith("ListHero_Integration_", builder.InitialCatalog);
        builder.InitialCatalog += "_Core";
        await using var factory = new OwnerApiFactory(builder.ConnectionString);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.MigrateAsync();
        await ExerciseSharingAsync(factory);
        using var owner = SignedInClient(factory);
        using var buyer = SignedInClient(factory);
        var list = await CreateListAsync(owner);
        var update = new UpdateWishListRequest { Name = "Concurrent", IsPublic = true, RowVersion = list.RowVersion };
        var edits = await Task.WhenAll(owner.PutAsJsonAsync($"/api/lists/{list.Id}", update), owner.PutAsJsonAsync($"/api/lists/{list.Id}", update));
        Assert.Single(edits, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(edits, response => response.StatusCode == HttpStatusCode.Conflict);
        var body = new CreatePurchaseMarkRequest { Quantity = 6, IdempotencyKey = Guid.NewGuid() };
        var retries = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => buyer.PostAsJsonAsync($"/api/lists/{list.Id}/items/{list.Items[0].Id}/marks", body)));
        var marks = await Task.WhenAll(retries.Select(JsonAsync<OwnPurchaseMarkResponse>));
        Assert.Single(marks.Select(mark => mark.Id).Distinct());
        using var verification = factory.Services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<ListHeroDbContext>();
        Assert.Equal(1, await db.ItemPurchases.CountAsync(mark => mark.WishListItemId == list.Items[0].Id));
        Assert.True(await db.WishLists.AnyAsync(saved => saved.ArchivedAt != null));
        Assert.True(await db.ItemPurchases.AnyAsync(saved => saved.UndoneAt != null));
        // Optional isolated browser fixture. The normal development database is never used.
        var fixturePath = Environment.GetEnvironmentVariable("LISTHERO_UI_FIXTURE_PATH");
        if (!string.IsNullOrWhiteSpace(fixturePath))
        {
            var link = await JsonAsync<ShareLinkResponse>(await owner.PostAsJsonAsync($"/api/lists/{list.Id}/share-links/", new CreateShareLinkRequest()));
            await File.WriteAllTextAsync(fixturePath, System.Text.Json.JsonSerializer.Serialize(new
                { Database = builder.InitialCatalog, ListId = list.Id, ShareToken = link.Token }));
        }
    }

    [Fact]
    public async Task Guest_credential_alone_cannot_undo_after_revocation_but_can_survive_sign_in()
    {
        await using var factory = new OwnerApiFactory();
        using var owner = SignedInClient(factory);
        using var guest = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var signedInGuest = SignedInClient(factory);
        var list = await CreateListAsync(owner);
        var item = list.Items[0];
        var path = $"/api/lists/{list.Id}";
        var link = await JsonAsync<ShareLinkResponse>(await owner.PostAsJsonAsync(path + "/share-links/", new CreateShareLinkRequest()));
        guest.DefaultRequestHeaders.Add(CapabilityHeaders.Share, link.Token);
        var credential = await JsonAsync<GuestCredentialResponse>(await guest.PostAsync(path + "/guest-credential", null));
        guest.DefaultRequestHeaders.Add(CapabilityHeaders.Guest, credential.Credential);
        var body = new CreatePurchaseMarkRequest { Quantity = 2, IdempotencyKey = Guid.NewGuid() };
        var mark = await JsonAsync<OwnPurchaseMarkResponse>(await guest.PostAsJsonAsync(path + $"/items/{item.Id}/marks", body));
        var undo = new ArchiveRequest { RowVersion = mark.RowVersion };
        await owner.PostAsync(path + $"/share-links/{link.Id}/revoke", null);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsJsonAsync(path + $"/items/{item.Id}/marks/{mark.Id}/undo", undo)).StatusCode);
        list = await JsonAsync<OwnerWishListResponse>(await owner.PutAsJsonAsync(path, new UpdateWishListRequest { Name = list.Name, IsPublic = true, RowVersion = list.RowVersion }));
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync(path + $"/items/{item.Id}/marks/{mark.Id}/undo", undo)).StatusCode);
        signedInGuest.DefaultRequestHeaders.Add(CapabilityHeaders.Guest, credential.Credential);
        Assert.Single((await signedInGuest.GetFromJsonAsync<ListViewResponse>(path + "/view"))!.Viewer!.MyMarks!);
        Assert.Equal(HttpStatusCode.NoContent, (await signedInGuest.PostAsJsonAsync(path + $"/items/{item.Id}/marks/{mark.Id}/undo", undo)).StatusCode);
    }

    private static async Task ExerciseSharingAsync(WebApplicationFactory<ListHero.Api.ApiAssemblyMarker> factory)
    {
        using var owner = SignedInClient(factory);
        using var guest = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var otherGuest = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var list = await CreateListAsync(owner);
        var item = list.Items[0];
        var path = $"/api/lists/{list.Id}";
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(path + "/view")).StatusCode);
        var link = await JsonAsync<ShareLinkResponse>(await owner.PostAsJsonAsync(path + "/share-links/", new CreateShareLinkRequest()));
        guest.DefaultRequestHeaders.Add(CapabilityHeaders.Share, link.Token);
        otherGuest.DefaultRequestHeaders.Add(CapabilityHeaders.Share, link.Token);
        var credential = await JsonAsync<GuestCredentialResponse>(await guest.PostAsync(path + "/guest-credential", null));
        guest.DefaultRequestHeaders.Add(CapabilityHeaders.Guest, credential.Credential);
        Assert.Equal(credential.Credential, (await JsonAsync<GuestCredentialResponse>(await guest.PostAsync(path + "/guest-credential", null))).Credential);
        var body = new CreatePurchaseMarkRequest { Quantity = 6, IdempotencyKey = Guid.NewGuid() };
        Assert.Equal(HttpStatusCode.Forbidden, (await otherGuest.PostAsJsonAsync(path + $"/items/{item.Id}/marks", body)).StatusCode);
        var mark = await JsonAsync<OwnPurchaseMarkResponse>(await guest.PostAsJsonAsync(path + $"/items/{item.Id}/marks", body));
        Assert.Equal(mark.Id, (await JsonAsync<OwnPurchaseMarkResponse>(await guest.PostAsJsonAsync(path + $"/items/{item.Id}/marks", body))).Id);
        Assert.Equal(HttpStatusCode.Conflict, (await guest.PostAsJsonAsync(path + $"/items/{item.Id}/marks", new CreatePurchaseMarkRequest { Quantity = 1, IdempotencyKey = body.IdempotencyKey })).StatusCode);
        var view = (await guest.GetFromJsonAsync<ListViewResponse>(path + "/view"))!.Viewer!;
        Assert.True(view.Items[0].IsFulfilled);
        Assert.Equal(6, view.Items[0].PurchasedQuantity);
        Assert.Equal(2, view.Items[0].OverpurchasedBy);
        Assert.Single(view.MyMarks!);
        Assert.Empty((await otherGuest.GetFromJsonAsync<ListViewResponse>(path + "/view"))!.Viewer!.MyMarks!);
        var warning = await owner.GetFromJsonAsync<ItemEditWarningResponse>(path + $"/items/{item.Id}/edit-warning");
        Assert.True(warning!.MayHavePurchaseMarks);
        owner.DefaultRequestHeaders.Add(CapabilityHeaders.Share, link.Token);
        foreach (var url in new[] { path + "/owner", path + "/view" })
        {
            var response = await owner.GetAsync(url);
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            var json = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("purchase", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fulfilled", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("myMarks", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("viewer", json, StringComparison.OrdinalIgnoreCase);
        }
        var edited = await JsonAsync<OwnerWishListResponse>(await owner.PutAsJsonAsync(path + $"/items/{item.Id}", new UpdateWishListItemRequest
            { Name = "Updated wish", DesiredQuantity = 3, RowVersion = item.RowVersion }));
        Assert.Equal(3, edited.Items[0].DesiredQuantity);
        var undo = new ArchiveRequest { RowVersion = mark.RowVersion };
        Assert.Equal(HttpStatusCode.NotFound, (await otherGuest.PostAsJsonAsync(path + $"/items/{item.Id}/marks/{mark.Id}/undo", undo)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await guest.PostAsJsonAsync(path + $"/items/{item.Id}/marks/{mark.Id}/undo", undo)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await guest.PostAsJsonAsync(path + $"/items/{item.Id}/marks/{mark.Id}/undo", undo)).StatusCode);
        // Retrying a creation after undo must return the original undone mark, not resurrect it.
        Assert.NotNull((await JsonAsync<OwnPurchaseMarkResponse>(await guest.PostAsJsonAsync(path + $"/items/{item.Id}/marks", body))).UndoneAt);
        Assert.Equal(0, (await guest.GetFromJsonAsync<ListViewResponse>(path + "/view"))!.Viewer!.Items[0].PurchasedQuantity);
        edited = await JsonAsync<OwnerWishListResponse>(await owner.PostAsJsonAsync(path + $"/items/{item.Id}/archive", new ArchiveRequest { RowVersion = edited.Items[0].RowVersion }));
        Assert.Empty(edited.Items);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync(path + "/archive", new ArchiveRequest { RowVersion = edited.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(path + "/view")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsJsonAsync(path + $"/items/{item.Id}/marks", body)).StatusCode);
    }
    private static async Task<OwnerWishListResponse> CreateListAsync(HttpClient owner)
    {
        var list = await JsonAsync<OwnerWishListResponse>(await owner.PostAsJsonAsync("/api/lists/", new CreateWishListRequest { Name = "Wishes" }));
        return await JsonAsync<OwnerWishListResponse>(await owner.PostAsJsonAsync($"/api/lists/{list.Id}/items", new CreateWishListItemRequest { Name = "Mug", DesiredQuantity = 4 }));
    }
    private static async Task<T> JsonAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"Expected success, received {(int)response.StatusCode}.");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class MemoryCollaborationStore : IListViewerStore, IShareLinkStore, IPurchaseStore
    {
        private readonly MemoryListStore lists;
        public ConcurrentDictionary<Guid, ListShareLink> Links { get; } = new();
        public ConcurrentDictionary<Guid, GuestIdentity> Guests { get; } = new();
        public ConcurrentDictionary<Guid, ItemPurchase> Marks { get; } = new();
        public MemoryCollaborationStore(MemoryListStore lists)
        {
            this.lists = lists;
            lists.PurchaseExists = id => Marks.Values.Any(mark => mark.WishListItemId == id && mark.IsActive);
        }
        public Task<WishList?> GetActiveListAsync(Guid listId, CancellationToken ct = default) => Task.FromResult(lists.Lists.TryGetValue(listId, out var list) && !list.IsArchived ? list : null);
        public Task<ListShareLink?> GetShareByHashAsync(string hash, CancellationToken ct = default) => Task.FromResult(Links.Values.SingleOrDefault(link => link.TokenHash == hash && !link.IsArchived));
        public Task<GuestIdentity?> GetGuestByHashAsync(string hash, CancellationToken ct = default) => Task.FromResult(Guests.Values.SingleOrDefault(guest => guest.CredentialHash == hash && !guest.IsArchived));
        public Task<IReadOnlyList<ItemPurchase>> GetPurchasesAsync(Guid listId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ItemPurchase>>(Marks.Values.Where(mark => lists.Items[mark.WishListItemId].WishListId == listId).ToArray());
        public Task<IReadOnlyList<ListShareLink>> GetLinksAsync(Guid listId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ListShareLink>>(Links.Values.Where(link => link.WishListId == listId).ToArray());
        public Task AddLinkAsync(ListShareLink link, CancellationToken ct = default) { Links[link.Id] = link; return Task.CompletedTask; }
        public Task RevokeLinkAsync(Guid listId, Guid linkId, DateTimeOffset now, CancellationToken ct = default)
        {
            if (!Links.TryGetValue(linkId, out var link) || link.WishListId != listId) throw new ListHero.Application.Common.ResourceNotFoundException();
            link.Revoke(now); return Task.CompletedTask;
        }
        public Task AddGuestAsync(GuestIdentity guest, CancellationToken ct = default) { Guests[guest.Id] = guest; return Task.CompletedTask; }
        public Task<ItemPurchase?> GetPurchaseAsync(Guid itemId, Guid markId, CancellationToken ct = default) => Task.FromResult(Marks.TryGetValue(markId, out var mark) && mark.WishListItemId == itemId && !mark.IsArchived ? mark : null);
        public Task<ItemPurchase?> GetByIdempotencyKeyAsync(Guid itemId, Guid key, CancellationToken ct = default) => Task.FromResult(Marks.Values.SingleOrDefault(mark => mark.WishListItemId == itemId && mark.IdempotencyKey == key));
        public Task<ItemPurchase> AddOrGetAsync(ItemPurchase mark, CancellationToken ct = default) { StampVersion(mark); Marks[mark.Id] = mark; return Task.FromResult(mark); }
        public Task SavePurchaseAsync(ItemPurchase mark, byte[] expectedVersion, CancellationToken ct = default) { StampVersion(mark); return Task.CompletedTask; }
    }
}
