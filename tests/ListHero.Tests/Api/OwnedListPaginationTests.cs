using System.Data.SqlTypes;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ListHero.Client.Api;
using ListHero.Contracts.Lists;
using ListHero.Domain.Entities;
using ListHero.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.Api;

public sealed partial class OwnerWorkflowTests
{
    [Theory]
    [InlineData("page=0")] [InlineData("page=-1")] [InlineData("page=1001")]
    [InlineData("page=abc")] [InlineData("page=")] [InlineData("page=1&page=2")]
    [InlineData("page=2147483648")] [InlineData("pageSize=0")] [InlineData("pageSize=-1")]
    [InlineData("pageSize=101")] [InlineData("pageSize=abc")] [InlineData("pageSize=")]
    [InlineData("pageSize=15&pageSize=30")] [InlineData("pageSize=1.5")]
    public async Task Invalid_owned_list_paging_is_rejected_before_provisioning_or_reading_data(string query)
    {
        await using var factory = new OwnerApiFactory();
        using var owner = SignedInClient(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/lists/mine?" + query)).StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<MemoryUserStore>().Users);
    }

    [Fact]
    public async Task Owned_list_paging_accepts_bounds_and_returns_recoverable_empty_pages()
    {
        await using var factory = new OwnerApiFactory(); using var owner = SignedInClient(factory);
        var list = await CreateListAsync(owner);
        var api = new WishListApiClient(owner, new TestTokenProvider());
        var smallest = await api.GetMineAsync(1, 1);
        Assert.Equal(list.Id, Assert.Single(smallest.Items).Id); Assert.False(smallest.HasMore);
        var empty = await api.GetMineAsync(1000, 100);
        Assert.Empty(empty.Items); Assert.Equal(1000, empty.Page); Assert.Equal(100, empty.PageSize); Assert.False(empty.HasMore);
        Assert.Contains("no-store", (await owner.GetAsync("/api/lists/mine")).Headers.CacheControl!.ToString());
    }

    [SqlServerFact]
    public async Task My_lists_SQL_pages_are_bounded_stable_projected_and_isolated_with_an_exact_last_page()
    {
        var connection = IsolatedSqlConnection("_OwnedPages");
        await using var factory = new OwnerApiFactory(connection);
        using (var scope = factory.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.MigrateAsync();
        using var owner = SignedInClient(factory); using var other = SignedInClient(factory);
        var first = await CreateListAsync(owner); await CreateListAsync(other);
        Guid ownerId; Guid[] expected;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ListHeroDbContext>();
            var original = await db.WishLists.SingleAsync(list => list.Id == first.Id); ownerId = original.OwnerId;
            var tied = Enumerable.Range(1, 104).Select(index => new WishList(ownerId, $"Wish {index}", "Summary only", original.CreatedAt.AddDays(1))).ToArray();
            var archived = new WishList(ownerId, "Archived should never appear", "", original.CreatedAt.AddDays(2)); archived.Archive(DateTimeOffset.UtcNow);
            db.AddRange(tied); db.Add(archived); await db.SaveChangesAsync();
            expected = tied.OrderBy(list => new SqlGuid(list.Id)).Select(list => list.Id).Append(first.Id).ToArray();
        }
        var api = new WishListApiClient(owner, new TestTokenProvider());
        var defaults = await api.GetMineAsync();
        Assert.Equal(1, defaults.Page); Assert.Equal(15, defaults.PageSize); Assert.Equal(15, defaults.Items.Count); Assert.True(defaults.HasMore);
        Assert.Equal(expected.Take(15), defaults.Items.Select(list => list.Id));
        Assert.Equal(defaults.Items.Select(list => list.Id), (await api.GetMineAsync()).Items.Select(list => list.Id));
        var collected = new List<Guid>();
        for (var page = 1; page <= 7; page++)
        {
            var result = await api.GetMineAsync(page);
            Assert.Equal(15, result.Items.Count); Assert.Equal(page < 7, result.HasMore);
            collected.AddRange(result.Items.Select(list => list.Id));
        }
        Assert.Equal(expected, collected); Assert.Equal(105, collected.Distinct().Count());
        Assert.Empty((await api.GetMineAsync(8)).Items);
        Assert.Equal(100, (await api.GetMineAsync(1, 100)).Items.Count);
        var tail = await api.GetMineAsync(2, 100); Assert.Equal(5, tail.Items.Count); Assert.False(tail.HasMore);
        var commands = new ReadCommandCapture();
        await using var readDb = new ListHeroDbContext(new DbContextOptionsBuilder<ListHeroDbContext>().UseSqlServer(connection).AddInterceptors(commands).Options);
        var projected = await new EfWishListStore(readDb).GetByOwnerAsync(ownerId, 2, 15);
        Assert.Equal(expected.Skip(15).Take(15), projected.Items.Select(list => list.Id));
        Assert.Contains("OFFSET", commands.Sql); Assert.Contains("FETCH NEXT", commands.Sql);
        Assert.Contains(15, commands.Parameters); Assert.Contains(16, commands.Parameters);
        Assert.DoesNotContain("[RowVersion]", commands.Sql); Assert.Empty(readDb.ChangeTracker.Entries());
        var json = JsonSerializer.Serialize(defaults);
        foreach (var hidden in new[] { "OwnerId", "RowVersion", "Purchased", "Token", "Archived should never appear" }) Assert.DoesNotContain(hidden, json);
    }
}
