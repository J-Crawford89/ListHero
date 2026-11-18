using ListHero.Domain.Entities;
using ListHero.Infrastructure.Maintenance;
using ListHero.Infrastructure.Persistence;
using ListHero.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ListHero.Tests.Api;

public sealed partial class OwnerWorkflowTests
{
    [SqlServerFact]
    public async Task Retention_only_removes_old_expired_cache_rows_and_never_archived_business_history()
    {
        var fixture = await CreateSavedListFixtureAsync("_Retention");
        var clock = new RetentionClock { Now = fixture.Now };
        var operation = new RetentionMaintenance(clock);
        var empty = await operation.RunAsync(fixture.Connection, new SqlConnectionStringBuilder(fixture.Connection).InitialCatalog, false, 1);
        Assert.False(empty.CachePresent);
        await using (var seed = fixture.Open())
        {
            var list = await seed.WishLists.SingleAsync(); list.Archive(fixture.Now);
            var item = new WishListItem(list.Id, "Archived synthetic wish", "", fixture.Now); item.Archive(fixture.Now);
            var guest = new GuestIdentity(Guid.NewGuid().ToString("N"), fixture.Now.AddYears(-2));
            var orphan = new GuestIdentity(Guid.NewGuid().ToString("N"), fixture.Now.AddYears(-2));
            var mark = new ItemPurchase(item.Id, 1, null, guest.Id, Guid.NewGuid(), fixture.Now); mark.Undo(fixture.Now);
            var saved = new SavedList(fixture.OwnerId, list.Id, fixture.ShareId, true, fixture.Now); saved.Archive(fixture.Now);
            seed.AddRange(item, guest, orphan, mark, saved); await seed.SaveChangesAsync();
            await seed.Database.ExecuteSqlRawAsync("CREATE SCHEMA cache");
            await seed.Database.ExecuteSqlRawAsync("CREATE TABLE cache.TokenCache (Id nvarchar(449) NOT NULL PRIMARY KEY, Value varbinary(max) NOT NULL, ExpiresAtTime datetimeoffset NOT NULL)");
            foreach (var (name, expiry) in new[] { ("old", fixture.Now.AddDays(-2)), ("also-old", fixture.Now.AddDays(-3)), ("recently-expired", fixture.Now.AddHours(-12)), ("live", fixture.Now.AddDays(1)) })
                await seed.Database.ExecuteSqlInterpolatedAsync($"INSERT cache.TokenCache VALUES({name},{new byte[] { 1, 2 }},{expiry})");
        }
        var database = new SqlConnectionStringBuilder(fixture.Connection).InitialCatalog;
        var preview = await operation.RunAsync(fixture.Connection, database, false, 1);
        Assert.Equal(2, preview.ExpiredCacheRows); Assert.Equal(0, preview.RemovedCacheRows);
        Assert.Equal(1, preview.RetainedArchivedLists); Assert.Equal(1, preview.RetainedArchivedItems);
        Assert.Equal(1, preview.RetainedPurchases); Assert.Equal(2, preview.RetainedGuests); Assert.Equal(1, preview.RetainedArchivedShortcuts);
        var applied = await operation.RunAsync(fixture.Connection, database, true, 1);
        Assert.Equal(1, applied.RemovedCacheRows);
        Assert.Equal(1, (await operation.RunAsync(fixture.Connection, database, false, 1)).ExpiredCacheRows);
        Assert.Equal(1, (await operation.RunAsync(fixture.Connection, database, true, 1)).RemovedCacheRows);
        Assert.Equal(0, (await operation.RunAsync(fixture.Connection, database, true, 1)).RemovedCacheRows);
        await using var verify = fixture.Open();
        Assert.Equal(2, await verify.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM cache.TokenCache").SingleAsync());
        Assert.Equal(2, await verify.GuestIdentities.CountAsync()); Assert.Single(await verify.ItemPurchases.ToArrayAsync());
        Assert.NotNull((await verify.WishListItems.SingleAsync()).ArchivedAt); Assert.NotNull((await verify.SavedLists.SingleAsync()).ArchivedAt);
    }

    [SqlServerFact]
    public async Task Test_database_cleanup_requires_ownership_age_an_exact_target_and_no_connections()
    {
        var fixture = await CreateSavedListFixtureAsync("_RetentionDrop", pooling: false);
        var database = new SqlConnectionStringBuilder(fixture.Connection).InitialCatalog;
        var current = new RetentionMaintenance(TimeProvider.System);
        Assert.False(Assert.Single((await current.TestDatabasesAsync(fixture.Connection, false, 1, database)).Databases).Eligible);
        var future = new RetentionMaintenance(new RetentionClock { Now = DateTimeOffset.UtcNow.AddDays(30) });
        using (var pooled = new SqlConnection(fixture.Connection)) SqlConnection.ClearPool(pooled);
        await using (var active = new SqlConnection(new SqlConnectionStringBuilder(fixture.Connection) { Pooling = false }.ConnectionString))
        {
            await active.OpenAsync();
            var held = await future.TestDatabasesAsync(fixture.Connection, true, 1, database);
            Assert.True(Assert.Single(held.Databases).Owned); Assert.True(held.Databases[0].InUse); Assert.Equal(0, held.Removed);
        }
        var preview = await WaitForDisconnect(future, fixture.Connection, database);
        Assert.True(Assert.Single(preview.Databases).Eligible, System.Text.Json.JsonSerializer.Serialize(preview)); Assert.Equal(0, preview.Removed);
        var removed = await future.TestDatabasesAsync(fixture.Connection, true, 1, database);
        Assert.Equal(1, removed.Removed);
        Assert.Empty((await future.TestDatabasesAsync(fixture.Connection, false, 1, database)).Databases);
    }

    [SqlServerFact]
    public async Task Unmarked_legacy_test_databases_are_never_adopted_or_dropped()
    {
        var settings = new SqlConnectionStringBuilder(IsolatedSqlConnection("_Legacy")) { Pooling = false };
        var name = settings.InitialCatalog;
        settings.InitialCatalog = "master";
        await using var master = new SqlConnection(settings.ConnectionString); await master.OpenAsync();
        await using var command = master.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{name}]"; await command.ExecuteNonQueryAsync();
        try
        {
            var operation = new RetentionMaintenance(new RetentionClock { Now = DateTimeOffset.UtcNow.AddDays(30) });
            var result = await operation.TestDatabasesAsync(settings.ConnectionString, true, 1, name);
            Assert.False(Assert.Single(result.Databases).Owned); Assert.False(result.Databases[0].Eligible);
            Assert.Equal(0, result.Removed);
        }
        finally
        {
            // Only the fresh, unmarked database created immediately above; never an existing database.
            command.CommandText = $"DROP DATABASE [{name}]"; await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<TestDatabaseReport> WaitForDisconnect(RetentionMaintenance operation, string connection, string database)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        TestDatabaseReport report;
        do
        {
            report = await operation.TestDatabasesAsync(connection, false, 1, database);
            if (!Assert.Single(report.Databases).InUse) return report;
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);
        return report;
    }

    [SqlServerFact]
    public async Task Native_local_restore_drill_recovers_database_history_keys_credentials_and_cookie_protection()
    {
        using var output = new StringWriter();
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["restore-drill", "--output", KeyRingBackupTests.NewPath()],
            name => name == "LISTHERO_MAINTENANCE_CONNECTION" ? IsolatedSqlConnection("_RestoreDriver") : null, output));
        var report = System.Text.Json.JsonSerializer.Deserialize<RestoreDrillReport>(output.ToString())!;
        Assert.True(report.BackupVerified); Assert.True(report.IntegrityVerified); Assert.True(report.DataVerified);
        Assert.True(report.ShareCredentialVerified); Assert.True(report.GuestCredentialVerified);
        Assert.True(report.WebCookieVerified); Assert.True(report.CacheProbeVerified); Assert.True(report.TemporaryDatabasesRemoved);
    }
    private sealed class RetentionClock : TimeProvider
    {
        public DateTimeOffset Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
