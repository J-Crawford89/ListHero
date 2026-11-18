using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ListHero.Infrastructure.Maintenance;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Tests.Persistence;

public sealed class KeyRingBackupTests
{
    private const string Password = "synthetic-backup-passphrase-42";
    internal static string NewPath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ListHero.slnx"))) root = root.Parent;
        return Path.Combine(root!.FullName, ".artifacts", "maintenance-tests", Guid.NewGuid().ToString("N"));
    }
    internal static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        using var original = new CertificateRequest("CN=Synthetic backup test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(original.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }
    private static ServiceProvider Provider(string path, string application, X509Certificate2? cert, bool readOnly = false)
    {
        var services = new ServiceCollection();
        var builder = services.AddDataProtection().SetApplicationName(application).PersistKeysToFileSystem(new(path));
        if (cert is not null) builder.ProtectKeysWithCertificate(cert).UnprotectKeysWithAnyCertificate(cert);
        if (readOnly) builder.DisableAutomaticKeyGeneration();
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("ListHero.Api", true)] [InlineData("ListHero.Web", true)] [InlineData("ListHero.Api", false)]
    public void Encrypted_bundles_restore_original_key_ids_and_payloads_in_a_new_directory(string application, bool withCertificate)
    {
        var root = NewPath(); var source = Path.Combine(root, "source");
        using var cert = withCertificate ? Certificate() : null;
        using var before = Provider(source, application, cert);
        var cipher = before.GetRequiredService<IDataProtectionProvider>().CreateProtector("probe").Protect("private-payload-canary");
        var keyFile = Directory.GetFiles(source, "key-*.xml").Single();
        File.AppendAllText(keyFile, "\n<!-- private-key-file-canary -->");
        var original = File.ReadAllBytes(keyFile);
        var backup = Path.Combine(root, "backup");
        var report = KeyRingBackup.Export(source, backup, application, Password, cert);
        Assert.Equal(1, report.KeyFiles); Assert.Equal(withCertificate, report.CertificateIncluded);
        Assert.DoesNotContain("private-key-file-canary", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(backup, "keys.lhb"))));
        Assert.Equal(original, File.ReadAllBytes(keyFile));
        Assert.Single(Directory.GetFiles(source));
        var restored = Path.Combine(root, "restored");
        KeyRingBackup.Restore(Path.Combine(backup, "keys.lhb"), restored, application, Password);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(restored, "keyring", Path.GetFileName(keyFile))));
        using var restoredCert = withCertificate ? X509Certificate2.CreateFromEncryptedPemFile(Path.Combine(restored, "certificate.pem"), Password) : null;
        using var after = Provider(Path.Combine(restored, "keyring"), application, restoredCert, true);
        Assert.Equal("private-payload-canary", after.GetRequiredService<IDataProtectionProvider>().CreateProtector("probe").Unprotect(cipher));
        using var wrongApp = Provider(Path.Combine(restored, "keyring"), application == "ListHero.Api" ? "ListHero.Web" : "ListHero.Api", restoredCert, true);
        Assert.Throws<CryptographicException>(() => wrongApp.GetRequiredService<IDataProtectionProvider>().CreateProtector("probe").Unprotect(cipher));
        Assert.Throws<IOException>(() => KeyRingBackup.Restore(Path.Combine(backup, "keys.lhb"), restored, application, Password));
        Assert.Throws<IOException>(() => KeyRingBackup.Export(source, backup, application, Password, cert));
    }

    [Theory]
    [InlineData("password")] [InlineData("tampered")] [InlineData("application")] [InlineData("truncated")]
    public void Invalid_bundles_never_create_a_restore_directory(string failure)
    {
        var root = NewPath(); var source = Path.Combine(root, "source");
        using var cert = Certificate(); using var provider = Provider(source, "ListHero.Api", cert);
        _ = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("probe").Protect("secret");
        var backup = Path.Combine(root, "backup"); KeyRingBackup.Export(source, backup, "ListHero.Api", Password, cert);
        var file = Path.Combine(backup, "keys.lhb");
        if (failure == "tampered") { var bytes = File.ReadAllBytes(file); bytes[^1] ^= 1; File.WriteAllBytes(file, bytes); }
        if (failure == "truncated") File.WriteAllBytes(file, [1, 2, 3]);
        var output = Path.Combine(root, "not-created");
        Assert.ThrowsAny<Exception>(() => KeyRingBackup.Restore(file, output, failure == "application" ? "ListHero.Web" : "ListHero.Api", failure == "password" ? Password + "wrong" : Password));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Missing_key_rings_bad_settings_wrong_certificates_and_machine_bound_files_are_refused()
    {
        var root = NewPath(); Directory.CreateDirectory(root);
        Assert.Throws<ArgumentException>(() => KeyRingBackup.Export(root, Path.Combine(root, "out"), "Other", Password, null));
        Assert.Throws<ArgumentException>(() => KeyRingBackup.Export(root, Path.Combine(root, "out"), "ListHero.Api", "short", null));
        Assert.Throws<InvalidDataException>(() => KeyRingBackup.Export(root, Path.Combine(root, "out"), "ListHero.Api", Password, null));
        var source = Path.Combine(root, "source");
        using var cert = Certificate(); using var wrong = Certificate(); using var provider = Provider(source, "ListHero.Api", cert);
        _ = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("probe").Protect("secret");
        Assert.ThrowsAny<Exception>(() => KeyRingBackup.Export(source, Path.Combine(root, "wrong"), "ListHero.Api", Password, wrong));
        File.WriteAllText(Directory.GetFiles(source).Single(), "<key><encryptedSecret decryptorType=\"Machine.Bound\" /></key>");
        Assert.Throws<InvalidDataException>(() => KeyRingBackup.Export(source, Path.Combine(root, "machine-bound"), "ListHero.Api", Password, cert));
    }

    [Fact]
    public void Revocation_records_are_preserved_without_unrevoking_keys()
    {
        var root = NewPath(); var source = Path.Combine(root, "source");
        using var cert = Certificate(); using var provider = Provider(source, "ListHero.Api", cert);
        _ = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("probe").Protect("secret");
        var manager = provider.GetRequiredService<IKeyManager>(); var id = manager.GetAllKeys().Single().KeyId;
        manager.RevokeKey(id, "synthetic drill");
        var backup = Path.Combine(root, "backup"); KeyRingBackup.Export(source, backup, "ListHero.Api", Password, cert);
        var output = Path.Combine(root, "restored"); KeyRingBackup.Restore(Path.Combine(backup, "keys.lhb"), output, "ListHero.Api", Password);
        using var recovered = Provider(Path.Combine(output, "keyring"), "ListHero.Api", cert, true);
        Assert.True(recovered.GetRequiredService<IKeyManager>().GetAllKeys().Single().IsRevoked);
    }

    [Fact]
    public async Task Command_exports_the_configured_encrypted_PEM_and_restores_it_without_printing_secrets()
    {
        var root = NewPath(); var source = Path.Combine(root, "source");
        using var cert = Certificate(); using var provider = Provider(source, "ListHero.Web", cert);
        var cipher = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("probe").Protect("secret");
        using var rsa = cert.GetRSAPrivateKey()!;
        var pem = cert.ExportCertificatePem() + "\n" + rsa.ExportEncryptedPkcs8PrivateKeyPem(Password, new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));
        var environment = new Dictionary<string, string>
        {
            ["LISTHERO_BACKUP_PASSPHRASE"] = Password, ["LISTHERO_KEY_CERTIFICATE_PASSWORD"] = Password,
            ["LISTHERO_KEY_CERTIFICATE_BASE64"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pem))
        };
        string? Setting(string name) => environment.GetValueOrDefault(name);
        var backup = Path.Combine(root, "backup"); var restored = Path.Combine(root, "restored"); using var output = new StringWriter();
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["backup-keys", "--application", "ListHero.Web", "--source", source, "--output", backup], Setting, output));
        Assert.Equal(0, await MaintenanceCommand.RunAsync(["restore-keys", "--application", "ListHero.Web", "--source", Path.Combine(backup, "keys.lhb"), "--output", restored], Setting, output));
        Assert.DoesNotContain(Password, output.ToString()); Assert.DoesNotContain("PRIVATE KEY", output.ToString());
        using var recoveredCert = X509Certificate2.CreateFromEncryptedPemFile(Path.Combine(restored, "certificate.pem"), Password);
        using var after = Provider(Path.Combine(restored, "keyring"), "ListHero.Web", recoveredCert, true);
        Assert.Equal("secret", after.GetRequiredService<IDataProtectionProvider>().CreateProtector("probe").Unprotect(cipher));
    }

    [Theory]
    [InlineData("traversal")] [InlineData("duplicate")] [InlineData("large-entry")]
    [InlineData("missing-manifest")] [InlineData("version")] [InlineData("certificate-mismatch")]
    [InlineData("no-keys")] [InlineData("dtd")] [InlineData("root")] [InlineData("decryptor")]
    [InlineData("alternate-stream")] [InlineData("newline")]
    public void Authenticated_but_invalid_bundle_contents_are_refused_before_output(string issue)
    {
        var root = NewPath(); Directory.CreateDirectory(root);
        using var plain = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(plain, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            void Add(string name, string value) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(value); }
            if (issue != "missing-manifest") Add("manifest.json", issue switch
            {
                "version" => "{\"Version\":2,\"Application\":\"ListHero.Api\",\"Certificate\":false}",
                "certificate-mismatch" => "{\"Version\":1,\"Application\":\"ListHero.Api\",\"Certificate\":true}",
                _ => "{\"Version\":1,\"Application\":\"ListHero.Api\",\"Certificate\":false}"
            });
            if (issue == "duplicate") Add("manifest.json", "{}");
            if (issue == "traversal") Add("keyring/../../escape.xml", "<key/>");
            if (issue == "alternate-stream") Add("keyring/key-fake.xml:stream.xml", "<key/>");
            if (issue == "newline") Add("keyring/key-fake.xml\n", "<key/>");
            if (issue == "large-entry") Add("keyring/key-fake.xml", new string('x', 1024 * 1024 + 1));
            if (issue == "dtd") Add("keyring/key-fake.xml", "<!DOCTYPE key [<!ENTITY test SYSTEM 'file:///private-canary'>]><key>&test;</key>");
            if (issue == "root") Add("keyring/key-fake.xml", "<revocation/>");
            if (issue == "decryptor") Add("keyring/key-fake.xml", "<key><encryptedSecret decryptorType=\"Unknown.Type\"/></key>");
        }
        var header = new byte[36]; "LHKEYS01"u8.CopyTo(header); RandomNumberGenerator.Fill(header.AsSpan(8));
        var key = Rfc2898DeriveBytes.Pbkdf2(Password, header.AsSpan(8, 16), 600_000, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[plain.Length]; var tag = new byte[16];
        try { using var aes = new AesGcm(key, 16); aes.Encrypt(header.AsSpan(24, 12), plain.ToArray(), cipher, tag, header); }
        finally { CryptographicOperations.ZeroMemory(key); }
        var file = Path.Combine(root, "invalid.lhb"); File.WriteAllBytes(file, [..header, ..tag, ..cipher]);
        var restored = Path.Combine(root, "not-created");
        Assert.ThrowsAny<Exception>(() => KeyRingBackup.Restore(file, restored, "ListHero.Api", Password));
        Assert.False(Directory.Exists(restored)); Assert.False(File.Exists(Path.Combine(root, "escape.xml")));
    }
}
