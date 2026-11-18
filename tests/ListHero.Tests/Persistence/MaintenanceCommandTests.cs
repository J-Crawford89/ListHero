using System.Security.Cryptography;
using ListHero.Infrastructure.Maintenance;

namespace ListHero.Tests.Persistence;

public sealed class MaintenanceCommandTests
{
    [Theory]
    [InlineData("retention --database Synthetic", "retention", false, 1000)]
    [InlineData("retention --database Synthetic --apply --batch 2", "retention", true, 2)]
    [InlineData("test-databases", "test-databases", false, 5)]
    [InlineData("test-databases --database Exact --apply --batch 1", "test-databases", true, 1)]
    [InlineData("backup-keys --application ListHero.Api --source source --output new", "backup-keys", false, 1000)]
    [InlineData("restore-keys --application ListHero.Web --source source --output new", "restore-keys", false, 1000)]
    [InlineData("restore-drill --output new", "restore-drill", false, 1000)]
    public async Task Commands_have_explicit_targets_and_default_to_preview(string arguments, string command, bool apply, int batch)
    {
        var fake = new FakeOperations(); using var output = new StringWriter();
        Assert.Equal(0, await MaintenanceCommand.RunAsync(arguments.Split(' '), _ => null, output, fake));
        Assert.Equal(command, fake.Request!.Command); Assert.Equal(apply, fake.Request.Apply); Assert.Equal(batch, fake.Request.Batch);
        Assert.Contains("complete", output.ToString());
    }

    [Theory]
    [InlineData("")] [InlineData("--help")]
    public async Task Help_never_connects(string arguments)
    {
        var fake = new FakeOperations(); using var output = new StringWriter();
        Assert.Equal(0, await MaintenanceCommand.RunAsync(arguments.Length == 0 ? [] : [arguments], _ => null, output, fake));
        Assert.Null(fake.Request); Assert.Contains("retention-and-restore.md", output.ToString());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("retention")]
    [InlineData("retention --database")]
    [InlineData("retention --database --apply")]
    [InlineData("retention --database A --database B")]
    [InlineData("retention --database A --unexpected x")]
    [InlineData("retention --database A --batch zero")]
    [InlineData("retention --database A --batch 0")]
    [InlineData("retention --database A --batch 1001")]
    [InlineData("test-databases --apply")]
    [InlineData("test-databases --batch 6")]
    [InlineData("restore-drill --output new --apply")]
    public async Task Invalid_arguments_never_reach_operations(string arguments)
    {
        var fake = new FakeOperations(); using var output = new StringWriter();
        Assert.Equal(1, await MaintenanceCommand.RunAsync(arguments.Split(' '), _ => null, output, fake));
        Assert.Null(fake.Request); Assert.Contains("invalid-arguments", output.ToString());
    }

    [Theory]
    [InlineData("crypto", "backup-authentication-or-key-failure")]
    [InlineData("data", "invalid-backup")]
    [InlineData("io", "filesystem-operation-failed")]
    [InlineData("access", "filesystem-operation-failed")]
    [InlineData("argument", "invalid-arguments")]
    [InlineData("cancel", "canceled")]
    [InlineData("unknown", "operation-failed")]
    public async Task Failure_output_never_includes_exception_or_credential_details(string kind, string reason)
    {
        const string secret = "private-credential-canary";
        Exception failure = kind switch
        {
            "crypto" => new CryptographicException(secret), "data" => new InvalidDataException(secret),
            "io" => new IOException(secret), "access" => new UnauthorizedAccessException(secret),
            "argument" => new ArgumentException(secret), "cancel" => new OperationCanceledException(secret), _ => new Exception(secret)
        };
        using var output = new StringWriter();
        Assert.Equal(1, await MaintenanceCommand.RunAsync(["test-databases"], _ => secret, output, new FakeOperations(failure)));
        Assert.Contains(reason, output.ToString()); Assert.DoesNotContain(secret, output.ToString());
    }

    [Theory]
    [InlineData("retention --database A")]
    [InlineData("test-databases")]
    [InlineData("restore-drill --output new")]
    [InlineData("backup-keys --application ListHero.Api --source none --output new")]
    [InlineData("restore-keys --application ListHero.Web --source none --output new")]
    public async Task Missing_environment_settings_are_reported_safely(string arguments)
    {
        using var output = new StringWriter();
        Assert.Equal(1, await MaintenanceCommand.RunAsync(arguments.Split(' '), _ => null, output));
        Assert.Contains("invalid-arguments", output.ToString());
    }

    [Fact]
    public async Task Destructive_operations_refuse_ambiguous_remote_and_invalid_targets_before_connecting()
    {
        var maintenance = new RetentionMaintenance(TimeProvider.System);
        const string local = @"Server=(localdb)\MSSQLLocalDB;Database=Exact;Integrated Security=true";
        await Assert.ThrowsAsync<ArgumentException>(() => maintenance.RunAsync(local, "Different", true, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => maintenance.RunAsync(local, "", true, 1));
        foreach (var batch in new[] { 0, 1001 }) await Assert.ThrowsAsync<ArgumentException>(() => maintenance.RunAsync(local, "Exact", true, batch));
        foreach (var batch in new[] { 0, 6 }) await Assert.ThrowsAsync<ArgumentException>(() => maintenance.TestDatabasesAsync(local, true, batch));
        await Assert.ThrowsAsync<ArgumentException>(() => maintenance.TestDatabasesAsync(local, true, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => maintenance.TestDatabasesAsync(local, true, 1, "ListHero_Integration_]; DROP DATABASE anything"));
        foreach (var connection in new[] { "Server=production.database.windows.net;Integrated Security=true", @"Server=(localdb)\MSSQLLocalDB;User ID=user;Password=secret" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => maintenance.TestDatabasesAsync(connection, false, 1));
            var output = KeyRingBackupTests.NewPath();
            await Assert.ThrowsAsync<ArgumentException>(() => LocalRestoreDrill.RunAsync(connection, output));
            Assert.False(Directory.Exists(output));
        }
        Assert.True(RetentionMaintenance.IsLocalDb(@"(localdb)\ListHeroCI"));
        Assert.True(RetentionMaintenance.IsLocalDb(@"np:\\.\pipe\LOCALDB#synthetic\tsql\query"));
        Assert.False(RetentionMaintenance.IsLocalDb(@"np:\\.\pipe\LOCALDB#synthetic\other"));
        Assert.False(RetentionMaintenance.IsTestDatabase("ListHero_Integration_" + new string('a', 32) + "\n"));
    }

    private sealed class FakeOperations(Exception? failure = null) : IMaintenanceOperations
    {
        public MaintenanceRequest? Request;
        public Task<object> ExecuteAsync(MaintenanceRequest request, Func<string, string?> environment, CancellationToken ct)
        {
            Request = request;
            return failure is null ? Task.FromResult<object>(new { Outcome = "complete" }) : Task.FromException<object>(failure);
        }
    }
}
