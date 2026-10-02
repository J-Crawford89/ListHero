using ListHero.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

if (args.Length > 1 || args.Length == 1 && args[0] != "--migrate-only")
    throw new ArgumentException("The only supported option is --migrate-only.");
var migrateOnly = args.Length == 1;
var connectionString = Environment.GetEnvironmentVariable("LISTHERO_DEPLOY_ADMIN_CONNECTION")
    ?? throw new InvalidOperationException("The deployment connection is required.");
var connectionSettings = new SqlConnectionStringBuilder(connectionString);
if (connectionSettings.InitialCatalog != "ListHeroBeta")
    throw new InvalidOperationException("This deployment helper only targets the ListHeroBeta database.");
if (migrateOnly && connectionSettings.DataSource != "tcp:sql-listhero-yj3pk6oimchdi.database.windows.net,1433")
    throw new InvalidOperationException("CI migrations only target the existing beta SQL server.");
var apiPassword = Environment.GetEnvironmentVariable("LISTHERO_DEPLOY_API_PASSWORD");
var cachePassword = Environment.GetEnvironmentVariable("LISTHERO_DEPLOY_CACHE_PASSWORD");
if (!migrateOnly && (string.IsNullOrEmpty(apiPassword) || string.IsNullOrEmpty(cachePassword)))
    throw new InvalidOperationException("Both runtime database credentials are required for initial bootstrap.");
await using (var database = new ListHeroDbContext(new DbContextOptionsBuilder<ListHeroDbContext>()
    .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(6, TimeSpan.FromSeconds(10), null)).Options))
    await database.Database.MigrateAsync();
if (migrateOnly)
{
    Console.WriteLine("Beta database migrations completed; existing runtime users and credentials were preserved.");
    return;
}
await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();
await using var command = connection.CreateCommand();
command.CommandTimeout = 120;
command.CommandText = """
    IF SCHEMA_ID(N'cache') IS NULL EXEC(N'CREATE SCHEMA [cache]');
    IF OBJECT_ID(N'cache.TokenCache', N'U') IS NULL
    BEGIN
        CREATE TABLE [cache].[TokenCache](
            [Id] nvarchar(449) COLLATE SQL_Latin1_General_CP1_CS_AS NOT NULL PRIMARY KEY,
            [Value] varbinary(max) NOT NULL,
            [ExpiresAtTime] datetimeoffset(7) NOT NULL,
            [SlidingExpirationInSeconds] bigint NULL,
            [AbsoluteExpiration] datetimeoffset(7) NULL);
        CREATE NONCLUSTERED INDEX [IX_TokenCache_ExpiresAtTime] ON [cache].[TokenCache]([ExpiresAtTime]);
    END;
    IF USER_ID(N'ListHeroApi') IS NULL
    BEGIN
        DECLARE @apiCommand nvarchar(max) = N'CREATE USER [ListHeroApi] WITH PASSWORD = ' + QUOTENAME(@apiPassword, '''');
        EXEC sp_executesql @apiCommand;
    END;
    IF USER_ID(N'ListHeroCache') IS NULL
    BEGIN
        DECLARE @cacheCommand nvarchar(max) = N'CREATE USER [ListHeroCache] WITH PASSWORD = ' + QUOTENAME(@cachePassword, '''');
        EXEC sp_executesql @cacheCommand;
    END;
    GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO [ListHeroApi];
    GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::[cache].[TokenCache] TO [ListHeroCache];
    """;
command.Parameters.AddWithValue("@apiPassword", apiPassword!);
command.Parameters.AddWithValue("@cachePassword", cachePassword!);
await command.ExecuteNonQueryAsync();
Console.WriteLine("Database migrations, encrypted-token cache table, and separate runtime users are ready.");
