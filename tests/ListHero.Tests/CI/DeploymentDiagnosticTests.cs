using System.Diagnostics;
using System.Text.Json;

namespace ListHero.Tests.CI;

public sealed class DeploymentDiagnosticTests
{
    private const string Secret = "private-connection-credential-error-canary";
    [Theory]
    [InlineData("package")] [InlineData("authentication")] [InlineData("migration")] [InlineData("firewall-cleanup")]
    public async Task Deployment_summaries_emit_only_allowlisted_metadata(string stage)
    {
        var root = Root();
        var output = Path.Combine(root, ".artifacts", "deployment-diagnostic-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var harness = Path.Combine(output, "record.ps1");
        var source = Path.Combine(root, "scripts", "DeploymentDiagnostics.Common.ps1").Replace("'", "''");
        await File.WriteAllTextAsync(harness, $". '{source}'\nWrite-BetaDeploymentDiagnostic -Stage $args[0] -Outcome failure -ElapsedMs 42\n");
        var result = await RunAsync(harness, output, stage);
        Assert.Equal(0, result.Exit);
        var json = await File.ReadAllTextAsync(Path.Combine(output, "deployment-diagnostic.json"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(stage, doc.RootElement.GetProperty("Stage").GetString());
        Assert.Equal("unknown", doc.RootElement.GetProperty("Commit").GetString());
        Assert.Equal("local", doc.RootElement.GetProperty("Run").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("ElapsedMs").GetInt64());
        var summary = await File.ReadAllTextAsync(Path.Combine(output, "summary.md"));
        Assert.Contains(stage, summary);
        Assert.DoesNotContain(Secret, result.Text + json + summary);
    }

    [Fact]
    public async Task Real_deployment_script_reports_a_redacted_preflight_failure_without_an_Azure_CLI_or_network()
    {
        var root = Root();
        var output = Path.Combine(root, ".artifacts", "deployment-diagnostic-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var result = await RunAsync(Path.Combine(root, "scripts", "Deploy-CiBeta.ps1"), output, emptyPath: true);
        Assert.NotEqual(0, result.Exit);
        Assert.Contains("preflight", result.Text);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "deployment-diagnostic.json")));
        Assert.Equal("preflight", doc.RootElement.GetProperty("Stage").GetString());
        Assert.Equal("failure", doc.RootElement.GetProperty("Outcome").GetString());
        Assert.DoesNotContain(Secret, result.Text + doc.RootElement.GetRawText());
    }

    [Fact]
    public async Task Native_migration_failure_output_is_withheld_before_writing_the_phase_diagnostic()
    {
        var root = Root();
        var output = Path.Combine(root, ".artifacts", "deployment-diagnostic-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var executable = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        var shell = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(path => Path.Combine(path, executable)).First(File.Exists);
        var native = Path.Combine(output, "fake-migration.ps1");
        await File.WriteAllTextAsync(native, "[Console]::Error.WriteLine($env:LISTHERO_DEPLOY_ADMIN_CONNECTION)\nexit 9\n");
        var harness = Path.Combine(output, "migration-harness.ps1");
        var source = Path.Combine(root, "scripts", "DeploymentDiagnostics.Common.ps1").Replace("'", "''");
        await File.WriteAllTextAsync(harness, $". '{source}'\ntry {{ Invoke-BetaMigration -Assembly '{native.Replace("'", "''")}' -Dotnet '{shell.Replace("'", "''")}' }} catch {{ Write-BetaDeploymentDiagnostic -Stage migration -Outcome failure; throw 'Migration failure recorded without native output.' }}\n");
        var result = await RunAsync(harness, output);
        Assert.NotEqual(0, result.Exit);
        Assert.Contains("Migration failure recorded", result.Text);
        var json = await File.ReadAllTextAsync(Path.Combine(output, "deployment-diagnostic.json"));
        Assert.Contains("migration", json);
        Assert.DoesNotContain(Secret, result.Text + json);
    }

    private static async Task<(int Exit, string Text)> RunAsync(string script, string output, string? stage = null, bool emptyPath = false)
    {
        var executable = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        var shell = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(path => Path.Combine(path, executable)).First(File.Exists);
        var start = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-File", script }) start.ArgumentList.Add(arg);
        if (stage is not null) start.ArgumentList.Add(stage);
        start.Environment["LISTHERO_DEPLOY_ADMIN_CONNECTION"] = Secret;
        start.Environment["GITHUB_SHA"] = Secret;
        start.Environment["GITHUB_RUN_ID"] = Secret;
        start.Environment["GITHUB_STEP_SUMMARY"] = Path.Combine(output, "summary.md");
        start.Environment["LISTHERO_DIAGNOSTIC_DIRECTORY"] = output;
        if (emptyPath) start.Environment["PATH"] = output; // Get-Command az fails before any Azure operation.
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout + await stderr);
    }
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ListHero.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository not found.");
    }
}
