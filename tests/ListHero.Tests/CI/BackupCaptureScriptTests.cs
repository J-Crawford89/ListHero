using System.Diagnostics;
using System.Text.Json;
using ListHero.Tests.Persistence;

namespace ListHero.Tests.CI;

public sealed class BackupCaptureScriptTests
{
    [Theory]
    [InlineData("missing-secret", false)] [InlineData("repository-path", false)]
    [InlineData("azure-failure", false)] [InlineData("native-failure", false)]
    [InlineData("invalid-report", false)] [InlineData("success", true)]
    public async Task Actual_wrapper_requires_preflight_and_redacts_unexpected_native_output(string scenario, bool succeeds)
    {
        const string canary = "private-backup-script-canary";
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ListHero.slnx"))) root = root.Parent;
        var directory = KeyRingBackupTests.NewPath(); Directory.CreateDirectory(directory);
        var fakeAzure = Path.Combine(directory, "az.ps1");
        await File.WriteAllTextAsync(fakeAzure, """
            if ($env:LISTHERO_TEST_SCENARIO -eq 'azure-failure') { Write-Output $env:LISTHERO_TEST_CANARY; exit 1 }
            if ($args[1] -eq 'show') { Write-Output '{"id":"656761a1-f733-44d2-85d9-44cb54a2f96d","state":"Enabled"}'; exit 0 }
            if ($args[1] -eq 'get-access-token') { @{subscription='656761a1-f733-44d2-85d9-44cb54a2f96d'; accessToken=$env:LISTHERO_TEST_CANARY} | ConvertTo-Json -Compress; exit 0 }
            throw 'unexpected Azure command'
            """);
        var harness = Path.Combine(directory, "invoke.ps1");
        await File.WriteAllTextAsync(harness, """
            function global:dotnet {
                if ($env:LISTHERO_TEST_SCENARIO -eq 'native-failure') { $global:LASTEXITCODE=1; Write-Output $env:LISTHERO_TEST_CANARY; return }
                $global:LASTEXITCODE=0
                if ($env:LISTHERO_TEST_SCENARIO -eq 'invalid-report') { Write-Output $env:LISTHERO_TEST_CANARY; return }
                Write-Output '{"Outcome":"captured","ArchiveVerified":true,"ReleaseCommit":"0123456789012345678901234567890123456789","Files":11}'
            }
            & $env:LISTHERO_TEST_SCRIPT -OutputDirectory $env:LISTHERO_TEST_OUTPUT -WritersQuiesced -UseSystemAzureCli
            exit $LASTEXITCODE
            """);
        var name = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        var shell = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p, name)).First(File.Exists);
        var start = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "-NoProfile", "-File", harness }) start.ArgumentList.Add(value);
        start.Environment["PATH"] = directory;
        start.Environment["LISTHERO_TEST_CANARY"] = canary;
        start.Environment["LISTHERO_TEST_SCENARIO"] = scenario;
        start.Environment["LISTHERO_TEST_SCRIPT"] = Path.Combine(root!.FullName, "scripts", "Capture-BetaBackup.ps1");
        start.Environment["LISTHERO_TEST_OUTPUT"] = scenario == "repository-path" ? Path.Combine(root.FullName, "unignored-backup") : Path.Combine(directory, "output");
        start.Environment["LISTHERO_BACKUP_PASSPHRASE"] = scenario == "missing-secret" ? "" : canary;
        start.Environment["LISTHERO_MAINTENANCE_CONNECTION"] = canary;
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await process.WaitForExitAsync(timeout.Token);
        var output = await stdout + await stderr;
        Assert.DoesNotContain(canary, output); Assert.Equal(succeeds ? 0 : 1, process.ExitCode);
        using var report = JsonDocument.Parse(output);
        Assert.Equal(succeeds ? "captured" : "failed", report.RootElement.GetProperty("Outcome").GetString());
    }
}
