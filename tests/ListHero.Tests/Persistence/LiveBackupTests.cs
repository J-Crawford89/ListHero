using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ListHero.LiveBackup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.Persistence;

public sealed class LiveBackupTests
{
    internal const string Passphrase = "synthetic-portable-capture-passphrase-76";
    [Fact]
    public void Sealed_archive_streams_large_payloads_and_authenticates_before_creating_plaintext()
    {
        var root = KeyRingBackupTests.NewPath(); Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input"); var bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 13); File.WriteAllBytes(input, bytes);
        var bundle = Path.Combine(root, "bundle"); SealedArchive.Seal(input, bundle, Passphrase);
        Assert.NotEqual(bytes, File.ReadAllBytes(bundle));
        var recovered = Path.Combine(root, "recovered"); SealedArchive.Open(bundle, recovered, Passphrase); Assert.Equal(bytes, File.ReadAllBytes(recovered));
        Assert.Throws<IOException>(() => SealedArchive.Seal(input, bundle, Passphrase));
        Assert.Throws<IOException>(() => SealedArchive.Open(bundle, recovered, Passphrase));
        Assert.Throws<ArgumentException>(() => SealedArchive.Open(bundle, recovered, "short"));
        Assert.Throws<ArgumentException>(() => SealedArchive.Seal(input, bundle, new string('a', 1025)));
        Assert.Throws<CryptographicException>(() => SealedArchive.Open(bundle, recovered + "-wrong", Passphrase + "wrong"));
        Assert.False(File.Exists(recovered + "-wrong"));
        var original = File.ReadAllBytes(bundle);
        foreach (var offset in new[] { 8, 24, 40, original.Length - 33, original.Length - 1 })
        {
            var tampered = original.ToArray(); tampered[offset] ^= 1; File.WriteAllBytes(bundle, tampered);
            Assert.Throws<CryptographicException>(() => SealedArchive.Open(bundle, recovered + "-tampered", Passphrase));
            Assert.False(File.Exists(recovered + "-tampered"));
        }
        foreach (var length in new[] { 0, 87, original.Length - 16 })
        {
            File.WriteAllBytes(bundle, original[..length]);
            Assert.ThrowsAny<Exception>(() => SealedArchive.Open(bundle, recovered + "-truncated", Passphrase));
            Assert.False(File.Exists(recovered + "-truncated"));
        }
        original[0] ^= 1; File.WriteAllBytes(bundle, original);
        Assert.Throws<InvalidDataException>(() => SealedArchive.Open(bundle, recovered + "-version", Passphrase));
        File.WriteAllBytes(input, []); Assert.Throws<InvalidDataException>(() => SealedArchive.Seal(input, bundle + "-empty", Passphrase));
    }

    [Theory]
    [InlineData("running")] [InlineData("wrong-resource")] [InlineData("settings-reference")]
    [InlineData("key-path")] [InlineData("bad-key")] [InlineData("key-traversal")]
    [InlineData("duplicate-key")] [InlineData("oversized-key")] [InlineData("wrong-certificate")]
    [InlineData("missing-release")] [InlineData("release-mismatch")] [InlineData("export-failure")]
    public async Task Capture_refuses_unsafe_inputs_without_publishing_a_backup_or_leaking_failure_details(string failure)
    {
        using var source = new SyntheticBackupSource { Failure = failure };
        var target = KeyRingBackupTests.NewPath(); using var report = new StringWriter();
        var code = await BackupCommand.RunAsync(["capture-beta", "--output", target, "--writers-quiesced"], Environment, report, source);
        Assert.Equal(1, code); Assert.DoesNotContain("private-capture-canary", report.ToString());
        Assert.False(File.Exists(Path.Combine(target, "capture.lhb"))); Assert.False(Directory.Exists(Path.Combine(target, "work")));
        if (failure is "running" or "wrong-resource") { Assert.False(Directory.Exists(target)); Assert.Equal(0, source.Exports); }
    }

    [Fact]
    public async Task CLI_validates_arguments_and_missing_credentials_before_contacting_cloud()
    {
        foreach (var args in new string[][] {
            ["oops"], ["unpack", "--output", "--source"], ["unpack", "--output", "a"],
            ["capture-beta", "--output", "a"], ["capture-beta", "--output", "a", "--writers-quiesced", "--apply"],
            ["unpack", "--output", "a", "--output", "b", "--source", "c"], ["unpack", "--output", " ", "--source", "c"],
            ["capture-beta", "--output", "a", "--writers-quiesced"] })
        {
            using var output = new StringWriter(); Assert.Equal(1, await BackupCommand.RunAsync(args, _ => null, output));
            Assert.Contains("invalid-arguments", output.ToString());
        }
        foreach (var args in new string[][] { [], ["--help"] })
        {
            using var output = new StringWriter(); Assert.Equal(0, await BackupCommand.RunAsync(args, _ => throw new Exception(), output));
        }
        using var source = new SyntheticBackupSource();
        await Assert.ThrowsAsync<ArgumentException>(() => BackupCapture.CaptureAsync(source, KeyRingBackupTests.NewPath(), Passphrase, false));
        foreach (var error in new Exception[] { new HttpRequestException("private-capture-canary"), new UnauthorizedAccessException("private-capture-canary"), new OperationCanceledException("private-capture-canary") })
        {
            source.Error = error; using var output = new StringWriter();
            Assert.Equal(1, await BackupCommand.RunAsync(["capture-beta", "--output", KeyRingBackupTests.NewPath(), "--writers-quiesced"], Environment, output, source));
            Assert.DoesNotContain("private-capture-canary", output.ToString());
        }
    }

    [Fact]
    public async Task Azure_reader_uses_only_expected_bearer_endpoints_and_refuses_unsafe_connections()
    {
        var handler = new RecordingHandler(); using var client = new HttpClient(handler);
        var source = new AzureBackupSource(client, "private-token-canary", "Server=evil;Database=ListHeroBeta;Encrypt=False");
        await source.ReadStateAsync(AzureBackupSource.Api, default); await source.ReadSettingsAsync(AzureBackupSource.Web, default);
        await source.ReadKeysAsync(AzureBackupSource.Api, "ListHero.Api", default);
        var target = KeyRingBackupTests.NewPath(); Directory.CreateDirectory(target);
        await source.ReadSiteAsync(AzureBackupSource.Web, Path.Combine(target, "site.zip"), default);
        Assert.Equal(new[] { "GET", "POST", "GET", "GET" }, handler.Requests.Select(r => r.Method));
        Assert.All(handler.Requests, r => Assert.Equal("Bearer private-token-canary", r.Authorization));
        Assert.Contains("/config/appsettings/list?", handler.Requests[1].Url);
        Assert.EndsWith("/api/zip/data/ListHero.Api/keys/", handler.Requests[2].Url);
        Assert.EndsWith("/api/zip/site/wwwroot/", handler.Requests[3].Url);
        await source.ReadConfigurationAsync(AzureBackupSource.Api, default);
        Assert.Equal("GET", handler.Requests[4].Method); Assert.Contains("/config/web?", handler.Requests[4].Url);
        Assert.Equal("POST", handler.Requests[5].Method); Assert.Contains("/config/connectionstrings/list?", handler.Requests[5].Url);
        await Assert.ThrowsAsync<ArgumentException>(() => source.ExportDatabaseAsync(Path.Combine(target, "no.bacpac"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => source.ReadStateAsync("other-app", default));
        await Assert.ThrowsAsync<ArgumentException>(() => source.ReadKeysAsync(AzureBackupSource.Api, "ListHero.Web", default));
        handler.Status = HttpStatusCode.Redirect;
        await Assert.ThrowsAsync<HttpRequestException>(() => source.ReadStateAsync(AzureBackupSource.Api, default));
        handler.Status = HttpStatusCode.OK; handler.Content = new byte[1024 * 1024 + 1];
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ReadStateAsync(AzureBackupSource.Api, default));
    }

    internal static string? Environment(string name) => name == "LISTHERO_BACKUP_PASSPHRASE" ? Passphrase : null;
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Method, string Url, string? Authorization)> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public byte[] Content { get; set; } = "{}"u8.ToArray();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(Content) });
        }
    }
}

internal sealed class SyntheticBackupSource : IBackupSource, IDisposable
{
    public string? Failure { get; set; }
    public Exception? Error { get; set; }
    public int Exports { get; private set; }
    public Func<string, CancellationToken, Task>? Export { get; set; }
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 certificate = KeyRingBackupTests.Certificate();
    private readonly string root = KeyRingBackupTests.NewPath();
    private readonly Dictionary<string, byte[]> keys = [];
    public Dictionary<string, string> ProtectedProbes { get; } = [];
    public SyntheticBackupSource()
    {
        foreach (var application in new[] { "ListHero.Api", "ListHero.Web" })
        {
            var path = Path.Combine(root, application);
            using var provider = new ServiceCollection().AddDataProtection().SetApplicationName(application).PersistKeysToFileSystem(new(path))
                .ProtectKeysWithCertificate(certificate).Services.BuildServiceProvider();
            ProtectedProbes[application] = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("capture-test").Protect("private-capture-canary");
            using var content = new MemoryStream();
            using (var zip = new ZipArchive(content, ZipArchiveMode.Create, true))
                foreach (var file in Directory.GetFiles(path)) zip.CreateEntryFromFile(file, Path.GetFileName(file));
            keys[application] = content.ToArray();
        }
    }
    public Task<byte[]> ReadStateAsync(string app, CancellationToken ct)
    {
        if (Error is not null) throw Error;
        return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(new { id = $"/subscriptions/{AzureBackupSource.Subscription}/resourceGroups/{AzureBackupSource.Group}/providers/Microsoft.Web/sites/{(Failure == "wrong-resource" ? "wrong" : app)}", properties = new { state = Failure == "running" || Failure == "restarted" && Exports > 0 ? "Running" : "Stopped" } }));
    }
    public Task<byte[]> ReadSettingsAsync(string app, CancellationToken ct)
    {
        using var rsa = certificate.GetRSAPrivateKey()!;
        // Cache the PEM: encryption creates fresh salts, whereas real app settings remain stable.
        var pemPath = Path.Combine(root, "certificate.pem");
        if (!File.Exists(pemPath)) File.WriteAllText(pemPath, certificate.ExportCertificatePem() + "\n" + rsa.ExportEncryptedPkcs8PrivateKeyPem("private-certificate-password", new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)));
        var properties = new Dictionary<string, string>
        {
            ["DataProtection__RequirePersistentKeys"] = "true",
            ["DataProtection__CertificateBase64"] = Convert.ToBase64String(File.ReadAllBytes(pemPath)),
            ["DataProtection__CertificatePassword"] = Failure == "wrong-certificate" ? "wrong" : "private-certificate-password",
            ["ConnectionStrings__ListHero"] = Failure == "settings-reference" ? "@Microsoft.KeyVault(SecretUri=private-capture-canary)" : "private-capture-canary",
            ["Test"] = Failure == "settings-changed" && Exports > 0 ? "changed" : "same"
        };
        if (Failure == "key-path") properties["DataProtection__KeyRingPath"] = "D:\\other\\keys";
        return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(new { properties }));
    }
    public Task<byte[]> ReadKeysAsync(string app, string application, CancellationToken ct)
    {
        if (Failure is "key-traversal" or "duplicate-key" or "oversized-key" or "bad-key" || Failure == "keys-changed" && Exports > 0)
        {
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            {
                var name = Failure == "key-traversal" ? "../private-capture-canary.xml" : "key-invalid.xml";
                using (var content = zip.CreateEntry(name).Open()) content.Write(Encoding.UTF8.GetBytes(Failure == "oversized-key" ? new string('a', 1024 * 1024 + 1) : "<wrong/>"));
                if (Failure == "duplicate-key") zip.CreateEntry(name);
            }
            return Task.FromResult(bytes.ToArray());
        }
        return Task.FromResult(keys[application]);
    }
    public Task<byte[]> ReadConfigurationAsync(string app, CancellationToken ct) => Task.FromResult(Encoding.UTF8.GetBytes(Failure == "configuration-changed" && Exports > 0 ? "{\"Web\":{\"new\":true},\"ConnectionStrings\":{}}" : "{\"Web\":{},\"ConnectionStrings\":{}}"));
    public Task ReadSiteAsync(string app, string target, CancellationToken ct)
    {
        var name = app == AzureBackupSource.Api ? "ListHero.Api" : "ListHero.Web";
        using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
        if (Failure == "release-mismatch" && app == AzureBackupSource.Web)
        {
            var dllPath = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(dllPath).ProductVersion!;
            var commit = version.Split('+').Last(); var different = new string(commit[0] == '0' ? '1' : '0', 40);
            var bytes = File.ReadAllBytes(dllPath); var replacements = 0;
            foreach (var encoding in new[] { Encoding.Unicode, Encoding.UTF8 })
            {
                var original = encoding.GetBytes(commit); var replacement = encoding.GetBytes(different);
                for (var offset = 0; offset <= bytes.Length - original.Length; offset++)
                    if (bytes.AsSpan(offset, original.Length).SequenceEqual(original)) { replacement.CopyTo(bytes, offset); replacements++; }
            }
            Assert.True(replacements > 0);
            using var dll = zip.CreateEntry(name + ".dll").Open(); dll.Write(bytes);
        }
        else if (Failure != "missing-release") zip.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory, name + ".dll"), name + ".dll");
        using var content = zip.CreateEntry("appsettings.json").Open();
        content.Write(Encoding.UTF8.GetBytes(Failure == "site-changed" && Exports > 0 ? "changed" : "private-capture-canary"));
        return Task.CompletedTask;
    }
    public async Task ExportDatabaseAsync(string target, CancellationToken ct)
    {
        Exports++;
        if (Failure == "export-failure" || Export is null) throw new InvalidOperationException("private-capture-canary");
        await Export(target, ct);
    }
    public void Dispose() => certificate.Dispose();
}
