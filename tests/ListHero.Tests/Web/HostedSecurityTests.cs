using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web.TokenCacheProviders.Distributed;
using Microsoft.AspNetCore.Mvc.Testing;
using ListHero.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListHero.Tests.Web;

public sealed partial class WebHostTests
{
    [Fact]
    public async Task Hosted_keys_are_encrypted_and_survive_a_host_restart()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ListHero test key encryption", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var directory = Path.Combine(FindRepository(), ".artifacts", "hosting-key-tests", Guid.NewGuid().ToString("N"));
        var pem = certificate.ExportCertificatePem() + "\n" + rsa.ExportEncryptedPkcs8PrivateKeyPem("test-key-password",
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));
        var settings = HostedSettings(directory, Convert.ToBase64String(Encoding.UTF8.GetBytes(pem)));
        string protectedValue;
        await using (var factory = new WebFactory().WithWebHostBuilder(builder => ApplySettings(builder, settings)))
        {
            protectedValue = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-check").Protect("private payload");
            Assert.IsType<SqlServerCache>(factory.Services.GetRequiredService<IDistributedCache>());
            Assert.True(factory.Services.GetRequiredService<IOptions<MsalDistributedTokenCacheAdapterOptions>>().Value.Encrypt);
        }
        Assert.All(Directory.GetFiles(directory, "key-*.xml"), path =>
        {
            var xml = File.ReadAllText(path);
            Assert.Contains("encryptedSecret", xml);
            Assert.DoesNotContain("<masterKey", xml);
        });
        await using var restarted = new WebFactory().WithWebHostBuilder(builder => ApplySettings(builder, settings));
        Assert.Equal("private payload", restarted.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-check").Unprotect(protectedValue));
        var otherApplication = DataProtectionProvider.Create(new DirectoryInfo(directory), builder => builder
            .SetApplicationName("ListHero.Api").ProtectKeysWithCertificate(certificate));
        Assert.Throws<CryptographicException>(() => otherApplication.CreateProtector("restart-check").Unprotect(protectedValue));
        var apiSettings = new Dictionary<string, string>(settings)
        {
            ["Authentication:Enabled"] = "false",
            ["ConnectionStrings:ListHero"] = "Server=(localdb)\\MSSQLLocalDB;Database=NotAccessed;Integrated Security=true;TrustServerCertificate=true",
            ["Database:EnableRetryOnFailure"] = "true"
        };
        string apiProtected;
        await using (var api = new WebApplicationFactory<ListHero.Api.ApiAssemblyMarker>().WithWebHostBuilder(builder =>
            { builder.UseEnvironment("Development"); ApplySettings(builder, apiSettings); }))
        {
            var protector = api.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-check");
            Assert.Throws<CryptographicException>(() => protector.Unprotect(protectedValue));
            apiProtected = protector.Protect("private share credential");
            using var scope = api.Services.CreateScope();
            Assert.True(scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.CreateExecutionStrategy().RetriesOnFailure);
        }
        await using var restartedApi = new WebApplicationFactory<ListHero.Api.ApiAssemblyMarker>().WithWebHostBuilder(builder =>
            { builder.UseEnvironment("Development"); ApplySettings(builder, apiSettings); });
        Assert.Equal("private share credential", restartedApi.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-check").Unprotect(apiProtected));
    }

    [Theory]
    [InlineData("ConnectionStrings:TokenCache", "", "persistent token cache")]
    [InlineData("DataProtection:CertificateBase64", "", "encryption certificate")]
    [InlineData("DataProtection:KeyRingPath", "relative/path", "absolute persistent path")]
    public void Hosted_security_rejects_incomplete_configuration(string key, string value, string message)
    {
        var settings = HostedSettings(Path.GetFullPath(".artifacts/unused-keys"), "unused");
        settings[key] = value;
        using var factory = new WebFactory().WithWebHostBuilder(builder => ApplySettings(builder, settings));
        Assert.Contains(message, Assert.Throws<InvalidOperationException>(() => factory.CreateClient()).Message);
    }

    private static Dictionary<string, string> HostedSettings(string directory, string certificate) => new()
    {
        ["DataProtection:RequirePersistentKeys"] = "true",
        ["DataProtection:KeyRingPath"] = directory,
        ["DataProtection:CertificateBase64"] = certificate,
        ["DataProtection:CertificatePassword"] = "test-key-password",
        ["ConnectionStrings:TokenCache"] = "Server=(localdb)\\MSSQLLocalDB;Database=NotAccessed;Integrated Security=true;TrustServerCertificate=true"
    };
    private static void ApplySettings(IWebHostBuilder builder, Dictionary<string, string> settings)
    {
        foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
    }
    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ListHero.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository not found.");
    }
}
