using System.Text.Json;

namespace ListHero.LiveBackup;

public static class BackupCommand
{
    public static async Task<int> RunAsync(string[] args, Func<string, string?> environment, TextWriter output,
        IBackupSource? source = null, CancellationToken ct = default)
    {
        try
        {
            if (args.Length == 0 || args is ["--help"])
            {
                await output.WriteLineAsync("List Hero portable backup: capture-beta --output NEWDIR --writers-quiesced; unpack --source BUNDLE --output NEWDIR. Secrets use environment variables. See docs/live-backup.md."); return 0;
            }
            var command = args[0];
            if (command is not ("capture-beta" or "unpack")) throw new ArgumentException();
            var options = new Dictionary<string, string>();
            var allowed = command == "unpack" ? new[] { "--source", "--output" } : ["--output", "--writers-quiesced"];
            for (var i = 1; i < args.Length; i++)
            {
                var name = args[i];
                if (!allowed.Contains(name) || options.ContainsKey(name)) throw new ArgumentException();
                var value = name == "--writers-quiesced" ? "true" : ++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal) ? args[i] : throw new ArgumentException();
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(); options.Add(name, value);
            }
            if (allowed.Any(name => !options.ContainsKey(name))) throw new ArgumentException();
            string Required(string name) => environment(name) is { Length: > 0 } value ? value : throw new ArgumentException();
            var passphrase = Required("LISTHERO_BACKUP_PASSPHRASE"); SealedArchive.ValidatePassphrase(passphrase);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
            var result = command == "unpack" ? BackupCapture.Unpack(options["--source"], options["--output"], passphrase) :
                await BackupCapture.CaptureAsync(source ?? new AzureBackupSource(client, Required("LISTHERO_BACKUP_ARM_TOKEN"), Required("LISTHERO_MAINTENANCE_CONNECTION")), options["--output"], passphrase, true, ct);
            await output.WriteLineAsync(JsonSerializer.Serialize(result)); return 0;
        }
        catch (Exception exception)
        {
            var reason = exception switch
            {
                ArgumentException => "invalid-arguments", System.Security.Cryptography.CryptographicException => "backup-authentication-or-key-failure",
                InvalidDataException or JsonException => "invalid-backup-or-capture-state", OperationCanceledException => "canceled",
                IOException or UnauthorizedAccessException => "filesystem-operation-failed", HttpRequestException => "azure-read-failed", _ => "capture-operation-failed"
            };
            await output.WriteLineAsync(JsonSerializer.Serialize(new { Outcome = "failed", Reason = reason })); return 1;
        }
    }
}
