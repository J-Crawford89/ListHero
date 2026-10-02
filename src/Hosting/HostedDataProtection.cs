using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Hosting;

// Linked into the two hosts; client and domain projects don't depend on Azure hosting.
internal static class HostedDataProtection
{
    public static void AddHostedDataProtection(this IServiceCollection services, IConfiguration configuration, string applicationName)
    {
        if (!configuration.GetValue<bool>("DataProtection:RequirePersistentKeys")) return;
        var certificateBase64 = configuration["DataProtection:CertificateBase64"];
        if (string.IsNullOrWhiteSpace(certificateBase64))
            throw new InvalidOperationException("Persistent Data Protection requires its encryption certificate.");
        var directory = configuration["DataProtection:KeyRingPath"];
        if (string.IsNullOrWhiteSpace(directory))
        {
            var appServiceHome = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrWhiteSpace(appServiceHome) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID")))
                throw new InvalidOperationException("Configure a persistent Data Protection key directory.");
            directory = Path.Combine(appServiceHome, "data", applicationName, "keys");
        }
        if (!Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("The Data Protection key directory must be an absolute persistent path.");
        // Windows Free App Service restricts native PFX import. PEM attaches an in-memory RSA key
        // without importing a private key through the Windows certificate store.
        var certificatePem = Encoding.UTF8.GetString(Convert.FromBase64String(certificateBase64));
        var certificate = X509Certificate2.CreateFromEncryptedPem(certificatePem, certificatePem,
            configuration["DataProtection:CertificatePassword"] ?? string.Empty);
        if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
        {
            certificate.Dispose();
            throw new InvalidOperationException("Data Protection requires an unexpired certificate with its private key.");
        }
        Directory.CreateDirectory(directory);
        // Let the service provider own the certificate used by the key encryptor.
        services.AddSingleton(_ => certificate);
        services.AddDataProtection().SetApplicationName(applicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(directory)).ProtectKeysWithCertificate(certificate);
    }
}
