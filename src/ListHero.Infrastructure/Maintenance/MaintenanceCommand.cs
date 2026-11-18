using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace ListHero.Infrastructure.Maintenance;

public sealed record MaintenanceRequest(string Command, IReadOnlyDictionary<string, string> Options, bool Apply, int Batch);
public interface IMaintenanceOperations
{
    Task<object> ExecuteAsync(MaintenanceRequest request, Func<string, string?> environment, CancellationToken ct);
}

public static class MaintenanceCommand
{
    public static async Task<int> RunAsync(string[] args, Func<string, string?> environment, TextWriter output,
        IMaintenanceOperations? operations = null, CancellationToken ct = default)
    {
        try
        {
            if (args.Length == 0 || args is ["--help"])
            {
                await output.WriteLineAsync("List Hero maintenance: retention --database NAME [--apply] [--batch 1000]; test-databases [--database EXACT_NAME] [--apply] [--batch 5]; backup-keys --application ListHero.Api|ListHero.Web --source DIR --output NEWDIR; restore-keys --application APP --source BUNDLE --output NEWDIR; restore-drill --output NEWDIR. Connections, certificate material and passphrases use environment variables; see docs/retention-and-restore.md.");
                return 0;
            }
            var command = args[0];
            var allowed = command switch
            {
                "retention" => new[] { "--database", "--apply", "--batch" },
                "test-databases" => ["--apply", "--batch", "--database"],
                "backup-keys" or "restore-keys" => ["--application", "--source", "--output"],
                "restore-drill" => ["--output"],
                _ => throw new ArgumentException()
            };
            var options = new Dictionary<string, string>();
            for (var index = 1; index < args.Length; index++)
            {
                var key = args[index];
                if (!allowed.Contains(key) || options.ContainsKey(key)) throw new ArgumentException();
                var value = key == "--apply" ? "true" : ++index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal) ? args[index] : throw new ArgumentException();
                options.Add(key, value);
            }
            foreach (var required in command == "test-databases" ? [] : allowed.Except(["--apply", "--batch"]))
                if (!options.TryGetValue(required, out var value) || string.IsNullOrWhiteSpace(value)) throw new ArgumentException();
            var batch = command == "test-databases" ? 5 : 1000;
            if (options.TryGetValue("--batch", out var text) && !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out batch)) throw new ArgumentException();
            if (batch < 1 || batch > (command == "test-databases" ? 5 : 1000)) throw new ArgumentException();
            if (command == "test-databases" && options.ContainsKey("--apply") && !options.ContainsKey("--database")) throw new ArgumentException();
            var result = await (operations ?? new MaintenanceOperations()).ExecuteAsync(new(command, options, options.ContainsKey("--apply"), batch), environment, ct);
            await output.WriteLineAsync(JsonSerializer.Serialize(result, result.GetType(), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception)
        {
            var reason = exception switch
            {
                SqlException => "database-operation-failed", CryptographicException => "backup-authentication-or-key-failure",
                InvalidDataException or JsonException or System.Xml.XmlException => "invalid-backup", IOException or UnauthorizedAccessException => "filesystem-operation-failed",
                ArgumentException => "invalid-arguments", OperationCanceledException => "canceled", _ => "operation-failed"
            };
            await output.WriteLineAsync(JsonSerializer.Serialize(new { Outcome = "failed", Reason = reason }));
            return 1;
        }
    }
}

public sealed class MaintenanceOperations : IMaintenanceOperations
{
    public async Task<object> ExecuteAsync(MaintenanceRequest request, Func<string, string?> environment, CancellationToken ct)
    {
        string Required(string name) => environment(name) is { Length: > 0 } value ? value : throw new ArgumentException("Required environment setting is missing.");
        var options = request.Options;
        switch (request.Command)
        {
            case "retention": return await new RetentionMaintenance(TimeProvider.System).RunAsync(Required("LISTHERO_MAINTENANCE_CONNECTION"), options["--database"], request.Apply, request.Batch, ct);
            case "test-databases": return await new RetentionMaintenance(TimeProvider.System).TestDatabasesAsync(Required("LISTHERO_MAINTENANCE_CONNECTION"), request.Apply, request.Batch, options.GetValueOrDefault("--database"), ct);
            case "restore-drill": return await LocalRestoreDrill.RunAsync(Required("LISTHERO_MAINTENANCE_CONNECTION"), options["--output"], ct);
            case "backup-keys":
                using (var certificate = environment("LISTHERO_KEY_CERTIFICATE_BASE64") is { Length: > 0 } encoded
                    ? X509Certificate2.CreateFromEncryptedPem(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), Required("LISTHERO_KEY_CERTIFICATE_PASSWORD")) : null)
                    return KeyRingBackup.Export(options["--source"], options["--output"], options["--application"], Required("LISTHERO_BACKUP_PASSPHRASE"), certificate);
            case "restore-keys": return KeyRingBackup.Restore(options["--source"], options["--output"], options["--application"], Required("LISTHERO_BACKUP_PASSPHRASE"));
            default: throw new ArgumentException();
        }
    }
}
