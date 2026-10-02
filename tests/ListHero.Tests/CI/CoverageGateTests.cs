using System.Diagnostics;

namespace ListHero.Tests.CI;

public sealed class CoverageGateTests
{
    [Theory]
    [InlineData("pass", "Coverage requirements passed")]
    [InlineData("line", "below its coverage minimum")]
    [InlineData("branch", "below its coverage minimum")]
    [InlineData("missing", "Missing or duplicate assembly")]
    [InlineData("skipped", "must execute every test")]
    [InlineData("failed", "must complete successfully")]
    [InlineData("aborted", "must complete successfully")]
    [InlineData("empty", "must complete successfully")]
    public async Task Coverage_gate_accepts_a_successful_run_and_rejects_regressions(string scenario, string message)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ListHero.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var directory = Path.Combine(root.FullName, ".artifacts", "coverage-gate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var coverage = Path.Combine(directory, "coverage.xml");
        var results = Path.Combine(directory, "results.trx");
        var thresholds = Path.Combine(directory, "thresholds.json");
        var package = scenario == "missing" ? "Other.Assembly" : "ListHero.Client";
        var lineRate = scenario == "line" ? "0.5" : "0.95";
        var branchRate = scenario == "branch" ? "0.5" : "0.9";
        await File.WriteAllTextAsync(coverage, $"<coverage><packages><package name=\"{package}\" line-rate=\"{lineRate}\" branch-rate=\"{branchRate}\" /></packages></coverage>");
        await File.WriteAllTextAsync(thresholds, "{\"ListHero.Client\": {\"line\": 90, \"branch\": 80}}");
        await File.WriteAllTextAsync(results, $"<TestRun><ResultSummary outcome=\"{(scenario == "aborted" ? "Aborted" : "Completed")}\"><Counters total=\"{(scenario == "empty" ? 0 : 1)}\" executed=\"{(scenario is "skipped" or "empty" ? 0 : 1)}\" failed=\"{(scenario == "failed" ? 1 : 0)}\" notExecuted=\"{(scenario == "skipped" ? 1 : 0)}\" /></ResultSummary></TestRun>");
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(root.FullName, "scripts", "Test-Coverage.ps1"),
            "-CoverageFile", coverage, "-TestResultsFile", results, "-ThresholdsFile", thresholds, "-RequireAllTests" })
            start.ArgumentList.Add(argument);
        // Synthetic gate checks should not write to the hosted run's summary.
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        var text = await output + await error;
        Assert.Contains(message, text);
        if (scenario == "pass") Assert.Equal(0, process.ExitCode); else Assert.NotEqual(0, process.ExitCode);
    }
}
