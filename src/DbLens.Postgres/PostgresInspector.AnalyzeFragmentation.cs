using System.Data.Common;
using DbLens.Core.Analysis.Fragmentation;
using DbLens.Core.Inspection;
using Npgsql;
using NpgsqlTypes;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    private const string PgStatTupleAvailableSql =
        "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pgstattuple')";

    // Lists the btree indexes of the scope with their page counts and measures nothing: pgstatindex fully
    // scans the index it is given, so the base calls it once per index that clears the page-count threshold
    // and survives one that does not. relkind 'i' leaves out the index of a partitioned table, which has
    // no pages of its own.
    private const string FragmentationCandidatesSql = """
        SELECT
            n.nspname                                                         AS schema_name,
            ct.relname                                                        AS table_name,
            ci.relname                                                        AS index_name,
            ix.indisprimary                                                   AS is_primary,
            am.amname                                                         AS method,
            (pg_relation_size(ci.oid) / current_setting('block_size')::bigint) AS page_count,
            ci.oid::bigint                                                    AS handle,
            0::bigint                                                         AS handle2
        FROM pg_index ix
        JOIN pg_class ct     ON ct.oid = ix.indrelid
        JOIN pg_class ci     ON ci.oid = ix.indexrelid
        JOIN pg_namespace n  ON n.oid = ct.relnamespace
        JOIN pg_am am        ON am.oid = ci.relam
        WHERE am.amname = 'btree'
        AND ci.relkind = 'i'
        AND ci.relpersistence <> 't'
        AND (n.nspname, ct.relname) IN (
            SELECT * FROM unnest(@schemas::text[], @names::text[])
        )
        """;

    private const string FragmentationMeasureSql = """
        SELECT leaf_fragmentation AS fragmentation_percent, avg_leaf_density AS page_density_percent
        FROM pgstatindex(@index_oid::oid::regclass)
        """;

    protected override DbCommand FragmentationCandidatesCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables) =>
        ScopedCommand(conn, FragmentationCandidatesSql, tables.Select(t => (t.Schema, t.Name)));

    protected override DbCommand FragmentationMeasureCommand(DbConnection conn, FragmentationCandidate index, AnalyzeFragmentationRequest request)
    {
        var cmd = Command(conn, FragmentationMeasureSql);
        cmd.Parameters.Add(new NpgsqlParameter("index_oid", NpgsqlDbType.Bigint) { Value = index.Handle });
        return cmd;
    }

    protected override async Task<string?> FragmentationUnavailableAsync(DbConnection conn, CancellationToken ct)
    {
        await using var installed = Command(conn, PgStatTupleAvailableSql);
        if (await installed.ExecuteScalarAsync(ct) is true)
        {
            // Checked up front: with every index below the size threshold nothing would call the function, and
            // a login that may not run it would get an empty analysis that reads as an all-clear.
            await using var allowed = Command(conn, "SELECT has_function_privilege('pgstatindex(regclass)', 'EXECUTE')");
            return await allowed.ExecuteScalarAsync(ct) is true ? null : PermissionDeniedText("permission denied for function pgstatindex");
        }

        return "PostgreSQL does not expose index fragmentation natively; dblens reads it via the " +
              "'pgstattuple' extension, which is not installed on this database. " +
              "Enable it with: CREATE EXTENSION pgstattuple; " +
              "(requires superuser or a role with CREATE on the database). " +
              "No fragmentation findings were produced.";
    }

    protected override string? FragmentationPermissionReason(DbException ex) =>
        ex is PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege } denied ? PermissionDeniedText(denied.MessageText) : null;

    private static string PermissionDeniedText(string message) =>
        $"The current login may not run pgstatindex ({message}). " +
        "Grant it with: GRANT pg_stat_scan_tables TO your_login; (pg_monitor includes it). " +
        "No fragmentation findings were produced.";

    protected override string FragmentationSource(AnalyzeFragmentationRequest request) =>
        "Fragmentation read via pgstattuple's pgstatindex(), which performs a full scan of each " +
        "index it measures. Only btree indexes are supported — other access methods " +
        "(gin, gist, hash, brin) are not reported.";
}
