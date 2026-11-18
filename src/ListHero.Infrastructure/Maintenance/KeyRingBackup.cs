using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Infrastructure.Maintenance;

public sealed record KeyBackupReport(string Application, int KeyFiles, bool CertificateIncluded);

public static partial class KeyRingBackup
{
    private const int Limit = 16 * 1024 * 1024;
    private static readonly byte[] Magic = "LHKEYS01"u8.ToArray();
    public static void NewPrivateDirectory(string path)
    {
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Choose a new output directory.");
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            var sid = WindowsIdentity.GetCurrent().User!;
            var acl = new DirectorySecurity(); acl.SetOwner(sid); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(acl);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public static void RequireApplication(string application)
    {
        if (application is not ("ListHero.Api" or "ListHero.Web")) throw new ArgumentException("Choose the API or web application name.");
    }
    private static void RequirePassphrase(string passphrase)
    {
        if (passphrase.Length is < 16 or > 1024) throw new ArgumentException("Use a backup passphrase of 16–1024 characters.");
    }

    public static KeyBackupReport Export(string source, string output, string application, string passphrase, X509Certificate2? certificate)
    {
        RequireApplication(application); RequirePassphrase(passphrase);
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in new DirectoryInfo(source).GetFiles("*.xml", SearchOption.TopDirectoryOnly))
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > 1024 * 1024) throw new InvalidDataException("Unsupported key file.");
                var data = File.ReadAllBytes(file.FullName); ValidateXml(file.Name, data);
                entries.Add("keyring/" + file.Name, data);
            }
            if (!entries.Keys.Any(name => name.StartsWith("keyring/key-", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("The key ring contains no keys.");
            ValidateDecryptable(source, application, certificate);
            if (certificate is not null)
            {
                if (!certificate.HasPrivateKey) throw new InvalidDataException("The key-protection certificate needs its private key.");
                entries.Add("certificate.pfx", certificate.Export(X509ContentType.Pfx));
            }
            entries.Add("manifest.json", JsonSerializer.SerializeToUtf8Bytes(new Manifest(1, application, certificate is not null)));
            if (entries.Values.Sum(value => (long)value.Length) > Limit) throw new InvalidDataException("The key bundle is too large.");
            using var zipBytes = new MemoryStream();
            using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
                foreach (var entry in entries)
                {
                    using var target = zip.CreateEntry(entry.Key).Open(); target.Write(entry.Value);
                }
            var plain = zipBytes.ToArray();
            try
            {
                if (plain.Length > Limit) throw new InvalidDataException("The key bundle is too large.");
                var header = new byte[36]; Magic.CopyTo(header, 0);
                RandomNumberGenerator.Fill(header.AsSpan(8, 16)); RandomNumberGenerator.Fill(header.AsSpan(24, 12));
                var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, header.AsSpan(8, 16), 600_000, HashAlgorithmName.SHA256, 32);
                var cipher = new byte[plain.Length]; var tag = new byte[16];
                try { using var aes = new AesGcm(key, 16); aes.Encrypt(header.AsSpan(24, 12), plain, cipher, tag, header); }
                finally { CryptographicOperations.ZeroMemory(key); }
                NewPrivateDirectory(output);
                using var file = new FileStream(Path.Combine(output, "keys.lhb"), FileMode.CreateNew, FileAccess.Write);
                file.Write(header); file.Write(tag); file.Write(cipher);
            }
            finally { CryptographicOperations.ZeroMemory(plain); if (zipBytes.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan()); }
            return new(application, entries.Count - (certificate is null ? 1 : 2), certificate is not null);
        }
        finally { foreach (var value in entries.Values) CryptographicOperations.ZeroMemory(value); }
    }

    public static KeyBackupReport Restore(string bundle, string output, string application, string passphrase)
    {
        RequireApplication(application); RequirePassphrase(passphrase);
        var length = new FileInfo(bundle).Length;
        if (length is < 53 or > Limit + 52) throw new InvalidDataException("Invalid bundle size.");
        var bytes = File.ReadAllBytes(bundle);
        if (!bytes.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("Unsupported bundle version.");
        var plain = new byte[bytes.Length - 52];
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, bytes.AsSpan(8, 16), 600_000, HashAlgorithmName.SHA256, 32);
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(bytes.AsSpan(24, 12), bytes.AsSpan(52), bytes.AsSpan(36, 16), plain, bytes.AsSpan(0, 36));
            using var zip = new ZipArchive(new MemoryStream(plain), ZipArchiveMode.Read);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.Length > 1024 * 1024 || (total += entry.Length) > Limit || entries.Count >= 4096) throw new InvalidDataException("Invalid bundle contents.");
                var name = entry.FullName;
                if (name is not ("manifest.json" or "certificate.pfx") && !(name.StartsWith("keyring/", StringComparison.Ordinal) && IsXmlName(name[8..])))
                    throw new InvalidDataException("Unsupported bundle entry.");
                using var input = entry.Open(); using var data = new MemoryStream();
                var buffer = new byte[8192]; int count;
                while ((count = input.Read(buffer)) != 0)
                {
                    if (data.Length + count > entry.Length) throw new InvalidDataException("Invalid entry length.");
                    data.Write(buffer, 0, count);
                }
                if (data.Length != entry.Length) throw new InvalidDataException("Invalid entry length.");
                var value = data.ToArray();
                if (name.StartsWith("keyring/", StringComparison.Ordinal)) ValidateXml(name[8..], value);
                if (!entries.TryAdd(name, value)) throw new InvalidDataException("Duplicate bundle entry.");
            }
            if (!entries.TryGetValue("manifest.json", out var manifestBytes)) throw new InvalidDataException("Missing manifest.");
            var manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes) ?? throw new InvalidDataException("Missing manifest.");
            if (manifest.Version != 1 || manifest.Application != application || manifest.Certificate != entries.ContainsKey("certificate.pfx")) throw new InvalidDataException("The backup does not match this application.");
            var files = entries.Where(entry => entry.Key.StartsWith("keyring/", StringComparison.Ordinal)).ToArray();
            if (!files.Any(entry => entry.Key.StartsWith("keyring/key-", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("The backup contains no keys.");
            using var certificate = manifest.Certificate ? X509CertificateLoader.LoadPkcs12(entries["certificate.pfx"], null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet) : null;
            NewPrivateDirectory(output); var ring = Path.Combine(output, "keyring"); Directory.CreateDirectory(ring);
            foreach (var entry in files) WriteNew(Path.Combine(output, entry.Key.Replace('/', Path.DirectorySeparatorChar)), entry.Value);
            if (certificate is not null)
            {
                using var rsa = certificate.GetRSAPrivateKey() ?? throw new InvalidDataException("Unsupported certificate key.");
                var pem = certificate.ExportCertificatePem() + "\n" + rsa.ExportEncryptedPkcs8PrivateKeyPem(passphrase, new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
                WriteNew(Path.Combine(output, "certificate.pem"), Encoding.UTF8.GetBytes(pem));
            }
            ValidateDecryptable(ring, application, certificate);
            return new(application, files.Length, manifest.Certificate);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain);
            foreach (var value in entries.Values) CryptographicOperations.ZeroMemory(value);
        }
    }
    [GeneratedRegex(@"\A(?:key|revocation)-[A-Za-z0-9-]+\.xml\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyFilePattern();
    private static bool IsXmlName(string name) => KeyFilePattern().IsMatch(name);
    private static void ValidateXml(string name, byte[] data)
    {
        if (!IsXmlName(name)) throw new InvalidDataException("Unsupported key file name.");
        using var reader = XmlReader.Create(new MemoryStream(data), new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var xml = XDocument.Load(reader);
        if (xml.Root?.Name != (name.StartsWith("key-", StringComparison.OrdinalIgnoreCase) ? "key" : "revocation")) throw new InvalidDataException("Unsupported key XML.");
        if (xml.Descendants("encryptedSecret").Any(element => !(element.Attribute("decryptorType")?.Value.StartsWith("Microsoft.AspNetCore.DataProtection.XmlEncryption.CertificateXmlDecryptor,", StringComparison.Ordinal) ?? false)))
            throw new InvalidDataException("Machine-bound key encryption cannot be exported as a portable bundle.");
    }
    private static void ValidateDecryptable(string ring, string application, X509Certificate2? certificate)
    {
        var services = new ServiceCollection();
        var builder = services.AddDataProtection().SetApplicationName(application).PersistKeysToFileSystem(new(ring)).DisableAutomaticKeyGeneration();
        if (certificate is not null) builder.UnprotectKeysWithAnyCertificate(certificate);
        using var provider = services.BuildServiceProvider();
        var keys = provider.GetRequiredService<IKeyManager>().GetAllKeys();
        if (keys.Count == 0) throw new InvalidDataException("The key ring contains no readable keys.");
        foreach (var item in keys)
            _ = item.CreateEncryptor() ?? throw new CryptographicException("Key material is unavailable.");
    }
    private static void WriteNew(string path, byte[] value)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write); file.Write(value);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private sealed record Manifest(int Version, string Application, bool Certificate);
}
