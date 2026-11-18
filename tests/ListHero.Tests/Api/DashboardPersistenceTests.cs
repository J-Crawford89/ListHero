using System.Data.Common;
using ListHero.Application.Common;
using ListHero.Domain.Entities;
using ListHero.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ListHero.Tests.Api;

public sealed partial class OwnerWorkflowTests
{
    [SqlServerFact]
    public async Task Dashboard_preview_is_bounded_and_projected_in_SQL_with_stable_order_and_owner_filtering()
    {
        var fixture = await CreateSavedListFixtureAsync("_Preview");
        await using (var seed = fixture.Open())
        {
            for (var index = 1; index <= 8; index++)
                seed.WishLists.Add(new(fixture.OwnerId, $"Wish {index}", "Description", fixture.Now.AddMinutes(index)));
            var archived = new WishList(fixture.OwnerId, "Archived", "", fixture.Now.AddHours(1));
            archived.Archive(fixture.Now.AddHours(2));
            seed.WishLists.Add(archived);
            seed.WishLists.Add(new(fixture.BuyerId, "Someone else's list", "", fixture.Now.AddHours(1)));
            await seed.SaveChangesAsync();
        }
        var commands = new ReadCommandCapture();
        await using var db = fixture.Open(commands);
        var preview = await new EfWishListStore(db).GetOwnerPreviewAsync(fixture.OwnerId);
        Assert.Equal(new[] { "Wish 8", "Wish 7", "Wish 6", "Wish 5", "Wish 4" }, preview.Select(list => list.Name));
        Assert.All(preview, list => Assert.Equal("Description", list.Description));
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Contains("TOP(", commands.Sql);
        Assert.Contains(5, commands.Parameters);
        Assert.Contains("ORDER BY", commands.Sql);
        Assert.Contains("[CreatedAt] DESC", commands.Sql);
        Assert.Contains("[Id]", commands.Sql);
        Assert.DoesNotContain("[RowVersion]", commands.Sql);
        Assert.Equal(9, (await new EfWishListStore(db).GetByOwnerAsync(fixture.OwnerId)).Count);
    }

    [SqlServerFact]
    public async Task Concurrent_first_saves_merge_a_pin_and_new_access_into_one_SQL_shortcut()
    {
        var fixture = await CreateSavedListFixtureAsync("_SaveRace");
        var gate = new SaveGate();
        var inserts = new SavedInsertInterceptor(async (_, ct) => await gate.ArriveAsync(ct));
        await using var pinContext = fixture.Open(inserts);
        await using var accessContext = fixture.Open(inserts);
        await Task.WhenAll(
            new EfSavedListStore(pinContext).SaveAsync(fixture.BuyerId, fixture.ListId, null, true, fixture.Now, CancellationToken.None),
            new EfSavedListStore(accessContext).SaveAsync(fixture.BuyerId, fixture.ListId, fixture.ShareId, null, fixture.Now, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, inserts.Attempts);
        await using var verify = fixture.Open();
        var saved = Assert.Single(await verify.SavedLists.AsNoTracking().ToArrayAsync());
        Assert.True(saved.IsPinned);
        Assert.Equal(fixture.ShareId, saved.ShareLinkId);
        Assert.Null(saved.ArchivedAt);
        Assert.DoesNotContain(pinContext.ChangeTracker.Entries<SavedList>(), entry => entry.State == EntityState.Added);
        Assert.DoesNotContain(accessContext.ChangeTracker.Entries<SavedList>(), entry => entry.State == EntityState.Added);

        // Explicit preference updates remain last-write-wins and preserve the current grant.
        await new EfSavedListStore(pinContext).SaveAsync(fixture.BuyerId, fixture.ListId, null, false, fixture.Now, CancellationToken.None);
        saved = await verify.SavedLists.AsNoTracking().SingleAsync();
        Assert.False(saved.IsPinned);
        Assert.Equal(fixture.ShareId, saved.ShareLinkId);
    }

    [SqlServerFact]
    public async Task Duplicate_save_recovery_stops_if_the_winning_shortcut_was_removed()
    {
        var fixture = await CreateSavedListFixtureAsync("_SaveRemoved");
        var inserts = new SavedInsertInterceptor(async (_, _) =>
        {
            await using var winner = fixture.Open();
            winner.SavedLists.Add(new(fixture.BuyerId, fixture.ListId, fixture.ShareId, false, fixture.Now));
            await winner.SaveChangesAsync();
        }, async () =>
        {
            await using var remover = fixture.Open();
            await new EfSavedListStore(remover).RemoveAsync(fixture.BuyerId, fixture.ListId, fixture.Now, CancellationToken.None);
        });
        await using var db = fixture.Open(inserts);
        await Assert.ThrowsAsync<EditConflictException>(() => new EfSavedListStore(db)
            .SaveAsync(fixture.BuyerId, fixture.ListId, null, true, fixture.Now, CancellationToken.None));
        Assert.Equal(1, inserts.Attempts);
        Assert.Empty(db.ChangeTracker.Entries<SavedList>());
        await using var verify = fixture.Open();
        Assert.NotNull((await verify.SavedLists.SingleAsync()).ArchivedAt);
    }

    private static async Task<SavedListFixture> CreateSavedListFixtureAsync(string suffix)
    {
        var connection = IsolatedSqlConnection(suffix);
        var now = DateTimeOffset.UtcNow;
        var owner = new User("https://test", Guid.NewGuid().ToString(), "Owner", now);
        var buyer = new User("https://test", Guid.NewGuid().ToString(), "Buyer", now);
        var list = new WishList(owner.Id, "Wishes", "", now);
        var share = new ListShareLink(list.Id, Guid.NewGuid().ToString("N"), "protected-token", now);
        var fixture = new SavedListFixture(connection, owner.Id, buyer.Id, list.Id, share.Id, now);
        await using var db = fixture.Open();
        await db.Database.MigrateAsync();
        db.AddRange(owner, buyer, list, share);
        await db.SaveChangesAsync();
        return fixture;
    }

    private sealed record SavedListFixture(string Connection, Guid OwnerId, Guid BuyerId, Guid ListId, Guid ShareId, DateTimeOffset Now)
    {
        public ListHeroDbContext Open(params IInterceptor[] interceptors)
            => new(new DbContextOptionsBuilder<ListHeroDbContext>().UseSqlServer(Connection).AddInterceptors(interceptors).Options);
    }

    private sealed class SavedInsertInterceptor(Func<ListHeroDbContext, CancellationToken, Task> beforeInsert,
        Func<Task>? afterFailure = null) : SaveChangesInterceptor
    {
        private int attempts;
        public int Attempts => attempts;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = (ListHeroDbContext)eventData.Context!;
            if (db.ChangeTracker.Entries<SavedList>().Any(entry => entry.State == EntityState.Added))
            {
                Interlocked.Increment(ref attempts);
                await beforeInsert(db, cancellationToken);
            }
            return result;
        }
        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
            => afterFailure?.Invoke() ?? Task.CompletedTask;
    }

    private sealed class ReadCommandCapture : DbCommandInterceptor
    {
        public string Sql { get; private set; } = "";
        public object?[] Parameters { get; private set; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Sql = command.CommandText;
            Parameters = command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).ToArray();
            return ValueTask.FromResult(result);
        }
    }
}
