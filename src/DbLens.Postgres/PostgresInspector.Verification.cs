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
