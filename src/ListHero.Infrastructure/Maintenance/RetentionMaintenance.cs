using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace ListHero.Infrastructure.Maintenance;

public sealed record RetentionReport(bool Applied, bool CachePresent, long ExpiredCacheRows, int RemovedCacheRows,
    long RetainedArchivedLists, long RetainedArchivedItems, long RetainedPurchases, long RetainedGuests, long RetainedArchivedShortcuts);
public sealed record TestDatabaseEntry(string Name, DateTime CreatedLocal, bool Owned, bool InUse, bool Eligible);
public sealed record TestDatabaseReport(bool Applied, IReadOnlyList<TestDatabaseEntry> Databases, int Removed);

public interface IRetentionMaintenance
{
    Task<RetentionReport> RunAsync(string connection, string expectedDatabase, bool apply, int batch, CancellationToken ct = default);
    Task<TestDatabaseReport> TestDatabasesAsync(string connection, bool apply, int batch, string? onlyDatabase = null, CancellationToken ct = default);
}

public sealed partial class RetentionMaintenance(TimeProvider clock) : IRetentionMaintenance
{
    public const string TestOwnerProperty = "ListHero.TestDatabase";
    public const string TestOwner = "ListHero.Tests.v1";
    [GeneratedRegex(@"\AListHero_Integration_[a-f0-9]{32}(?:_[A-Za-z0-9_]{1,64})?\z")]
    private static partial Regex TestNamePattern();
    public static bool IsTestDatabase(string name) => TestNamePattern().IsMatch(name);
    public static bool IsLocalDb(string server) => server.Equals(@"(localdb)\MSSQLLocalDB", StringComparison.OrdinalIgnoreCase)
        || server.Equals(@"(localdb)\ListHeroCI", StringComparison.OrdinalIgnoreCase)
        || server.StartsWith(@"np:\\.\pipe\LOCALDB#", StringComparison.OrdinalIgnoreCase) && server.EndsWith(@"\tsql\query", StringComparison.OrdinalIgnoreCase);
    internal static SqlConnectionStringBuilder LocalConnection(string connection)
    {
        var settings = new SqlConnectionStringBuilder(connection);
        if (!IsLocalDb(settings.DataSource) || !settings.IntegratedSecurity) throw new ArgumentException("This operation requires Windows LocalDB with integrated authentication.");
        settings.Pooling = false;
        return settings;
    }

    public async Task<RetentionReport> RunAsync(string connection, string expectedDatabase, bool apply, int batch, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedDatabase) || new SqlConnectionStringBuilder(connection).InitialCatalog != expectedDatabase)
            throw new ArgumentException("Specify the exact target database.");
        if (batch is < 1 or > 1000) throw new ArgumentException("Choose a batch from 1–1000.");
        await using var db = new SqlConnection(connection);
        await db.OpenAsync(ct);
        await using var inspect = db.CreateCommand();
        inspect.CommandText = """
            SELECT (SELECT COUNT_BIG(*) FROM dbo.WishLists WHERE ArchivedAt IS NOT NULL),
                (SELECT COUNT_BIG(*) FROM dbo.WishListItems WHERE ArchivedAt IS NOT NULL),
                (SELECT COUNT_BIG(*) FROM dbo.ItemPurchases), (SELECT COUNT_BIG(*) FROM dbo.GuestIdentities),
                (SELECT COUNT_BIG(*) FROM dbo.SavedLists WHERE ArchivedAt IS NOT NULL),
                CASE WHEN OBJECT_ID(N'cache.TokenCache', N'U') IS NULL THEN 0 ELSE 1 END;
            """;
        long lists, items, purchases, guests, shortcuts; bool cache;
        await using (var reader = await inspect.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            lists = reader.GetInt64(0); items = reader.GetInt64(1); purchases = reader.GetInt64(2);
            guests = reader.GetInt64(3); shortcuts = reader.GetInt64(4); cache = reader.GetInt32(5) == 1;
        }
        long expired = 0; var removed = 0;
        if (cache)
        {
            await using var query = db.CreateCommand();
            query.CommandText = "SELECT COUNT_BIG(*) FROM cache.TokenCache WHERE ExpiresAtTime <= @cutoff";
            query.Parameters.Add("@cutoff", SqlDbType.DateTimeOffset).Value = clock.GetUtcNow().AddHours(-24);
            expired = (long)(await query.ExecuteScalarAsync(ct))!;
            if (apply)
            {
                query.CommandText = """
                    ;WITH expired AS (SELECT TOP (@batch) * FROM cache.TokenCache
                        WHERE ExpiresAtTime <= @cutoff ORDER BY ExpiresAtTime, Id) DELETE FROM expired;
                    """;
                query.Parameters.Add("@batch", SqlDbType.Int).Value = batch;
                removed = await query.ExecuteNonQueryAsync(ct);
            }
        }
        return new(apply, cache, expired, removed, lists, items, purchases, guests, shortcuts);
    }

    public async Task<TestDatabaseReport> TestDatabasesAsync(string connection, bool apply, int batch, string? onlyDatabase = null, CancellationToken ct = default)
    {
        if (batch is < 1 or > 5) throw new ArgumentException("Choose a database batch from 1–5.");
        if (onlyDatabase is not null && !IsTestDatabase(onlyDatabase) || apply && onlyDatabase is null) throw new ArgumentException("Apply requires one exact generated test-database name.");
        var settings = LocalConnection(connection); settings.InitialCatalog = "master";
        await using var db = new SqlConnection(settings.ConnectionString); await db.OpenAsync(ct);
        await using var list = db.CreateCommand();
        list.CommandText = """
            SELECT TOP (500) name, create_date, CASE WHEN EXISTS
                (SELECT 1 FROM sys.dm_exec_sessions s WHERE s.database_id = d.database_id) THEN 1 ELSE 0 END
            FROM sys.databases d WHERE name LIKE N'ListHero[_]Integration[_]%' AND state = 0 AND is_read_only = 0
                AND source_database_id IS NULL AND (@name IS NULL OR name = @name) ORDER BY create_date, name;
            """;
        list.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = (object?)onlyDatabase ?? DBNull.Value;
        var candidates = new List<(string Name, DateTime Created, bool InUse)>();
        await using (var reader = await list.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (IsTestDatabase(reader.GetString(0))) candidates.Add((reader.GetString(0), reader.GetDateTime(1), reader.GetInt32(2) == 1));
        var cutoff = clock.GetLocalNow().DateTime.AddDays(-14);
        var entries = new List<TestDatabaseEntry>(); var removed = 0;
        foreach (var candidate in candidates)
        {
            var name = candidate.Name; // Strict generated-name validation above makes identifier quoting safe.
            await using var owner = db.CreateCommand();
            owner.CommandText = $"SELECT CONVERT(nvarchar(100), value) FROM [{name}].sys.extended_properties WHERE class = 0 AND name = @property";
            owner.Parameters.AddWithValue("@property", TestOwnerProperty);
            var owned = (string?)await owner.ExecuteScalarAsync(ct) == TestOwner;
            var eligible = owned && !candidate.InUse && candidate.Created <= cutoff;
            entries.Add(new(name, candidate.Created, owned, candidate.InUse, eligible));
            if (!apply || !eligible || removed >= batch) continue;
            await using var drop = db.CreateCommand(); drop.CommandTimeout = 5;
            drop.CommandText = $"""
                IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @name AND create_date = @created AND create_date <= @cutoff AND state = 0 AND is_read_only = 0 AND source_database_id IS NULL)
                    AND NOT EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE database_id = DB_ID(@name))
                    AND (SELECT CONVERT(nvarchar(100), value) FROM [{name}].sys.extended_properties WHERE class = 0 AND name = @property) = @owner
                BEGIN DROP DATABASE [{name}]; SELECT 1; END ELSE SELECT 0;
                """;
            drop.Parameters.AddWithValue("@name", name); drop.Parameters.Add("@created", SqlDbType.DateTime).Value = candidate.Created;
            drop.Parameters.Add("@cutoff", SqlDbType.DateTime).Value = cutoff;
            drop.Parameters.AddWithValue("@property", TestOwnerProperty); drop.Parameters.AddWithValue("@owner", TestOwner);
            // Never force SINGLE_USER/ROLLBACK: a newly connected user must cause DROP to fail safely.
            try { removed += Convert.ToInt32(await drop.ExecuteScalarAsync(ct)); }
            catch (SqlException exception) when (exception.Number is 3702 or -2) { /* In use or lock timeout: retain it. */ }
        }
        return new(apply, entries, removed);
    }
}
