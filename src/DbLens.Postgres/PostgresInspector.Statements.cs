using System.Data.Common;
using System.Globalization;
using DbLens.Core.Analysis;
using DbLens.Core.Models;

namespace DbLens.Postgres;

/// <summary>
/// pg_stat_statements where it was created: CREATE EXTENSION … SCHEMA extensions (Supabase) puts it
/// off the search_path, so its views are always read qualified.
/// </summary>
public sealed partial class PostgresInspector
{
    private const string StatementsSchemaSql =
        "SELECT n.nspname FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'pg_stat_statements'";

    /// <summary>The extension's schema, quoted; null when it was not created in this database.</summary>
    private static async Task<string?> StatementsSchemaAsync(DbConnection conn, CancellationToken ct)
    {
        await using var command = Command(conn, StatementsSchemaSql);
        return await command.ExecuteScalarAsync(ct) is string schema ? SqlIdentifier.Quote(schema, DatabaseDialect.PostgreSql) : null;
    }

    /// <summary><paramref name="sql"/> with {0} as the extension's quoted schema.</summary>
    private static DbCommand StatementsCommand(DbConnection conn, string sql, string schema) =>
        Command(conn, string.Format(CultureInfo.InvariantCulture, sql, schema));
}
