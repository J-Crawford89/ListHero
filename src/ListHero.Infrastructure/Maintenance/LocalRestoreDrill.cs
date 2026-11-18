using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ListHero.Domain.Entities;
using ListHero.Infrastructure.Persistence;
using ListHero.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Infrastructure.Maintenance;

public sealed record RestoreDrillReport(bool BackupVerified, bool IntegrityVerified, bool DataVerified,
    bool ShareCredentialVerified, bool GuestCredentialVerified, bool WebCookieVerified, bool CacheProbeVerified,
    bool TemporaryDatabasesRemoved);

public static class LocalRestoreDrill
{
    public static async Task<RestoreDrillReport> RunAsync(string connection, string output, CancellationToken ct = default)
    {
        var settings = RetentionMaintenance.LocalConnection(connection); settings.InitialCatalog = "master";
        output = Path.GetFullPath(output); KeyRingBackup.NewPrivateDirectory(output);
        var run = Guid.NewGuid().ToString("N"); var source = "ListHero_RestoreDrill_" + run + "_Source"; var target = "ListHero_RestoreDrill_" + run + "_Restored";
        await File.WriteAllTextAsync(Path.Combine(output, "drill.json"), JsonSerializer.Serialize(new { Run = run, Source = source, Restored = target }), ct);
        await using var master = new SqlConnection(settings.ConnectionString); await master.OpenAsync(ct);
        var success = false; var removed = false;
        try
        {
            var sourceData = Literal(Path.Combine(output, "source.mdf")); var sourceLog = Literal(Path.Combine(output, "source.ldf"));
            await Command(master, $"CREATE DATABASE [{source}] ON PRIMARY (NAME=N'{source}', FILENAME=N'{sourceData}') LOG ON (NAME=N'{source}_log', FILENAME=N'{sourceLog}')", ct);
            var sourceSettings = new SqlConnectionStringBuilder(settings.ConnectionString) { InitialCatalog = source };
            await using (var owned = new SqlConnection(sourceSettings.ConnectionString))
            {
                await owned.OpenAsync(ct); await Command(owned, $"EXEC sys.sp_addextendedproperty @name=N'ListHero.RestoreDrill', @value=N'{run}'", ct);
            }
            var apiRing = Path.Combine(output, "api-source-keys"); var webRing = Path.Combine(output, "web-source-keys");
            using var rsa = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=ListHero synthetic restore drill", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(1));
            using var api = Protection(apiRing, "ListHero.Api", certificate);
            using var web = Protection(webRing, "ListHero.Web", certificate);
            var tokens = new CapabilityTokenService(api.GetRequiredService<IDataProtectionProvider>());
            var share = tokens.Issue(); var guest = tokens.Issue(); var now = DateTimeOffset.UtcNow;
            var cookieProtector = web.GetRequiredService<IDataProtectionProvider>().CreateProtector("Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware", "Cookies", "v2");
            var cookie = new TicketDataFormat(cookieProtector).Protect(new(new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "synthetic-owner")], "Cookies")), new(), "Cookies"));
            var cacheProtector = web.GetRequiredService<IDataProtectionProvider>().CreateProtector("ListHero.RestoreDrill.CacheProbe.v1");
            var cache = cacheProtector.Protect("synthetic-cache-probe");
            await using (var db = Open(sourceSettings.ConnectionString))
            {
                await db.Database.MigrateAsync(ct);
                var owner = new User("https://synthetic.invalid", run, "Synthetic owner", now);
                var list = new WishList(owner.Id, "Synthetic private wishes", "Restore verification", now);
                var item = new WishListItem(list.Id, "Synthetic gift", "", now, desiredQuantity: 4);
                var archived = new WishListItem(list.Id, "Archived gift", "", now); archived.Archive(now);
                var buyer = new GuestIdentity(guest.Hash, now);
                var link = new ListShareLink(list.Id, share.Hash, tokens.ProtectShareToken(share.Token), now);
                var mark = new ItemPurchase(item.Id, 2, null, buyer.Id, Guid.NewGuid(), now);
                var shortcut = new SavedList(owner.Id, list.Id, link.Id, true, now); shortcut.Archive(now);
                db.AddRange(owner, list, item, archived, buyer, link, mark, shortcut); await db.SaveChangesAsync(ct);
                await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA cache", ct);
                await db.Database.ExecuteSqlRawAsync("CREATE TABLE cache.TokenCache (Id nvarchar(449) NOT NULL PRIMARY KEY, Value varbinary(max) NOT NULL, ExpiresAtTime datetimeoffset NOT NULL, SlidingExpirationInSeconds bigint NULL, AbsoluteExpiration datetimeoffset NULL)", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT cache.TokenCache(Id,Value,ExpiresAtTime) VALUES(N'synthetic-probe',{Encoding.UTF8.GetBytes(cache)},{now.AddDays(30)})", ct);
            }
            string before; await using (var db = Open(sourceSettings.ConnectionString)) before = await Snapshot(db, ct);
            var backup = Path.Combine(output, "synthetic.bak");
            await Command(master, $"BACKUP DATABASE [{source}] TO DISK=N'{Literal(backup)}' WITH COPY_ONLY,CHECKSUM", ct);
            await Command(master, $"RESTORE VERIFYONLY FROM DISK=N'{Literal(backup)}' WITH CHECKSUM", ct);
            await Command(master, $"RESTORE DATABASE [{target}] FROM DISK=N'{Literal(backup)}' WITH CHECKSUM, MOVE N'{source}' TO N'{Literal(Path.Combine(output, "restored.mdf"))}', MOVE N'{source}_log' TO N'{Literal(Path.Combine(output, "restored.ldf"))}'", ct);
            await Command(master, $"DBCC CHECKDB([{target}]) WITH NO_INFOMSGS", ct);
            var restoredSettings = new SqlConnectionStringBuilder(settings.ConnectionString) { InitialCatalog = target };
            await using var restored = Open(restoredSettings.ConnectionString);
            if (before != await Snapshot(restored, ct)) throw new InvalidDataException("Restored records do not match the source.");
            var password = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            var apiBackup = Path.Combine(output, "api-backup"); var webBackup = Path.Combine(output, "web-backup");
            KeyRingBackup.Export(apiRing, apiBackup, "ListHero.Api", password, certificate);
            KeyRingBackup.Export(webRing, webBackup, "ListHero.Web", password, certificate);
            var apiRestored = Path.Combine(output, "api-restored"); var webRestored = Path.Combine(output, "web-restored");
            KeyRingBackup.Restore(Path.Combine(apiBackup, "keys.lhb"), apiRestored, "ListHero.Api", password);
            KeyRingBackup.Restore(Path.Combine(webBackup, "keys.lhb"), webRestored, "ListHero.Web", password);
            using var restoredCertificate = X509Certificate2.CreateFromEncryptedPemFile(Path.Combine(apiRestored, "certificate.pem"), password);
            using var recoveredApi = Protection(Path.Combine(apiRestored, "keyring"), "ListHero.Api", restoredCertificate);
            using var recoveredWeb = Protection(Path.Combine(webRestored, "keyring"), "ListHero.Web", restoredCertificate);
            var recoveredTokens = new CapabilityTokenService(recoveredApi.GetRequiredService<IDataProtectionProvider>());
            if (recoveredTokens.UnprotectShareToken((await restored.ListShareLinks.SingleAsync(ct)).ProtectedToken) != share.Token
                || !recoveredTokens.Matches(guest.Token, (await restored.GuestIdentities.SingleAsync(ct)).CredentialHash)) throw new CryptographicException("Credential recovery failed.");
            var recoveredProvider = recoveredWeb.GetRequiredService<IDataProtectionProvider>();
            var ticket = new TicketDataFormat(recoveredProvider.CreateProtector("Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware", "Cookies", "v2")).Unprotect(cookie);
            if (ticket?.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value != "synthetic-owner") throw new CryptographicException("Cookie recovery failed.");
            await restored.Database.CloseConnectionAsync();
            await using (var cacheDb = new SqlConnection(restoredSettings.ConnectionString))
            {
                await cacheDb.OpenAsync(ct); await using var read = cacheDb.CreateCommand(); read.CommandText = "SELECT Value FROM cache.TokenCache WHERE Id=N'synthetic-probe'";
                var cipher = Encoding.UTF8.GetString((byte[])(await read.ExecuteScalarAsync(ct))!);
                if (recoveredProvider.CreateProtector("ListHero.RestoreDrill.CacheProbe.v1").Unprotect(cipher) != "synthetic-cache-probe") throw new CryptographicException("Cache probe recovery failed.");
            }
            success = true;
        }
        finally
        {
            // Only databases with this run's unpredictable name AND ownership marker can be removed.
            var targetRemoved = await RemoveOwned(master, target, run, output);
            var sourceRemoved = await RemoveOwned(master, source, run, output);
            removed = targetRemoved && sourceRemoved;
        }
        return new(success, success, success, success, success, success, success, removed);
    }
    private static string Literal(string value) => value.Replace("'", "''");
    private static ListHeroDbContext Open(string connection) => new(new DbContextOptionsBuilder<ListHeroDbContext>().UseSqlServer(connection).Options);
    internal static ServiceProvider Protection(string directory, string application, X509Certificate2 certificate)
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName(application).PersistKeysToFileSystem(new(directory))
            .ProtectKeysWithCertificate(certificate).UnprotectKeysWithAnyCertificate(certificate);
        return services.BuildServiceProvider();
    }
    private static async Task<string> Snapshot(ListHeroDbContext db, CancellationToken ct) => JsonSerializer.Serialize(new
    {
        Users = await db.Users.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(ct),
        Lists = await db.WishLists.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(ct),
        Items = await db.WishListItems.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(ct),
        Marks = await db.ItemPurchases.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(ct),
        Guests = await db.GuestIdentities.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(ct),
        Links = await db.ListShareLinks.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(ct),
        Saved = await db.SavedLists.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(ct)
    });
    private static async Task Command(SqlConnection db, string sql, CancellationToken ct)
    {
        await using var command = db.CreateCommand(); command.CommandTimeout = 120; command.CommandText = sql; await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task<bool> RemoveOwned(SqlConnection master, string name, string run, string directory)
    {
        if (!Guid.TryParseExact(run, "N", out _) || name != "ListHero_RestoreDrill_" + run + "_Source" && name != "ListHero_RestoreDrill_" + run + "_Restored") return false;
        await using var command = master.CreateCommand(); command.CommandTimeout = 10;
        command.CommandText = $"""
            IF DB_ID(N'{name}') IS NULL SELECT 1 ELSE BEGIN
                IF (SELECT CONVERT(nvarchar(100), value) FROM [{name}].sys.extended_properties WHERE class=0 AND name=N'ListHero.RestoreDrill')=N'{run}'
                    AND NOT EXISTS (SELECT 1 FROM sys.master_files WHERE database_id=DB_ID(N'{name}') AND LEFT(physical_name,LEN(@root))<>@root)
                BEGIN DROP DATABASE [{name}]; SELECT 1; END ELSE SELECT 0;
            END
            """;
        command.Parameters.AddWithValue("@root", Path.GetFullPath(directory) + Path.DirectorySeparatorChar);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }
}
