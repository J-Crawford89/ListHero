using System.Diagnostics;
using System.Text.Json;

namespace ListHero.Tests.CI;

public sealed class CredentialExpiryTests
{
    private const string Canary = "private-secret-cert-error-canary";
    private static readonly string[] Ids = ["entra-web-secret", "api-key-certificate", "web-key-certificate"];
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ListHero.slnx"))) directory = directory.Parent;
        return directory!.FullName;
    }
    private static object Entry(string id, string? expiry, string source = "operator") => new { id, expiresOn = expiry, source, recordedOn = "2030-01-01" };
    private static string Inventory(params object[] entries) => JsonSerializer.Serialize(new { version = 1, credentials = entries.Length == 0 ? Ids.Select(id => Entry(id, "2031-01-01")).ToArray() : entries });

    [Theory]
    [InlineData("2030-01-31T00:00:00Z", "due", 30, 1)]
    [InlineData("2030-01-30T23:59:59Z", "healthy", 31, 0)]
    [InlineData("2030-02-23T00:00:00Z", "urgent", 7, 1)]
    [InlineData("2030-02-22T23:59:59Z", "due", 8, 1)]
    [InlineData("2030-03-02T00:00:00Z", "expired", 0, 1)]
    [InlineData("2030-03-02T00:00:01Z", "expired", 0, 1)]
    [InlineData("2030-03-03T00:00:00Z", "expired", -1, 1)]
    public async Task Actual_checker_observes_exact_UTC_warning_urgent_and_expired_boundaries(string now, string expected, int days, int exit)
    {
        var json = Inventory(Entry(Ids[0], "2030-03-02"), Entry(Ids[1], "2031-01-01"), Entry(Ids[2], "2031-01-01"));
        var result = await Check(json, now);
        Assert.Equal(exit, result.Exit);
        using var report = Report(result.Output);
        var row = report.RootElement.GetProperty("Credentials")[0];
        Assert.Equal(expected, row.GetProperty("Status").GetString()); Assert.Equal(days, row.GetProperty("DaysRemaining").GetInt32());
        Assert.Equal(exit == 1, report.RootElement.GetProperty("HasAlerts").GetBoolean());
        Assert.DoesNotContain(Canary, result.Output + result.Summary);
    }

    [Fact]
    public async Task Missing_dates_alert_instead_of_passing_and_every_credential_is_independently_monitored()
    {
        var result = await Check(Inventory(Entry(Ids[0], null, "unknown"), Entry(Ids[1], "2029-01-01"), Entry(Ids[2], "2030-01-03")));
        Assert.Equal(1, result.Exit);
        using var report = Report(result.Output);
        Assert.Equal(new[] { "unknown", "expired", "urgent" }, report.RootElement.GetProperty("Credentials").EnumerateArray().Select(row => row.GetProperty("Status").GetString()));
        Assert.Contains("Entra web sign-in secret", result.Summary); Assert.Contains("API key-protection", result.Summary); Assert.Contains("Web key-protection", result.Summary);
    }

    public static IEnumerable<object[]> InvalidInventories()
    {
        yield return ["{"]; yield return ["null"];
        yield return ["{\"version\":2,\"credentials\":[]}"];
        yield return [Inventory(Entry(Ids[0], "2031-01-01"), Entry(Ids[0], "2031-01-01"), Entry(Ids[2], "2031-01-01"))];
        yield return [Inventory(Entry(Canary, "2031-01-01"), Entry(Ids[1], "2031-01-01"), Entry(Ids[2], "2031-01-01"))];
        yield return [Inventory(Entry(Ids[0], Canary), Entry(Ids[1], "2031-01-01"), Entry(Ids[2], "2031-01-01"))];
        yield return [Inventory(Entry(Ids[0], "2031-01-01", Canary), Entry(Ids[1], "2031-01-01"), Entry(Ids[2], "2031-01-01"))];
        yield return [Inventory(Entry(Ids[0], null), Entry(Ids[1], "2031-01-01"), Entry(Ids[2], "2031-01-01"))];
        yield return [Inventory(Entry(Ids[0], "2031-02-29"), Entry(Ids[1], "2031-01-01"), Entry(Ids[2], "2031-01-01"))];
        yield return [Inventory(Entry(Ids[0], "2031-01-01"))];
        yield return [Inventory().Replace("\"version\":1", "\"version\":\"1\"")];
        yield return [Inventory().Replace("\"id\":\"entra-web-secret\"", "\"id\":\"entra-web-secret\",\"secret\":\"" + Canary + "\"")];
        yield return [new string('x', 16385)];
    }

    [Theory, MemberData(nameof(InvalidInventories))]
    public async Task Invalid_inventory_has_a_safe_failure_without_echoing_input_or_creating_a_healthy_report(string json)
    {
        var result = await Check(json);
        Assert.Equal(2, result.Exit); Assert.Contains("Raw details were withheld", result.Output);
        Assert.DoesNotContain(Canary, result.Output + result.Summary); Assert.DoesNotContain("healthy", result.Output);
    }

    [Fact]
    public async Task Missing_file_bad_clock_and_unwritable_summary_fail_safely()
    {
        var result = await Check(Inventory(), missing: true); Assert.Equal(2, result.Exit); Assert.DoesNotContain(Canary, result.Output);
        result = await Check(Inventory(), Canary); Assert.Equal(2, result.Exit); Assert.DoesNotContain(Canary, result.Output);
        result = await Check(Inventory(), badSummary: true); Assert.Equal(2, result.Exit); Assert.DoesNotContain(Canary, result.Output);
    }

    [Fact]
    public async Task Operator_updates_only_the_selected_record_and_does_not_need_any_cloud_tool()
    {
        var directory = NewDirectory(); var path = Path.Combine(directory, "inventory.json"); await File.WriteAllTextAsync(path, Inventory());
        var result = await Run("Update-CredentialExpiry.ps1", directory, ["-InventoryPath", path, "-CredentialId", Ids[0], "-ExpiresOn", "2032-02-29"]);
        Assert.Equal(0, result.Exit); Assert.DoesNotContain(Canary, result.Output);
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(path)); var rows = data.RootElement.GetProperty("credentials");
        Assert.Equal("2032-02-29", rows[0].GetProperty("expiresOn").GetString()); Assert.Equal("operator", rows[0].GetProperty("source").GetString());
        Assert.Equal("2031-01-01", rows[1].GetProperty("expiresOn").GetString());
        result = await Run("Update-CredentialExpiry.ps1", directory, ["-InventoryPath", path, "-CredentialId", Canary, "-ExpiresOn", Canary]);
        Assert.Equal(2, result.Exit); Assert.DoesNotContain(Canary, result.Output);
    }

    [Fact]
    public async Task Backup_import_exports_only_public_expiry_dates_from_a_synthetic_protected_backup()
    {
        var directory = NewDirectory(); var path = Path.Combine(directory, "inventory.json"); var backup = Path.Combine(directory, "synthetic.dpapi");
        await File.WriteAllTextAsync(path, Inventory());
        if (OperatingSystem.IsWindows())
        {
            var harness = Path.Combine(directory, "make.ps1");
            await File.WriteAllTextAsync(harness, """
                $rsa=[Security.Cryptography.RSA]::Create(2048)
                try {
                  $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=synthetic',$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
                  $cert=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1),[DateTimeOffset]::new(2033,1,2,0,0,0,[TimeSpan]::Zero))
                  try {
                    $pem=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($cert.ExportCertificatePem()))
                    $clear=[Text.Encoding]::UTF8.GetBytes((@{apiProtectionCertificate=$pem;webProtectionCertificate=$pem;password=$env:LISTHERO_TEST_CANARY} | ConvertTo-Json))
                    try { [IO.File]::WriteAllBytes($args[0],[Security.Cryptography.ProtectedData]::Protect($clear,[Text.Encoding]::UTF8.GetBytes('ListHero free beta deployment v1'),[Security.Cryptography.DataProtectionScope]::CurrentUser)) }
                    finally { [Array]::Clear($clear,0,$clear.Length) }
                  } finally { $cert.Dispose() }
                } finally { $rsa.Dispose() }
                """);
            Assert.Equal(0, (await Run(harness, directory, [backup])).Exit);
        }
        var result = await Run("Update-CredentialExpiry.ps1", directory, ["-InventoryPath", path, "-ReadLocalCertificateBackup", "-CertificateBackupPath", backup]);
        Assert.Equal(OperatingSystem.IsWindows() ? 0 : 2, result.Exit); Assert.DoesNotContain(Canary, result.Output);
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        if (OperatingSystem.IsWindows())
        {
            foreach (var row in data.RootElement.GetProperty("credentials").EnumerateArray().Skip(1))
            { Assert.Equal("2033-01-02", row.GetProperty("expiresOn").GetString()); Assert.Equal("local-backup", row.GetProperty("source").GetString()); }
        }
        Assert.DoesNotContain(Canary, data.RootElement.GetRawText());
    }

    [Fact]
    public void Alert_workflow_is_separate_read_only_and_has_no_Azure_identity_secrets_or_deployment_step()
    {
        var workflow = File.ReadAllText(Path.Combine(Root(), ".github/workflows/credential-expiry.yml"));
        Assert.Contains("schedule:", workflow); Assert.Contains("workflow_dispatch:", workflow); Assert.Contains("contents: read", workflow);
        Assert.Contains("37 14 * * *", workflow); Assert.Contains("Test-CredentialExpiry.ps1", workflow);
        Assert.Contains("test_notification:", workflow); Assert.Contains("type: boolean", workflow); Assert.Contains("default: false", workflow);
        Assert.Contains("Intentional failure", workflow);
        foreach (var prohibited in new[] { "secrets.", "id-token:", "Azure/login", "Deploy-", "webapp", "environment:", "push:", "pull_request:" }) Assert.DoesNotContain(prohibited, workflow);
        foreach (var script in new[] { "Test-CredentialExpiry.ps1", "CredentialExpiry.Common.ps1" })
        {
            var text = File.ReadAllText(Path.Combine(Root(), "scripts", script));
            foreach (var prohibited in new[] { "Invoke-RestMethod", "Invoke-WebRequest", "Get-Command az", "Connect-Azure", "beta-secrets.dpapi" }) Assert.DoesNotContain(prohibited, text);
        }
    }

    private static JsonDocument Report(string output) => JsonDocument.Parse(output[output.IndexOf('{')..]);
    private static string NewDirectory() { var path = Path.Combine(Root(), ".artifacts", "credential-expiry-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static async Task<(int Exit, string Output, string Summary)> Check(string json, string now = "2030-01-01T00:00:00Z", bool missing = false, bool badSummary = false)
    {
        var directory = NewDirectory(); var path = Path.Combine(directory, "inventory.json");
        if (!missing) await File.WriteAllTextAsync(path, json);
        return await Run("Test-CredentialExpiry.ps1", directory, ["-InventoryPath", path, "-AsOfUtc", now], badSummary);
    }
    private static async Task<(int Exit, string Output, string Summary)> Run(string script, string directory, string[] args, bool badSummary = false)
    {
        var name = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        var shell = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(path => Path.Combine(path, name)).First(File.Exists);
        var summary = Path.Combine(directory, "summary.md");
        var start = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "-NoProfile", "-File", Path.IsPathFullyQualified(script) ? script : Path.Combine(Root(), "scripts", script) }.Concat(args)) start.ArgumentList.Add(value);
        start.Environment["PATH"] = directory; // No CLI/cloud commands can be resolved by the child.
        start.Environment["LISTHERO_TEST_CANARY"] = Canary;
        start.Environment["GITHUB_STEP_SUMMARY"] = badSummary ? directory : summary;
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout + await stderr, File.Exists(summary) ? await File.ReadAllTextAsync(summary) : "");
    }
}
