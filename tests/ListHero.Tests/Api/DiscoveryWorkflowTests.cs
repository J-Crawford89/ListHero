using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ListHero.Application.Common;
using ListHero.Application.Discovery;
using ListHero.Client.Api;
using ListHero.Contracts.Discovery;
using ListHero.Contracts.Lists;
using ListHero.Domain.Entities;
using ListHero.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.Api;

public sealed partial class OwnerWorkflowTests
{
    [Theory]
    [InlineData("lists?q=x")] [InlineData("lists?page=0")] [InlineData("lists?pageSize=51")]
    [InlineData("people?q=x")] [InlineData("people?q=%40%40")]
    [InlineData("people/jane?page=0")] [InlineData("people/a")]
    public async Task Public_discovery_validates_inputs_before_querying_SQL(string route)
    {
        await using var factory = new OwnerApiFactory();
        using var http = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/discovery/" + route)).StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<MemoryUserStore>().Users);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/discovery/people")).StatusCode);
    }

    private static async Task<OwnProfileResponse> SetProfileAsync(HttpClient http, string? handle = null)
    {
        var api = new PublicProfileApiClient(http, new TestTokenProvider());
        var current = await api.GetAsync();
        return await api.UpdateAsync(new() { DisplayName = "Jane Hero", Handle = handle ?? "jane-" + Guid.NewGuid().ToString("N")[..10],
            RowVersion = current.RowVersion, ConfirmDiscovery = true });
    }

    [Fact]
    public async Task Profile_requires_authentication_consent_and_valid_versions_and_publication_requires_profile()
    {
        await using var factory = new OwnerApiFactory();
        using var anonymous = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/profile/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/profile/me", new UpdatePublicProfileRequest())).StatusCode);
        using var owner = SignedInClient(factory);
        var list = await CreateListAsync(owner);
        var publish = new UpdateWishListRequest { Name = list.Name, RowVersion = list.RowVersion, IsPublic = true };
        var denied = await owner.PutAsJsonAsync($"/api/lists/{list.Id}", publish);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Contains("profile_required", await denied.Content.ReadAsStringAsync());
        var profile = await owner.GetFromJsonAsync<OwnProfileResponse>("/api/profile/me");
        Assert.Null(profile!.Handle);
        Assert.Contains("no-store", (await owner.GetAsync("/api/profile/me")).Headers.CacheControl!.ToString());
        var invalid = new UpdatePublicProfileRequest { DisplayName = "Jane", Handle = "jane", RowVersion = profile.RowVersion };
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/profile/me", invalid)).StatusCode);
        invalid.ConfirmDiscovery = true;
        invalid.RowVersion = "invalid";
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/profile/me", invalid)).StatusCode);
        invalid.RowVersion = Convert.ToBase64String([1]);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/profile/me", invalid)).StatusCode);
        invalid.RowVersion = profile.RowVersion;
        profile = await JsonAsync<OwnProfileResponse>(await owner.PutAsJsonAsync("/api/profile/me", invalid));
        Assert.Equal("jane", profile.Handle);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/profile/me", invalid)).StatusCode);
        invalid.RowVersion = profile.RowVersion; invalid.Handle = "changed";
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/profile/me", invalid)).StatusCode);
        invalid.Handle = "jane"; invalid.DisplayName = "Jane Updated";
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/profile/me", invalid)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/lists/{list.Id}", publish)).StatusCode);
        using var other = SignedInClient(factory);
        var duplicate = await other.GetFromJsonAsync<OwnProfileResponse>("/api/profile/me");
        invalid.RowVersion = duplicate!.RowVersion;
        var conflict = await other.PutAsJsonAsync("/api/profile/me", invalid);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("handle_unavailable", await conflict.Content.ReadAsStringAsync());
    }

    [SqlServerFact]
    public async Task Discovery_SQL_excludes_private_archived_and_unconfirmed_content_and_reuses_viewer_access()
    {
        await using var factory = new OwnerApiFactory(IsolatedSqlConnection("_Discovery"));
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.MigrateAsync();
        using var owner = SignedInClient(factory);
        using var other = SignedInClient(factory);
        using var anonymous = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var discovery = new DiscoveryApiClient(anonymous);
        var first = await CreateListAsync(owner);
        var hidden = await JsonAsync<OwnerWishListResponse>(await owner.PostAsJsonAsync("/api/lists/", new CreateWishListRequest { Name = "PRIVATE SECRET" }));
        // Simulate a public list from before public profile onboarding.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ListHeroDbContext>();
            var legacy = await db.WishLists.FindAsync(first.Id);
            legacy!.Update("Camping 100%_ gear", new string('x', 220) + " wilderness", true);
            await db.SaveChangesAsync();
        }
        Assert.Empty((await discovery.GetListsAsync(new())).Items);
        Assert.NotNull((await anonymous.GetFromJsonAsync<ListViewResponse>($"/api/lists/{first.Id}/view"))!.Viewer);
        var profile = await SetProfileAsync(owner, "@JANE-HERO");
        Assert.Equal("jane-hero", profile.Handle);
        await SetProfileAsync(other, "jane-other");
        var second = await JsonAsync<OwnerWishListResponse>(await owner.PostAsJsonAsync("/api/lists/", new CreateWishListRequest { Name = "Another public list" }));
        second = await JsonAsync<OwnerWishListResponse>(await owner.PutAsJsonAsync($"/api/lists/{second.Id}", new UpdateWishListRequest { Name = second.Name, RowVersion = second.RowVersion, IsPublic = true }));
        var archived = await JsonAsync<OwnerWishListResponse>(await owner.PostAsJsonAsync("/api/lists/", new CreateWishListRequest { Name = "ARCHIVED SECRET" }));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ListHeroDbContext>();
            var record = await db.WishLists.FindAsync(archived.Id); record!.Update(record.Name, "", true); record.Archive(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        var page = await discovery.GetListsAsync(new(null, 1, 1));
        Assert.Single(page.Items); Assert.True(page.HasMore);
        var next = await discovery.GetListsAsync(new(null, 2, 1));
        Assert.Single(next.Items); Assert.NotEqual(page.Items[0].Id, next.Items[0].Id); Assert.False(next.HasMore);
        Assert.Empty((await discovery.GetListsAsync(new(null, 3, 1))).Items);
        var found = Assert.Single((await discovery.GetListsAsync(new("WILDERNESS"))).Items);
        Assert.Equal(first.Id, found.Id); Assert.Equal(200, found.Description.Length);
        Assert.Single((await discovery.GetListsAsync(new("100%_"))).Items);
        Assert.Empty((await discovery.GetListsAsync(new("100%z"))).Items);
        Assert.Empty((await discovery.GetListsAsync(new("SECRET"))).Items);
        Assert.Empty((await discovery.GetPeopleAsync(new())).Items);
        var people = await discovery.GetPeopleAsync(new("JANE"));
        Assert.Equal("jane-hero", Assert.Single(people.Items).Handle); // Other Jane has no public lists.
        Assert.Equal("jane-hero", Assert.Single((await discovery.GetPeopleAsync(new("@JANE-HERO"))).Items).Handle);
        Assert.Equal(2, (await discovery.GetProfileAsync("JANE-HERO")).Lists.Items.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/discovery/people/jane-other")).StatusCode);
        Assert.Contains("no-store", (await anonymous.GetAsync("/api/discovery/lists")).Headers.CacheControl!.ToString());
        var json = JsonSerializer.Serialize(page);
        foreach (var forbidden in new[] { "Purchased", "Fulfilled", "IdentityIssuer", "IdentitySubject", "RowVersion", "Token", "PRIVATE SECRET" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull((await owner.GetFromJsonAsync<ListViewResponse>($"/api/lists/{first.Id}/view"))!.Owner);
        Assert.False((await anonymous.GetFromJsonAsync<ListViewResponse>($"/api/lists/{first.Id}/view"))!.Viewer!.CanMarkPurchases);
        first = (await owner.GetFromJsonAsync<OwnerWishListResponse>($"/api/lists/{first.Id}/owner"))!;
        await owner.PutAsJsonAsync($"/api/lists/{first.Id}", new UpdateWishListRequest { Name = first.Name, RowVersion = first.RowVersion, IsPublic = false });
        Assert.Empty((await discovery.GetListsAsync(new("wilderness"))).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/lists/{first.Id}/view")).StatusCode);
        await owner.PostAsJsonAsync($"/api/lists/{second.Id}/archive", new ArchiveRequest { RowVersion = second.RowVersion });
        Assert.Empty((await discovery.GetPeopleAsync(new("jane"))).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/discovery/people/jane-hero")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ListHeroDbContext>();
            var record = await db.WishLists.FindAsync(hidden.Id); record!.Update("Inactive owner's public list", "", true);
            var user = await db.Users.FindAsync(record.OwnerId); user!.Archive(DateTimeOffset.UtcNow); await db.SaveChangesAsync();
        }
        Assert.Empty((await discovery.GetListsAsync(new())).Items);
    }

    [SqlServerFact]
    public async Task SQL_profile_claims_are_unique_and_concurrent_edits_are_rejected()
    {
        await using var factory = new OwnerApiFactory(IsolatedSqlConnection("_ProfileClaims"));
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.MigrateAsync();
        using var first = SignedInClient(factory); using var second = SignedInClient(factory);
        var a = await first.GetFromJsonAsync<OwnProfileResponse>("/api/profile/me");
        var b = await second.GetFromJsonAsync<OwnProfileResponse>("/api/profile/me");
        UpdatePublicProfileRequest Claim(string version, string handle) => new() { DisplayName = "Same Name", Handle = handle, RowVersion = version, ConfirmDiscovery = true };
        var claims = await Task.WhenAll(first.PutAsJsonAsync("/api/profile/me", Claim(a!.RowVersion, "Shared")), second.PutAsJsonAsync("/api/profile/me", Claim(b!.RowVersion, "SHARED")));
        Assert.Equal(1, claims.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, claims.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        using var scope1 = factory.Services.CreateScope(); using var scope2 = factory.Services.CreateScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<ListHeroDbContext>(); var db2 = scope2.ServiceProvider.GetRequiredService<ListHeroDbContext>();
        var user1 = await db1.Users.AsNoTracking().SingleAsync(u => u.PublicHandle == "shared");
        var user2 = await db2.Users.AsNoTracking().SingleAsync(u => u.Id == user1.Id);
        var version1 = user1.RowVersion.ToArray(); var version2 = user2.RowVersion.ToArray();
        user1.UpdatePublicProfile("First edit", "shared"); user2.UpdatePublicProfile("Stale edit", "shared");
        await new EfPublicProfileStore(db1).SaveAsync(user1, version1, default);
        await Assert.ThrowsAsync<EditConflictException>(() => new EfPublicProfileStore(db2).SaveAsync(user2, version2, default));
        Assert.Equal("First edit", (await db1.Users.AsNoTracking().SingleAsync(u => u.Id == user1.Id)).PublicDisplayName);
    }
}
