using System.Data.Common;
using System.Text.RegularExpressions;
using ListHero.Infrastructure.Maintenance;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ListHero.Tests.Persistence;

// Mark only a successfully executed CREATE DATABASE from the test host. Never adopt old databases.
public sealed class TestDatabaseOwnershipInterceptor : DbCommandInterceptor
{
    public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        var match = Regex.Match(command.CommandText, @"^\s*CREATE DATABASE \[([A-Za-z0-9_]+)\]", RegexOptions.IgnoreCase);
        if (!match.Success || !RetentionMaintenance.IsTestDatabase(match.Groups[1].Value) || command.Connection is not SqlConnection connection
            || !RetentionMaintenance.IsLocalDb(new SqlConnectionStringBuilder(connection.ConnectionString).DataSource)) return result;
        var settings = new SqlConnectionStringBuilder(connection.ConnectionString) { InitialCatalog = match.Groups[1].Value, Pooling = false };
        await using var db = new SqlConnection(settings.ConnectionString); await db.OpenAsync(cancellationToken);
        await using var mark = db.CreateCommand();
        mark.CommandText = "EXEC sys.sp_addextendedproperty @name=N'ListHero.TestDatabase', @value=N'ListHero.Tests.v1'";
        await mark.ExecuteNonQueryAsync(cancellationToken);
        return result;
    }
}
