using System.Data.Common;
using DbLens.Core.Analysis.Verification;
using Npgsql;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    // READ ONLY makes the restriction engine-enforced rather than merely checked, and the
    // server-side statement_timeout guarantees a probe stops rather than being abandoned
    // client-side. MVCC means a read never blocks writers, so no isolation trade-off is needed.
    protected override string VerificationSetupSql(VerificationBudget budget) =>
        $"SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = {budget.QueryTimeoutSeconds * 1000}";

    protected override string ReadOnlyQuerySetupSql =>
        $"SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = {QueryTimeoutSeconds * 1000}";

    // has_table_privilege folds in ownership, role membership and superuser, so one query answers
    // "could this login change any user table" without reimplementing PostgreSQL's privilege rules.
    protected override async Task<string?> DescribeWriteCapabilityAsync(DbConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = QueryTimeoutSeconds;
        cmd.CommandText = """
            SELECT CASE
                WHEN (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN 'superuser ' || current_user
                WHEN EXISTS (
                    SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE c.relkind IN ('r', 'p')
                      AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_%'
                      AND has_table_privilege(c.oid, 'INSERT, UPDATE, DELETE, TRUNCATE'))
                    THEN current_user || ' can modify user tables'
            END
            """;
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    protected override VerificationFailureKind ClassifyVerificationError(DbException ex) =>
        ClassifyPostgresError((ex as PostgresException)?.SqlState);

    internal static VerificationFailureKind ClassifyPostgresError(string? sqlState) => sqlState switch
    {
        "42501" => VerificationFailureKind.PermissionDenied,
        "57014" => VerificationFailureKind.Timeout,
        "42P01" or "42703" => VerificationFailureKind.ObjectMissing,
        "42883" or "42804" => VerificationFailureKind.TypeNotComparable,
        _ => VerificationFailureKind.Unknown,
    };
}
