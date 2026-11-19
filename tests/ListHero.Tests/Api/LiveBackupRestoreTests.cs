using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ListHero.Domain.Entities;
using ListHero.Infrastructure.Persistence;
using ListHero.LiveBackup;
using ListHero.Tests.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SqlServer.Dac;

namespace ListHero.Tests.Api;

public sealed partial class OwnerWorkflowTests
{
    [SqlServerFact]
    public async Task Portable_capture_restores_real_export_history_cache_and_both_original_key_rings_offline()
    {
        var fixture = await CreateSavedListFixtureAsync("_PortableBackup");
        using var source = new SyntheticBackupSource();
        var guest = new GuestIdentity("original-guest-hash-canary", fixture.Now);
        var item = new WishListItem(fixture.ListId, "Portable synthetic wish", "", fixture.Now); item.Archive(fixture.Now);
        var mark = new ItemPurchase(item.Id, 6, null, guest.Id, Guid.NewGuid(), fixture.Now); mark.Undo(fixture.Now);
        var saved = new SavedList(fixture.BuyerId, fixture.ListId, fixture.ShareId, true, fixture.Now); saved.Archive(fixture.Now);
        await using (var seed = fixture.Open())
        {
            seed.AddRange(guest, item, mark, saved); await seed.SaveChangesAsync();
            await seed.Database.ExecuteSqlRawAsync("CREATE SCHEMA cache");
            await seed.Database.ExecuteSqlRawAsync("CREATE TABLE cache.TokenCache (Id nvarchar(449) NOT NULL PRIMARY KEY, Value varbinary(max) NOT NULL, ExpiresAtTime datetimeoffset NOT NULL)");
            await seed.Database.ExecuteSqlInterpolatedAsync($"INSERT cache.TokenCache VALUES({"synthetic"},{Encoding.UTF8.GetBytes("cache-bytes-canary")},{fixture.Now.AddDays(1)})");
        }
        var exportRoot = KeyRingBackupTests.NewPath(); Directory.CreateDirectory(exportRoot);
        var bacpac = Path.Combine(exportRoot, "synthetic.bacpac");
        await DatabaseExport.ExportAsync(fixture.Connection, new SqlConnectionStringBuilder(fixture.Connection).InitialCatalog, bacpac);
        await Assert.ThrowsAsync<ArgumentException>(() => DatabaseExport.ExportAsync(fixture.Connection, "wrong", bacpac));
        source.Export = (target, _) => { File.Copy(bacpac, target); return Task.CompletedTask; };
        var captured = KeyRingBackupTests.NewPath(); using var output = new StringWriter();
        Assert.Equal(0, await BackupCommand.RunAsync(["capture-beta", "--output", captured, "--writers-quiesced"], LiveBackupTests.Environment, output, source));
        Assert.DoesNotContain("canary", output.ToString());
        Assert.Single(Directory.GetFiles(captured)); Assert.Empty(Directory.GetDirectories(captured));
        var bundle = Path.Combine(captured, "capture.lhb"); Assert.True(File.Exists(bundle));
        Assert.DoesNotContain("private-capture-canary", Encoding.UTF8.GetString(File.ReadAllBytes(bundle)));
        await Assert.ThrowsAsync<IOException>(() => BackupCapture.CaptureAsync(source, captured, LiveBackupTests.Passphrase, true));
        var unpacked = KeyRingBackupTests.NewPath(); using var unpackReport = new StringWriter();
        Assert.Equal(0, await BackupCommand.RunAsync(["unpack", "--source", bundle, "--output", unpacked], LiveBackupTests.Environment, unpackReport));
        Assert.DoesNotContain("canary", unpackReport.ToString());
        Assert.False(Directory.Exists(Path.Combine(unpacked, "work")));
        foreach (var application in new[] { "ListHero.Api", "ListHero.Web" })
        {
            var prefix = application == "ListHero.Api" ? "api" : "web";
            var protection = Path.Combine(unpacked, prefix, "protection");
            using var certificate = X509Certificate2.CreateFromEncryptedPemFile(Path.Combine(protection, "certificate.pem"), LiveBackupTests.Passphrase);
            using var provider = new ServiceCollection().AddDataProtection().SetApplicationName(application)
                .PersistKeysToFileSystem(new(Path.Combine(protection, "keyring"))).UnprotectKeysWithAnyCertificate(certificate)
                .DisableAutomaticKeyGeneration().Services.BuildServiceProvider();
            Assert.Equal("private-capture-canary", provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("capture-test").Unprotect(source.ProtectedProbes[application]));
        }
        var restoredConnection = new SqlConnectionStringBuilder(fixture.Connection) { InitialCatalog = new SqlConnectionStringBuilder(fixture.Connection).InitialCatalog + "_Imported", Pooling = false };
        var masterConnection = new SqlConnectionStringBuilder(restoredConnection.ConnectionString) { InitialCatalog = "master" };
        try
        {
            using var package = BacPackage.Load(Path.Combine(unpacked, "database.bacpac"));
            new DacServices(masterConnection.ConnectionString).ImportBacpac(package, restoredConnection.InitialCatalog, new DacImportOptions { CommandTimeout = 120 });
            await using var db = new ListHeroDbContext(new DbContextOptionsBuilder<ListHeroDbContext>().UseSqlServer(restoredConnection.ConnectionString).Options);
            Assert.Equal(2, await db.Users.CountAsync()); Assert.Equal(fixture.ListId, (await db.WishLists.SingleAsync()).Id);
            Assert.NotNull((await db.WishListItems.SingleAsync()).ArchivedAt); Assert.Equal(item.Id, (await db.WishListItems.SingleAsync()).Id);
            Assert.Equal(guest.CredentialHash, (await db.GuestIdentities.SingleAsync()).CredentialHash);
            var restoredMark = await db.ItemPurchases.SingleAsync(); Assert.Equal(mark.Id, restoredMark.Id); Assert.Equal(6, restoredMark.Quantity); Assert.NotNull(restoredMark.UndoneAt);
            Assert.NotNull((await db.SavedLists.SingleAsync()).ArchivedAt); Assert.Equal(fixture.ShareId, (await db.ListShareLinks.SingleAsync()).Id);
            Assert.Equal("cache-bytes-canary", Encoding.UTF8.GetString(await db.Database.SqlQueryRaw<byte[]>("SELECT Value FROM cache.TokenCache").SingleAsync()));
            Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
            await db.Database.ExecuteSqlRawAsync("DBCC CHECKDB WITH NO_INFOMSGS");
        }
        finally
        {
            // The exact fresh target from this test, no force-disconnect or wildcard deletion.
            await using var connection = new SqlConnection(masterConnection.ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = $"IF DB_ID(@name) IS NOT NULL DROP DATABASE [{restoredConnection.InitialCatalog}]";
            command.Parameters.AddWithValue("@name", restoredConnection.InitialCatalog); await command.ExecuteNonQueryAsync();
        }
        foreach (var failure in new[] { "restarted", "settings-changed", "keys-changed", "site-changed", "configuration-changed" })
        {
            using var changing = new SyntheticBackupSource { Failure = failure, Export = source.Export };
            var failed = KeyRingBackupTests.NewPath();
            await Assert.ThrowsAsync<InvalidDataException>(() => BackupCapture.CaptureAsync(changing, failed, LiveBackupTests.Passphrase, true));
            Assert.False(File.Exists(Path.Combine(failed, "capture.lhb"))); Assert.Empty(Directory.GetFileSystemEntries(failed));
        }
        // Authenticated archives can still contain unsupported or mismatched contents. Reject these
        // before extracting any application/config/database files, with no path traversal.
        foreach (var damage in new[] { "hash", "duplicate", "traversal", "version", "manifest" })
        {
            var changed = Path.Combine(exportRoot, damage + ".zip");
            SealedArchive.Open(bundle, changed, LiveBackupTests.Passphrase);
            using (var zip = ZipFile.Open(changed, ZipArchiveMode.Update))
            {
                if (damage is "duplicate" or "traversal") zip.CreateEntry(damage == "duplicate" ? "api/settings.json" : "../escape.json");
                else
                {
                    var entry = zip.GetEntry(damage == "hash" ? "api/settings.json" : "manifest.json")!;
                    using var reader = new StreamReader(entry.Open()); var text = reader.ReadToEnd(); reader.Dispose(); entry.Delete();
                    using var writer = new StreamWriter(zip.CreateEntry(entry.FullName).Open());
                    writer.Write(damage == "hash" ? "altered" : damage == "version" ? text.Replace("\"Version\":1", "\"Version\":99") : "null");
                }
            }
            var sealedChanged = changed + ".lhb"; SealedArchive.Seal(changed, sealedChanged, LiveBackupTests.Passphrase);
            var refused = KeyRingBackupTests.NewPath(); Assert.Throws<InvalidDataException>(() => BackupCapture.Unpack(sealedChanged, refused, LiveBackupTests.Passphrase));
            Assert.Empty(Directory.GetFileSystemEntries(refused));
        }
    }
}
