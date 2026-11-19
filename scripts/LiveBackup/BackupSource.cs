using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace ListHero.LiveBackup;

public interface IBackupSource
{
    Task<byte[]> ReadStateAsync(string app, CancellationToken ct);
    Task<byte[]> ReadSettingsAsync(string app, CancellationToken ct);
    Task<byte[]> ReadConfigurationAsync(string app, CancellationToken ct);
    Task<byte[]> ReadKeysAsync(string app, string application, CancellationToken ct);
    Task ReadSiteAsync(string app, string target, CancellationToken ct);
    Task ExportDatabaseAsync(string target, CancellationToken ct);
}

public sealed class AzureBackupSource(HttpClient client, string token, string connection) : IBackupSource
{
    public const string Subscription = "656761a1-f733-44d2-85d9-44cb54a2f96d";
    public const string Group = "rg-listhero-beta";
    public const string Web = "listhero-yj3pk6oimchdi";
    public const string Api = "listhero-api-yj3pk6oimchdi";
    public const string Server = "sql-listhero-yj3pk6oimchdi.database.windows.net";
    public const string Database = "ListHeroBeta";
    private static string Resource(string app)
    {
        if (app is not (Web or Api)) throw new ArgumentException();
        return $"https://management.azure.com/subscriptions/{Subscription}/resourceGroups/{Group}/providers/Microsoft.Web/sites/{app}";
    }
    public Task<byte[]> ReadStateAsync(string app, CancellationToken ct) => ReadAsync(HttpMethod.Get, Resource(app) + "?api-version=2024-11-01", 1024 * 1024, ct);
    public Task<byte[]> ReadSettingsAsync(string app, CancellationToken ct) => ReadAsync(HttpMethod.Post, Resource(app) + "/config/appsettings/list?api-version=2024-11-01", 4 * 1024 * 1024, ct);
    public async Task<byte[]> ReadConfigurationAsync(string app, CancellationToken ct)
    {
        var web = await ReadAsync(HttpMethod.Get, Resource(app) + "/config/web?api-version=2024-11-01", 4 * 1024 * 1024, ct);
        var connections = await ReadAsync(HttpMethod.Post, Resource(app) + "/config/connectionstrings/list?api-version=2024-11-01", 4 * 1024 * 1024, ct);
        using var webJson = JsonDocument.Parse(web); using var connectionsJson = JsonDocument.Parse(connections);
        return JsonSerializer.SerializeToUtf8Bytes(new { Web = webJson.RootElement, ConnectionStrings = connectionsJson.RootElement });
    }
    public Task<byte[]> ReadKeysAsync(string app, string application, CancellationToken ct)
    {
        _ = Resource(app);
        if (application != (app == Web ? "ListHero.Web" : "ListHero.Api")) throw new ArgumentException();
        return ReadAsync(HttpMethod.Get, $"https://{app}.scm.azurewebsites.net/api/zip/data/{application}/keys/", 16 * 1024 * 1024, ct);
    }
    public async Task ReadSiteAsync(string app, string target, CancellationToken ct)
    {
        _ = Resource(app);
        using var response = await SendAsync(HttpMethod.Get, $"https://{app}.scm.azurewebsites.net/api/zip/site/wwwroot/", ct);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        await CopyBoundedAsync(input, output, 512L * 1024 * 1024, ct);
    }
    public Task ExportDatabaseAsync(string target, CancellationToken ct)
    {
        var settings = new SqlConnectionStringBuilder(connection);
        if (settings.DataSource != $"tcp:{Server},1433" || settings.InitialCatalog != Database ||
            settings.TrustServerCertificate || settings.Encrypt != SqlConnectionEncryptOption.Mandatory || settings.IntegratedSecurity || settings.AttachDBFilename.Length != 0)
            throw new ArgumentException();
        return DatabaseExport.ExportAsync(connection, Database, target, ct);
    }
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) { response.Dispose(); throw new HttpRequestException(); }
        return response;
    }
    private async Task<byte[]> ReadAsync(HttpMethod method, string url, long limit, CancellationToken ct)
    {
        using var response = await SendAsync(method, url, ct);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); await CopyBoundedAsync(input, output, limit, ct);
        return output.ToArray();
    }
    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            if ((total += read) > limit) throw new InvalidDataException();
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}

public static class DatabaseExport
{
    public static Task ExportAsync(string connection, string database, string target, CancellationToken ct = default) => Task.Run(() =>
    {
        if (new SqlConnectionStringBuilder(connection).InitialCatalog != database || File.Exists(target)) throw new ArgumentException();
        // Library API keeps the SQL credential out of process arguments. Do not attach log handlers.
        var services = new DacServices(connection);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite);
        services.ExportBacpac(output, database, new DacExportOptions
        {
            CommandTimeout = 120, LongRunningCommandTimeout = 1800,
            Storage = DacSchemaModelStorageType.Memory,
            TempDirectoryForTableData = Path.GetDirectoryName(Path.GetFullPath(target))
        }, null, ct);
    }, ct);
}
