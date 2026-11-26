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

    // READ COMMITTED (the default) on purpose: a figure someone is asked to trust must not include
    // uncommitted rows. The lock timeout keeps a blocked read from hanging.
    protected override string ReadOnlyQuerySetupSql => "SET LOCK_TIMEOUT 5000";

    protected override string LocalWriteCaveatExtra =>
        " On SQL Server the rollback does not undo the locks the statement holds while it runs.";

    protected override IReadOnlyList<string> ReceiptCaveats =>
    [
        "SQL Server has no read-only transaction mode: the statement allow-list is what kept this to a " +
        "SELECT, and the transaction was rolled back.",
    ];

    // HAS_PERMS_BY_NAME and fn_my_permissions report effective permissions, so role membership,
    // object-level grants and sysadmin all count. EXECUTE counts too: a procedure can write.
    protected override async Task<string?> DescribeWriteCapabilityAsync(DbConnection conn, CancellationToken ct)
    {
        await using var cmd = Command(conn, """
            SELECT CASE
                WHEN IS_SRVROLEMEMBER('sysadmin') = 1 THEN SUSER_SNAME() + ' is sysadmin'
                WHEN EXISTS (
                    SELECT 1 FROM fn_my_permissions(NULL, 'DATABASE')
                    WHERE permission_name IN ('INSERT', 'UPDATE', 'DELETE', 'ALTER', 'CONTROL', 'EXECUTE',
                                              'CREATE TABLE', 'CREATE PROCEDURE'))
                    THEN USER_NAME() + ' has write or EXECUTE permissions in this database'
                WHEN EXISTS (
                    SELECT 1 FROM sys.objects o
                    WHERE o.is_ms_shipped = 0 AND o.type IN ('U', 'P', 'V')
                      AND (HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + '.' + QUOTENAME(o.name), 'OBJECT', 'INSERT') = 1
                        OR HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + '.' + QUOTENAME(o.name), 'OBJECT', 'UPDATE') = 1
                        OR HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + '.' + QUOTENAME(o.name), 'OBJECT', 'DELETE') = 1
                        OR HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + '.' + QUOTENAME(o.name), 'OBJECT', 'EXECUTE') = 1))
                    THEN USER_NAME() + ' can modify or execute objects in this database'
            END
            """);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

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
