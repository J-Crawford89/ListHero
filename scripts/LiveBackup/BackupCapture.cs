using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ListHero.Infrastructure.Maintenance;

namespace ListHero.LiveBackup;

public sealed record BackupFile(string Name, long Bytes, string Sha256);
public sealed record BackupManifest(int Version, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc,
    string Subscription, string ResourceGroup, string Database, string ReleaseCommit, string Consistency, BackupFile[] Files);
public sealed record CaptureReport(string Outcome, bool ArchiveVerified, string ReleaseCommit, int Files);

public static partial class BackupCapture
{
    private static readonly string[] PayloadNames = ["database.bacpac", "api/settings.json", "api/configuration.json", "api/state.json", "api/site.zip", "api/keys.lhb", "web/settings.json", "web/configuration.json", "web/state.json", "web/site.zip", "web/keys.lhb"];
    [GeneratedRegex(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant)] private static partial Regex CommitPattern();

    public static async Task<CaptureReport> CaptureAsync(IBackupSource source, string output, string passphrase, bool writersQuiesced, CancellationToken ct = default)
    {
        SealedArchive.ValidatePassphrase(passphrase);
        if (!writersQuiesced) throw new ArgumentException();
        // Read-only preflight before creating any files or exporting database contents.
        var before = await States(source, ct);
        KeyRingBackup.NewPrivateDirectory(output);
        var work = Path.Combine(Path.GetFullPath(output), "work"); KeyRingBackup.NewPrivateDirectory(work);
        var partial = Path.Combine(output, "capture.partial");
        var started = DateTimeOffset.UtcNow;
        string release;
        try
        {
            Directory.CreateDirectory(Path.Combine(work, "api")); Directory.CreateDirectory(Path.Combine(work, "web"));
            var settings = new Dictionary<string, byte[]>(); var rings = new Dictionary<string, byte[]>(); var configurations = new Dictionary<string, byte[]>();
            foreach (var (prefix, app, application) in Apps())
            {
                var data = await source.ReadSettingsAsync(app, ct); settings.Add(app, data);
                var properties = Settings(data);
                var configuration = await source.ReadConfigurationAsync(app, ct); configurations.Add(app, configuration);
                if (Encoding.UTF8.GetString(configuration).Contains("@Microsoft.KeyVault", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                var pem = Encoding.UTF8.GetString(Convert.FromBase64String(properties["DataProtection__CertificateBase64"]));
                using var certificate = X509Certificate2.CreateFromEncryptedPem(pem, pem, properties["DataProtection__CertificatePassword"]);
                var keys = await source.ReadKeysAsync(app, application, ct); rings.Add(app, keys);
                var ring = Path.Combine(work, prefix + "-ring"); KeyRingBackup.NewPrivateDirectory(ring);
                WriteRing(keys, ring);
                var bundle = Path.Combine(work, prefix + "-bundle");
                KeyRingBackup.Export(ring, bundle, application, passphrase, certificate);
                File.Move(Path.Combine(bundle, "keys.lhb"), Path.Combine(work, prefix, "keys.lhb"));
                File.WriteAllBytes(Path.Combine(work, prefix, "settings.json"), data);
                File.WriteAllBytes(Path.Combine(work, prefix, "configuration.json"), configuration);
                File.WriteAllBytes(Path.Combine(work, prefix, "state.json"), before[app]);
                await source.ReadSiteAsync(app, Path.Combine(work, prefix, "site.zip"), ct);
            }
            var apiCommit = ReadRelease(Path.Combine(work, "api", "site.zip"), "ListHero.Api");
            var webCommit = ReadRelease(Path.Combine(work, "web", "site.zip"), "ListHero.Web");
            if (apiCommit != webCommit) throw new InvalidDataException();
            release = apiCommit;
            await source.ExportDatabaseAsync(Path.Combine(work, "database.bacpac"), ct);
            using (BacpacCheck(Path.Combine(work, "database.bacpac"))) { }
            _ = await States(source, ct);
            // Refuse an observed deployment/settings/key rotation during capture. The operator must
            // still exclude other SQL writers and deployment jobs throughout the maintenance window.
            foreach (var (prefix, app, application) in Apps())
            {
                if (!SettingsEqual(settings[app], await source.ReadSettingsAsync(app, ct)) ||
                    !RingEqual(rings[app], await source.ReadKeysAsync(app, application, ct)) ||
                    !JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(configurations[app]), JsonSerializer.Deserialize<JsonElement>(await source.ReadConfigurationAsync(app, ct)))) throw new InvalidDataException();
                var check = Path.Combine(work, prefix + "-check.zip"); await source.ReadSiteAsync(app, check, ct);
                if (SiteFingerprint(check) != SiteFingerprint(Path.Combine(work, prefix, "site.zip"))) throw new InvalidDataException();
            }
            var manifest = new BackupManifest(1, started, DateTimeOffset.UtcNow, AzureBackupSource.Subscription, AzureBackupSource.Group,
                AzureBackupSource.Database, apiCommit, "operator-quiesced; apps-stopped-before-and-after", PayloadNames.Select(name => Describe(work, name)).ToArray());
            var zipPath = Path.Combine(work, "capture.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var name in PayloadNames) zip.CreateEntryFromFile(Path.Combine(work, name), name, CompressionLevel.NoCompression);
                using var stream = zip.CreateEntry("manifest.json").Open(); JsonSerializer.Serialize(stream, manifest);
            }
            SealedArchive.Seal(zipPath, partial, passphrase);
            var verify = Path.Combine(work, "verify.zip"); SealedArchive.Open(partial, verify, passphrase);
            _ = ValidateArchive(verify);
        }
        finally { RemoveWorkspace(work); }
        // A final filename means authentication, file hashes, and staging cleanup all succeeded.
        File.Move(partial, Path.Combine(output, "capture.lhb"));
        return new("captured", true, release, PayloadNames.Length);
    }

    public static CaptureReport Unpack(string bundle, string output, string passphrase)
    {
        SealedArchive.ValidatePassphrase(passphrase);
        KeyRingBackup.NewPrivateDirectory(output);
        var work = Path.Combine(Path.GetFullPath(output), "work"); KeyRingBackup.NewPrivateDirectory(work);
        BackupManifest manifest;
        try
        {
            var plain = Path.Combine(work, "capture.zip"); SealedArchive.Open(bundle, plain, passphrase);
            manifest = ValidateArchive(plain);
            using var zip = ZipFile.OpenRead(plain);
            foreach (var name in PayloadNames.Append("manifest.json"))
            {
                var path = Path.Combine(output, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                zip.GetEntry(name)!.ExtractToFile(path, false);
            }
            foreach (var (prefix, _, application) in Apps())
                KeyRingBackup.Restore(Path.Combine(output, prefix, "keys.lhb"), Path.Combine(output, prefix, "protection"), application, passphrase);
        }
        finally { RemoveWorkspace(work); }
        return new("unpacked", true, manifest.ReleaseCommit, PayloadNames.Length);
    }

    private static IEnumerable<(string Prefix, string App, string Application)> Apps()
    {
        yield return ("api", AzureBackupSource.Api, "ListHero.Api"); yield return ("web", AzureBackupSource.Web, "ListHero.Web");
    }
    private static async Task<Dictionary<string, byte[]>> States(IBackupSource source, CancellationToken ct)
    {
        var result = new Dictionary<string, byte[]>();
        foreach (var (_, app, _) in Apps())
        {
            var bytes = await source.ReadStateAsync(app, ct);
            using var json = JsonDocument.Parse(bytes); var root = json.RootElement;
            var expected = $"/subscriptions/{AzureBackupSource.Subscription}/resourceGroups/{AzureBackupSource.Group}/providers/Microsoft.Web/sites/{app}";
            if (!string.Equals(root.GetProperty("id").GetString(), expected, StringComparison.OrdinalIgnoreCase) || root.GetProperty("properties").GetProperty("state").GetString() != "Stopped") throw new InvalidDataException();
            result.Add(app, bytes);
        }
        return result;
    }
    private static Dictionary<string, string> Settings(byte[] data)
    {
        using var json = JsonDocument.Parse(data);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in json.RootElement.GetProperty("properties").EnumerateObject())
        {
            var value = property.Value.GetString() ?? throw new InvalidDataException();
            if (value.Contains("@Microsoft.KeyVault", StringComparison.OrdinalIgnoreCase) || !values.TryAdd(property.Name.Replace(":", "__"), value)) throw new InvalidDataException();
        }
        if (!values.ContainsKey("DataProtection__CertificateBase64") || !values.ContainsKey("DataProtection__CertificatePassword") ||
            !values.TryGetValue("DataProtection__RequirePersistentKeys", out var persistent) || !string.Equals(persistent, "true", StringComparison.OrdinalIgnoreCase) ||
            values.TryGetValue("DataProtection__KeyRingPath", out var path) && !string.IsNullOrWhiteSpace(path)) throw new InvalidDataException();
        return values;
    }
    private static bool SettingsEqual(byte[] first, byte[] second)
    {
        var a = Settings(first); var b = Settings(second);
        return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }
    private static Dictionary<string, byte[]> Ring(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase); long size = 0;
        foreach (var entry in zip.Entries)
        {
            if (!Regex.IsMatch(entry.FullName, @"\A(?:key|revocation)-[A-Za-z0-9-]+\.xml\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                entry.Length > 1024 * 1024 || (size += entry.Length) > 16 * 1024 * 1024 || files.Count >= 4096) throw new InvalidDataException();
            using var stream = entry.Open(); using var content = new MemoryStream(); SealedArchive.CopyExactly(stream, content, entry.Length);
            if (!files.TryAdd(entry.FullName, content.ToArray())) throw new InvalidDataException();
        }
        return files;
    }
    private static void WriteRing(byte[] bytes, string target)
    {
        foreach (var (name, content) in Ring(bytes)) File.WriteAllBytes(Path.Combine(target, name), content);
    }
    private static bool RingEqual(byte[] first, byte[] second)
    {
        var a = Ring(first); var b = Ring(second);
        return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && pair.Value.AsSpan().SequenceEqual(value));
    }
    private static string ReadRelease(string path, string application)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.Entries.Single(e => e.FullName == application + ".dll");
        if (entry.Length is < 1 or > 16 * 1024 * 1024) throw new InvalidDataException();
        var dll = path + ".dll";
        try
        {
            using (var input = entry.Open())
            using (var output = new FileStream(dll, FileMode.CreateNew, FileAccess.Write))
            {
                SealedArchive.CopyExactly(input, output, entry.Length);
                if (input.ReadByte() != -1) throw new InvalidDataException();
            }
            var version = FileVersionInfo.GetVersionInfo(dll).ProductVersion ?? "";
            var commit = version.Split('+').Last(); if (!CommitPattern().IsMatch(commit)) throw new InvalidDataException();
            return commit;
        }
        finally { File.Delete(dll); }
    }
    private static IDisposable BacpacCheck(string path) => Microsoft.SqlServer.Dac.BacPackage.Load(path);
    private static string SiteFingerprint(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 30000 || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count) throw new InvalidDataException();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long size = 0;
        foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            if ((size += entry.Length) > 1024L * 1024 * 1024) throw new InvalidDataException();
            hash.AppendData(Encoding.UTF8.GetBytes(entry.FullName + "\0"));
            hash.AppendData(HashEntry(entry));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static BackupFile Describe(string root, string name)
    {
        using var stream = File.OpenRead(Path.Combine(root, name));
        return new(name, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }
    private static BackupManifest ValidateArchive(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count != PayloadNames.Length + 1 || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count ||
            zip.Entries.Any(e => !PayloadNames.Append("manifest.json").Contains(e.FullName)) || zip.GetEntry("manifest.json")!.Length > 64 * 1024) throw new InvalidDataException();
        var manifestEntry = zip.GetEntry("manifest.json")!;
        using var stream = manifestEntry.Open(); using var manifestBytes = new MemoryStream();
        SealedArchive.CopyExactly(stream, manifestBytes, manifestEntry.Length);
        if (stream.ReadByte() != -1) throw new InvalidDataException();
        var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestBytes.ToArray()) ?? throw new InvalidDataException();
        if (manifest.Version != 1 || manifest.ReleaseCommit is null || !CommitPattern().IsMatch(manifest.ReleaseCommit) || manifest.Subscription != AzureBackupSource.Subscription ||
            manifest.ResourceGroup != AzureBackupSource.Group || manifest.Database != AzureBackupSource.Database || manifest.Files is null || manifest.Files.Length != PayloadNames.Length ||
            manifest.Files.Select(f => f.Name).Distinct().Count() != PayloadNames.Length || manifest.StartedUtc > manifest.CompletedUtc || manifest.Consistency != "operator-quiesced; apps-stopped-before-and-after") throw new InvalidDataException();
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (!PayloadNames.Contains(file.Name) || file.Bytes < 1 || (total += file.Bytes) > SealedArchive.MaximumBytes || file.Sha256 is null) throw new InvalidDataException();
            var entry = zip.GetEntry(file.Name)!;
            if (entry.Length != file.Bytes) throw new InvalidDataException();
            if (Convert.ToHexString(HashEntry(entry)) != file.Sha256) throw new InvalidDataException();
        }
        return manifest;
    }
    private static byte[] HashEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; var remaining = entry.Length;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new InvalidDataException();
            hash.AppendData(buffer, 0, read); remaining -= read;
        }
        if (stream.ReadByte() != -1) throw new InvalidDataException();
        return hash.GetHashAndReset();
    }
    private static void RemoveWorkspace(string directory)
    {
        // Only the new private staging directory created by this invocation. Refuse links rather
        // than following them during cleanup; never delete or overwrite an operator's existing path.
        var info = new DirectoryInfo(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException();
        foreach (var entry in info.GetFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException();
            if (entry is DirectoryInfo child) RemoveWorkspace(child.FullName); else entry.Delete();
        }
        info.Delete();
    }
}
