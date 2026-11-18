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
    [SqlServerFact]
    public async Task Saved_lists_follow_link_revocation_account_isolation_and_pin_preferences()
    {
        await using var factory = new OwnerApiFactory(IsolatedSqlConnection("_Dashboard"));
        using (var scope = factory.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.MigrateAsync();
        using var owner = SignedInClient(factory); using var buyer = SignedInClient(factory); using var other = SignedInClient(factory);
        using var anonymous = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/dashboard/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"/api/dashboard/saved/{Guid.NewGuid()}", new SaveListRequest())).StatusCode);
        var list = await CreateListAsync(owner);
        var ownApi = new DashboardApiClient(owner, new TestTokenProvider()); var api = new DashboardApiClient(buyer, new TestTokenProvider());
        Assert.Empty((await api.GetAsync()).Saved);
        Assert.Equal(HttpStatusCode.NotFound, (await buyer.GetAsync($"/api/lists/{list.Id}/view")).StatusCode);
        await Assert.ThrowsAsync<ListHero.Client.Abstractions.Api.ApiRequestException>(() => api.SaveAsync(list.Id, false));
        var link = await JsonAsync<ShareLinkResponse>(await owner.PostAsJsonAsync($"/api/lists/{list.Id}/share-links/", new CreateShareLinkRequest()));
        await api.SaveAsync(list.Id, false, link.Token);
        Assert.NotNull((await buyer.GetFromJsonAsync<ListViewResponse>($"/api/lists/{list.Id}/view"))!.Viewer);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/lists/{list.Id}/view")).StatusCode);
        await api.SaveAsync(list.Id, true);
        var dashboard = await api.GetAsync(); Assert.Single(dashboard.Pinned); Assert.Single(dashboard.Saved); Assert.Empty(dashboard.Owned);
        var json = JsonSerializer.Serialize(dashboard);
        Assert.DoesNotContain(link.Token!, json); Assert.DoesNotContain("Purchased", json); Assert.DoesNotContain("Fulfilled", json);
        await ownApi.SaveAsync(list.Id, true); Assert.True(Assert.Single((await ownApi.GetAsync()).Pinned).IsOwner);
        await api.SaveAsync(list.Id, false); Assert.Empty((await api.GetAsync()).Pinned);
        Assert.Single((await api.SavedAsync()).Items); Assert.Empty((await api.SavedAsync(2)).Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await buyer.GetAsync("/api/dashboard/saved?page=0")).StatusCode);
        await owner.PostAsync($"/api/lists/{list.Id}/share-links/{link.Id}/revoke", null);
        var unavailable = Assert.Single((await api.SavedAsync()).Items); Assert.False(unavailable.IsAvailable); Assert.Equal("List unavailable", unavailable.Name);
        Assert.Equal(HttpStatusCode.NotFound, (await buyer.GetAsync($"/api/lists/{list.Id}/view")).StatusCode);
        await api.RemoveAsync(list.Id); await api.RemoveAsync(list.Id); Assert.Empty((await api.SavedAsync()).Items);
        Assert.Single((await ownApi.GetAsync()).Owned);
        var replacement = await JsonAsync<ShareLinkResponse>(await owner.PostAsJsonAsync($"/api/lists/{list.Id}/share-links/", new CreateShareLinkRequest()));
        await api.SaveAsync(list.Id, false, replacement.Token);
        Assert.Single((await api.SavedAsync()).Items);
        Assert.Contains("no-store", (await buyer.GetAsync("/api/dashboard/")).Headers.CacheControl!.ToString());
    }
}
