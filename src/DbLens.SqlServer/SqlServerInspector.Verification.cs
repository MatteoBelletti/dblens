using System.Data.Common;
using DbLens.Core.Analysis.Verification;
using Microsoft.Data.SqlClient;

namespace DbLens.SqlServer;

public sealed partial class SqlServerInspector
{
    // READ UNCOMMITTED so a scan of a large table cannot block production writers: "dblens held
    // up our writes" is a far worse outcome than a count that included a few uncommitted rows,
    // and this is already a point-in-time measurement. LOCK_TIMEOUT bounds any wait that remains.
    protected override string VerificationSetupSql(VerificationBudget budget) =>
        "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT 5000";

    protected override VerificationFailureKind ClassifyVerificationError(DbException ex) =>
        ClassifySqlServerError((ex as SqlException)?.Number ?? 0);

    protected override IReadOnlyList<string> VerificationCaveats =>
    [
        "On SQL Server the verification queries ran under READ UNCOMMITTED with a lock " +
        "timeout, so they could not block writers but the counts may include uncommitted " +
        "rows. PostgreSQL needs no such trade-off and its counts are of committed data. " +
        "SQL Server also has no read-only transaction mode, so there the statement " +
        "allow-list — not the engine — is what prevents a write from being sent.",
    ];

    internal static VerificationFailureKind ClassifySqlServerError(int number) => number switch
    {
        229 or 230 or 297 => VerificationFailureKind.PermissionDenied,
        -2 or 1222 => VerificationFailureKind.Timeout,
        208 or 207 => VerificationFailureKind.ObjectMissing,
        402 => VerificationFailureKind.TypeNotComparable,
        _ => VerificationFailureKind.Unknown,
    };
}
