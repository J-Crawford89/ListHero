using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ListHero.Client.Api;
using ListHero.Contracts.Lists;
using ListHero.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.Api;

public sealed partial class OwnerWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archived_owner_cannot_be_bypassed_by_public_visibility_share_links_guests_or_admins(bool isPublic)
    {
        await using var factory = new OwnerApiFactory();
        using var owner = SignedInClient(factory);
        using var admin = SignedInClient(factory);
        admin.DefaultRequestHeaders.Add("X-Test-Role", "Admin");
        using var guest = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var list = await CreateListAsync(owner);
        if (isPublic)
        {
            await SetProfileAsync(owner);
            list = await JsonAsync<OwnerWishListResponse>(await owner.PutAsJsonAsync($"/api/lists/{list.Id}",
                new UpdateWishListRequest { Name = list.Name, RowVersion = list.RowVersion, IsPublic = true }));
        }
        var path = $"/api/lists/{list.Id}";
        var link = await JsonAsync<ShareLinkResponse>(await owner.PostAsJsonAsync(path + "/share-links/", new CreateShareLinkRequest()));
        guest.DefaultRequestHeaders.Add(CapabilityHeaders.Share, link.Token);
        var credential = await JsonAsync<GuestCredentialResponse>(await guest.PostAsync(path + "/guest-credential", null));
        guest.DefaultRequestHeaders.Add(CapabilityHeaders.Guest, credential.Credential);
        var body = new CreatePurchaseMarkRequest { Quantity = 1, IdempotencyKey = Guid.NewGuid() };
        var mark = await JsonAsync<OwnPurchaseMarkResponse>(await guest.PostAsJsonAsync(path + $"/items/{list.Items[0].Id}/marks", body));

        var ownerId = factory.Services.GetRequiredService<MemoryListStore>().Lists[list.Id].OwnerId;
        factory.Services.GetRequiredService<MemoryUserStore>().Users.Values.Single(user => user.Id == ownerId).Archive(DateTimeOffset.UtcNow);

        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(path + "/view")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(path + "/view")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(path + "/owner")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsync(path + "/guest-credential", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsJsonAsync(path + $"/items/{list.Items[0].Id}/marks", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsJsonAsync(path + $"/items/{list.Items[0].Id}/marks/{mark.Id}/undo", new ArchiveRequest { RowVersion = mark.RowVersion })).StatusCode);
        Assert.Null(factory.Services.GetRequiredService<MemoryCollaborationStore>().Marks[mark.Id].UndoneAt);
        Assert.False(factory.Services.GetRequiredService<MemoryListStore>().Lists[list.Id].IsArchived);
    }

    [SqlServerFact]
    public async Task Disabling_an_owner_blocks_every_list_access_path_and_redacts_saved_entries_without_erasing_history()
    {
        await using var factory = new OwnerApiFactory(IsolatedSqlConnection("_ArchivedOwner"));
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.MigrateAsync();
        using var owner = SignedInClient(factory);
        using var buyer = SignedInClient(factory);
        using var admin = SignedInClient(factory);
        admin.DefaultRequestHeaders.Add("X-Test-Role", "Admin");
        using var anonymous = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var guest = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var profile = await SetProfileAsync(owner, "disabled-hero");
        var privateList = await CreateListAsync(owner);
        var publicList = await CreateListAsync(owner);
        publicList = await JsonAsync<OwnerWishListResponse>(await owner.PutAsJsonAsync($"/api/lists/{publicList.Id}",
            new UpdateWishListRequest { Name = "Disabled owner's public title", RowVersion = publicList.RowVersion, IsPublic = true }));
        var control = await CreateListAsync(buyer);
        await SetProfileAsync(buyer, "active-hero");
        control = await JsonAsync<OwnerWishListResponse>(await buyer.PutAsJsonAsync($"/api/lists/{control.Id}",
            new UpdateWishListRequest { Name = "Active owner's list", RowVersion = control.RowVersion, IsPublic = true }));
        var buyerDashboard = new DashboardApiClient(buyer, new TestTokenProvider());
        await new DashboardApiClient(owner, new TestTokenProvider()).SaveAsync(privateList.Id, true);
        foreach (var list in new[] { privateList, publicList })
        {
            var link = await JsonAsync<ShareLinkResponse>(await owner.PostAsJsonAsync($"/api/lists/{list.Id}/share-links/", new CreateShareLinkRequest()));
            await buyerDashboard.SaveAsync(list.Id, true, link.Token);
            if (list.Id == privateList.Id) guest.DefaultRequestHeaders.Add(CapabilityHeaders.Share, link.Token);
        }
        var path = $"/api/lists/{privateList.Id}";
        var item = privateList.Items[0];
        var credential = await JsonAsync<GuestCredentialResponse>(await guest.PostAsync(path + "/guest-credential", null));
        guest.DefaultRequestHeaders.Add(CapabilityHeaders.Guest, credential.Credential);
        var request = new CreatePurchaseMarkRequest { Quantity = 2, IdempotencyKey = Guid.NewGuid() };
        var guestMark = await JsonAsync<OwnPurchaseMarkResponse>(await guest.PostAsJsonAsync(path + $"/items/{item.Id}/marks", request));
        var buyerMark = await JsonAsync<OwnPurchaseMarkResponse>(await buyer.PostAsJsonAsync($"/api/lists/{publicList.Id}/items/{publicList.Items[0].Id}/marks",
            new CreatePurchaseMarkRequest { Quantity = 1, IdempotencyKey = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, (await buyer.GetAsync(path + "/view")).StatusCode); // Saved private grant, no share header.
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/lists/{publicList.Id}/view")).StatusCode);
        Assert.All((await buyerDashboard.SavedAsync()).Items, entry => Assert.True(entry.IsAvailable));

        Guid ownerId;
        // Use the existing account archive mechanism; no new admin endpoint or Entra change.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ListHeroDbContext>();
            ownerId = (await db.WishLists.SingleAsync(list => list.Id == privateList.Id)).OwnerId;
            var account = await db.Users.SingleAsync(user => user.Id == ownerId);
            account.Archive(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        foreach (var list in new[] { privateList, publicList })
        {
            var listPath = $"/api/lists/{list.Id}";
            foreach (var viewer in new[] { buyer, admin, anonymous, guest })
            {
                using var denied = await viewer.GetAsync(listPath + "/view");
                Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
                var json = await denied.Content.ReadAsStringAsync();
                Assert.DoesNotContain(list.Name, json); Assert.DoesNotContain(credential.Credential, json);
                Assert.Contains("no-store", denied.Headers.CacheControl!.ToString());
                Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsJsonAsync(listPath + $"/items/{list.Items[0].Id}/marks",
                    new CreatePurchaseMarkRequest { Quantity = 1, IdempotencyKey = Guid.NewGuid() })).StatusCode);
            }
            Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsync(listPath + "/guest-credential", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await buyer.PutAsJsonAsync($"/api/dashboard/saved/{list.Id}", new SaveListRequest { IsPinned = false })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(listPath + "/owner")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(listPath + "/share-links/")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync(listPath,
                new UpdateWishListRequest { Name = "Blocked edit", RowVersion = list.RowVersion })).StatusCode);
        }
        // Even a retry of an already committed mark must go through the disabled-owner gate.
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsJsonAsync(path + $"/items/{item.Id}/marks", request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.PostAsJsonAsync(path + $"/items/{item.Id}/marks/{guestMark.Id}/undo",
            new ArchiveRequest { RowVersion = guestMark.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await buyer.PostAsJsonAsync($"/api/lists/{publicList.Id}/items/{publicList.Items[0].Id}/marks/{buyerMark.Id}/undo",
            new ArchiveRequest { RowVersion = buyerMark.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/dashboard/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/lists/mine")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/lists/", new CreateWishListRequest { Name = "Blocked" })).StatusCode);

        var saved = await buyerDashboard.SavedAsync();
        Assert.Equal(2, saved.Items.Count);
        Assert.All(saved.Items, AssertUnavailable);
        var dashboard = await buyerDashboard.GetAsync();
        Assert.All(dashboard.Pinned, AssertUnavailable); Assert.Equal(2, dashboard.Pinned.Count);
        Assert.All(dashboard.Saved, AssertUnavailable); Assert.Single(dashboard.Owned);
        var discovery = new DiscoveryApiClient(anonymous);
        Assert.Equal(control.Id, Assert.Single((await discovery.GetListsAsync(new())).Items).Id);
        Assert.Equal("active-hero", Assert.Single((await discovery.GetPeopleAsync(new("hero"))).Items).Handle);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/discovery/people/{profile.Handle}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/lists/{control.Id}/view")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ListHeroDbContext>();
            var lists = new EfWishListStore(db);
            Assert.Empty((await lists.GetByOwnerAsync(ownerId, 1, 15)).Items);
            Assert.Empty(await lists.GetOwnerPreviewAsync(ownerId));
            Assert.Null(await lists.GetOwnedAsync(privateList.Id, ownerId));
            Assert.Null(await new EfCollaborationStore(db).GetActiveListAsync(publicList.Id));
            Assert.Equal(2, await db.WishLists.CountAsync(list => list.OwnerId == ownerId && list.ArchivedAt == null));
            Assert.Equal(2, await db.ItemPurchases.CountAsync(mark => mark.UndoneAt == null && mark.ArchivedAt == null));
            Assert.Equal(2, await db.ListShareLinks.CountAsync(link => link.RevokedAt == null && link.ArchivedAt == null));
            Assert.Equal(3, await db.SavedLists.CountAsync(entry => entry.ArchivedAt == null && entry.IsPinned));
            var ownSaved = Assert.Single((await new EfSavedListStore(db).GetAsync(ownerId, 1, false, DateTimeOffset.UtcNow, CancellationToken.None)).Items);
            AssertUnavailable(ownSaved); Assert.True(ownSaved.IsOwner);
            Assert.Single(await db.GuestIdentities.ToArrayAsync());
        }
        // Recipients can still remove an unavailable reference from their own dashboard.
        await buyerDashboard.RemoveAsync(privateList.Id);
        Assert.Equal(publicList.Id, Assert.Single((await buyerDashboard.SavedAsync()).Items).ListId);
    }

    private static void AssertUnavailable(SavedListSummary entry)
    {
        Assert.False(entry.IsAvailable);
        Assert.True(entry.IsPinned);
        Assert.Equal("List unavailable", entry.Name);
        Assert.DoesNotContain("Token", JsonSerializer.Serialize(entry), StringComparison.OrdinalIgnoreCase);
    }
}
