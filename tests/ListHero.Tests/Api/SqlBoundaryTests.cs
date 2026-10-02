using System.Net;
using System.Net.Http.Json;
using ListHero.Application.Collaboration;
using ListHero.Contracts.Lists;
using ListHero.Domain.Entities;
using ListHero.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ListHero.Tests.Api;

public sealed partial class OwnerWorkflowTests
{
    [SqlServerFact]
    public async Task SQL_enforces_constraints_and_persists_link_revocation_and_expiration()
    {
        var connection = IsolatedSqlConnection("_Boundaries");
        var clock = new TestClock();
        await using var factory = new OwnerApiFactory(connection, clock);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.MigrateAsync();
        using var owner = SignedInClient(factory);
        using var buyer = SignedInClient(factory);
        using var guest = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var list = await CreateListAsync(owner);
        var item = list.Items[0];
        var path = $"/api/lists/{list.Id}";
        var api = new ListHero.Client.Api.ListCollaborationApiClient(owner, new TestTokenProvider());
        var link = await api.CreateLinkAsync(list.Id, new());
        Assert.Equal(link.Token, Assert.Single(await api.GetLinksAsync(list.Id)).Token);
        guest.DefaultRequestHeaders.Add(CapabilityHeaders.Share, link.Token);
        var credential = await JsonAsync<GuestCredentialResponse>(await guest.PostAsync(path + "/guest-credential", null));
        guest.DefaultRequestHeaders.Add(CapabilityHeaders.Guest, credential.Credential);
        var mark = await JsonAsync<OwnPurchaseMarkResponse>(await guest.PostAsJsonAsync(path + $"/items/{item.Id}/marks", new CreatePurchaseMarkRequest { IdempotencyKey = Guid.NewGuid() }));
        using var verification = factory.Services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<ListHeroDbContext>();
        // Bypass domain validation deliberately: the database must reject invalid writes itself.
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WishListItems SET DesiredQuantity = 0 WHERE Id = {item.Id}"))).Number);
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WishListItems SET ApproximateUnitPrice = -1 WHERE Id = {item.Id}"))).Number);
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WishListItems SET WishListId = {Guid.NewGuid()} WHERE Id = {item.Id}"))).Number);
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ItemPurchases SET Quantity = 0 WHERE Id = {mark.Id}"))).Number);
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ItemPurchases SET GuestIdentityId = NULL WHERE Id = {mark.Id}"))).Number);
        var ownerId = await db.WishLists.Where(l => l.Id == list.Id).Select(l => l.OwnerId).SingleAsync();
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ItemPurchases SET UserId = {ownerId} WHERE Id = {mark.Id}"))).Number);
        await api.RevokeLinkAsync(list.Id, link.Id);
        await api.RevokeLinkAsync(list.Id, link.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(path + "/view")).StatusCode);
        var revoked = Assert.Single(await api.GetLinksAsync(list.Id));
        Assert.Null(revoked.Token); Assert.False(revoked.IsActive);
        Assert.NotNull((await db.ListShareLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id)).RevokedAt);
        var expiring = await api.CreateLinkAsync(list.Id, new() { ExpiresAt = clock.Now.AddMinutes(1) });
        guest.DefaultRequestHeaders.Remove(CapabilityHeaders.Share);
        guest.DefaultRequestHeaders.Add(CapabilityHeaders.Share, expiring.Token);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(path + "/view")).StatusCode);
        clock.Now = expiring.ExpiresAt!.Value;
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(path + "/view")).StatusCode);
        Assert.Null((await api.GetLinksAsync(list.Id)).Single(l => l.Id == expiring.Id).Token);
        var unchanged = await db.WishListItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        Assert.Equal(4, unchanged.DesiredQuantity); Assert.Null(unchanged.ApproximateUnitPrice);
        var saved = await db.ItemPurchases.AsNoTracking().SingleAsync(p => p.Id == mark.Id);
        Assert.Equal(1, saved.Quantity); Assert.Null(saved.UserId); Assert.NotNull(saved.GuestIdentityId);
    }

    [SqlServerFact]
    public async Task Concurrent_undo_of_the_same_SQL_mark_recovers_the_losing_row_version()
    {
        var gate = new SaveGate();
        await using var factory = new OwnerApiFactory(IsolatedSqlConnection("_UndoRace"), overrides: services =>
        {
            services.RemoveAll<IPurchaseStore>();
            services.AddScoped<IPurchaseStore>(p => new GatedPurchaseStore(p.GetRequiredService<EfCollaborationStore>(), gate));
        });
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.MigrateAsync();
        using var owner = SignedInClient(factory);
        using var buyer = SignedInClient(factory);
        var list = await CreateListAsync(owner);
        await owner.PutAsJsonAsync($"/api/lists/{list.Id}", new UpdateWishListRequest { Name = list.Name, IsPublic = true, RowVersion = list.RowVersion });
        var item = list.Items[0];
        var mark = await JsonAsync<OwnPurchaseMarkResponse>(await buyer.PostAsJsonAsync($"/api/lists/{list.Id}/items/{item.Id}/marks", new CreatePurchaseMarkRequest { IdempotencyKey = Guid.NewGuid() }));
        var route = $"/api/lists/{list.Id}/items/{item.Id}/marks/{mark.Id}/undo";
        var undo = new ArchiveRequest { RowVersion = mark.RowVersion };
        var results = await Task.WhenAll(buyer.PostAsJsonAsync(route, undo), buyer.PostAsJsonAsync(route, undo)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.All(results, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
        Assert.Equal(2, gate.Arrivals);
        var view = (await buyer.GetFromJsonAsync<ListViewResponse>($"/api/lists/{list.Id}/view"))!.Viewer!;
        Assert.Equal(0, view.Items[0].PurchasedQuantity); Assert.Empty(view.MyMarks!);
        using var check = factory.Services.CreateScope();
        Assert.NotNull((await check.ServiceProvider.GetRequiredService<ListHeroDbContext>().ItemPurchases.SingleAsync()).UndoneAt);
    }

    [SqlServerFact]
    public async Task Simultaneous_SQL_requests_with_the_same_key_and_different_payloads_have_one_winner()
    {
        await using var factory = new OwnerApiFactory(IsolatedSqlConnection("_PayloadRace"));
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.MigrateAsync();
        using var owner = SignedInClient(factory);
        using var buyer = SignedInClient(factory);
        using var another = SignedInClient(factory);
        var list = await CreateListAsync(owner);
        await owner.PutAsJsonAsync($"/api/lists/{list.Id}", new UpdateWishListRequest { Name = list.Name, IsPublic = true, RowVersion = list.RowVersion });
        var key = Guid.NewGuid();
        var route = $"/api/lists/{list.Id}/items/{list.Items[0].Id}/marks";
        var results = await Task.WhenAll(buyer.PostAsJsonAsync(route, new CreatePurchaseMarkRequest { Quantity = 2, IdempotencyKey = key }),
            another.PostAsJsonAsync(route, new CreatePurchaseMarkRequest { Quantity = 3, IdempotencyKey = key }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        using var check = factory.Services.CreateScope();
        var marks = await check.ServiceProvider.GetRequiredService<ListHeroDbContext>().ItemPurchases.ToArrayAsync();
        Assert.Single(marks); Assert.Contains(marks[0].Quantity, new[] { 2, 3 });
    }

    private static string IsolatedSqlConnection(string suffix)
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("LISTHERO_TEST_SQL_CONNECTION"));
        Assert.StartsWith("ListHero_Integration_", builder.InitialCatalog);
        builder.InitialCatalog += suffix;
        return builder.ConnectionString;
    }
    private sealed class SaveGate
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public int Arrivals => arrivals;
        public async Task ArriveAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
    }
    // Both real SQL requests must read the original version before either writes its undo.
    private sealed class GatedPurchaseStore(IPurchaseStore inner, SaveGate gate) : IPurchaseStore
    {
        public Task AddGuestAsync(GuestIdentity guest, CancellationToken ct = default) => inner.AddGuestAsync(guest, ct);
        public Task<ItemPurchase?> GetPurchaseAsync(Guid itemId, Guid markId, CancellationToken ct = default) => inner.GetPurchaseAsync(itemId, markId, ct);
        public Task<ItemPurchase?> GetByIdempotencyKeyAsync(Guid itemId, Guid key, CancellationToken ct = default) => inner.GetByIdempotencyKeyAsync(itemId, key, ct);
        public Task<ItemPurchase> AddOrGetAsync(ItemPurchase mark, CancellationToken ct = default) => inner.AddOrGetAsync(mark, ct);
        public async Task SavePurchaseAsync(ItemPurchase mark, byte[] expectedVersion, CancellationToken ct = default)
        { await gate.ArriveAsync(ct); await inner.SavePurchaseAsync(mark, expectedVersion, ct); }
    }
}
