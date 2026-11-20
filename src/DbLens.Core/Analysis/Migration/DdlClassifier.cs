using System.Text.RegularExpressions;
using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Migration;

/// <summary>
/// Classifies DDL statements by shape. Deliberately not a SQL parser — it recognizes the
/// common migration forms and returns <see cref="MigrationOperationKind.Unknown"/> for
/// everything else, so an unrecognized statement is never reported as safe.
/// </summary>
public static class DdlClassifier
{
    private const RegexOptions Opts =
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline;

    // Identifier: bare, "quoted" or [bracketed], optionally schema-qualified.
    private const string Ident = @"(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][A-Za-z0-9_$]*)";
    private const string QualifiedName = $@"(?:(?<schema>{Ident})\s*\.\s*)?(?<name>{Ident})";

    private static readonly Regex AlterTableRx =
        new($@"^\s*ALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:ONLY\s+)?{QualifiedName}\s+(?<rest>.*)$", Opts);

    private static readonly Regex CreateIndexRx =
        new($@"^\s*CREATE\s+(?<unique>UNIQUE\s+)?(?<concurrent>CONCURRENTLY\s+)?INDEX\s+" +
            $@"(?<concurrent2>CONCURRENTLY\s+)?(?:IF\s+NOT\s+EXISTS\s+)?(?<index>{Ident})?\s*" +
            $@"ON\s+{QualifiedName}(?<rest>.*)$", Opts);

    private static readonly Regex DropIndexRx =
        new($@"^\s*DROP\s+INDEX\s+(?<concurrent>CONCURRENTLY\s+)?(?:IF\s+EXISTS\s+)?{QualifiedName}(?<rest>.*)$", Opts);

    private static readonly Regex CreateTableRx =
        new($@"^\s*CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?{QualifiedName}", Opts);

    private static readonly Regex DropTableRx =
        new($@"^\s*DROP\s+TABLE\s+(?:IF\s+EXISTS\s+)?{QualifiedName}", Opts);

    private static readonly Regex DropTableListRx =
        new(@"^(?<head>\s*DROP\s+TABLE\s+(?:IF\s+EXISTS\s+)?)(?<names>.*?)(?<tail>\s+(?:CASCADE|RESTRICT))?\s*$", Opts);

    private static readonly Regex TruncateRx =
        new($@"^\s*TRUNCATE\s+(?:TABLE\s+)?(?:ONLY\s+)?{QualifiedName}", Opts);

    private static readonly Regex DropSchemaRx =
        new($@"^\s*DROP\s+(?:SCHEMA|DATABASE)\s+(?:IF\s+EXISTS\s+)?(?<name>{Ident})", Opts);

    // Statements that change nothing by themselves: not reported, not counted as unrecognized.
    private static readonly Regex TransactionControlRx =
        new(@"^\s*(?:BEGIN|START\s+TRANSACTION|COMMIT|END)(?:\s+(?:TRAN|TRANSACTION|WORK))?\s*$", Opts);

    // EF Core's bookkeeping in `dotnet ef migrations script`: its history row, and on SQL Server the
    // variable it reads a column's default constraint name into. Not reported, not counted either.
    private static readonly Regex EfBookkeepingRx =
        new($@"^\s*(?:INSERT\s+INTO\s+(?:{Ident}\s*\.\s*)?{EfHistory}\s*\([^;()]*\)\s*VALUES\s*\((?:[^;()']|'[^']*')*\)" +
            $@"|DELETE\s+FROM\s+(?:{Ident}\s*\.\s*)?{EfHistory}\s+WHERE\s+[^;()\n]*" +
            @"|DECLARE\s+@\w+\s+(?:sysname|nvarchar\s*\(\s*max\s*\))" +
            @"|SELECT\s+@\w+\s*=\s*(?:QUOTENAME\s*\(\s*)?\[d\]\.\[name\]\s*\)?\s+FROM\s+\[sys\]\.\[default_constraints\]\s+\[d\]" +
            @"\s+INNER\s+JOIN\s+\[sys\]\.\[columns\]\s+\[c\]\s+ON\s+[^;\n]*\s+WHERE\s+[^;\n]*)\s*;?\s*$", Opts);

    private const string EfHistory = @"[\[""]?__EFMigrationsHistory[\]""]?";

    // ...which it then drops through EXEC: classified as the ALTER TABLE it runs, named by the variable,
    // and reported with the script's own text.
    private static readonly Regex EfDropDefaultRx =
        new($@"^\s*IF\s+@(?<var>\w+)\s+IS\s+NOT\s+NULL\s+EXEC\s*\(\s*N?'(?<alter>ALTER\s+TABLE\s+{QualifiedName}\s+DROP\s+CONSTRAINT\s+)" +
            @"\[?'\s*\+\s*@\k<var>\s*\+\s*'\]?;?'\s*\)\s*;?\s*$", Opts);

    // ── T-SQL scripts: GO ends a batch, and statements need no separator ──────

    private static readonly Regex BatchSeparatorRx =
        new(@"^[ \t]*GO(?:[ \t]+\d+)?[ \t]*\r?$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // A module must be alone in its batch, and its body is statements of its own.
    private static readonly Regex ModuleRx =
        new(@"^\s*(?:CREATE|ALTER)\s+(?:OR\s+ALTER\s+)?(?:PROC|PROCEDURE|FUNCTION|TRIGGER|VIEW)\b", Opts);

    private static readonly Regex StatementStartRx =
        new(@"^(?=[ \t]*(?:ALTER|CREATE|DROP|TRUNCATE|UPDATE|DELETE|INSERT|MERGE|EXEC|EXECUTE)\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // The verb an ALTER TABLE action starts with; SQL Server lets later actions omit it (ADD a INT, b INT).
    private static readonly Regex ActionVerbRx =
        new(@"^\s*(?<verb>ADD|DROP\s+COLUMN|DROP\s+CONSTRAINT|DROP|ALTER|RENAME|VALIDATE|SET|ENABLE|DISABLE|" +
            @"ATTACH|DETACH|OWNER|WITH|CHECK|NOCHECK|SWITCH|REBUILD)\b", Opts);

    // ALTER TABLE sub-clauses.
    private static readonly Regex AddColumnRx =
        new($@"^\s*ADD\s+(?:COLUMN\s+)?(?:IF\s+NOT\s+EXISTS\s+)?(?<column>{Ident})\s+(?<rest>.*)$", Opts);

    // The negative lookahead keeps DROP CONSTRAINT / DROP DEFAULT / DROP NOT NULL from being
    // read as a column named "CONSTRAINT", "DEFAULT" or "NOT".
    private static readonly Regex DropColumnRx =
        new($@"^\s*DROP\s+(?:COLUMN\s+)?(?:IF\s+EXISTS\s+)?(?!CONSTRAINT\b|DEFAULT\b|NOT\s+NULL\b|INDEX\b)" +
            $@"(?<column>{Ident})", Opts);

    private static readonly Regex AlterColumnTypeRx =
        new($@"^\s*(?:ALTER|MODIFY)\s+(?:COLUMN\s+)?(?<column>{Ident})\s+(?:SET\s+DATA\s+)?TYPE\s+", Opts);

    private static readonly Regex AlterColumnSetNotNullRx =
        new($@"^\s*ALTER\s+(?:COLUMN\s+)?(?<column>{Ident})\s+SET\s+NOT\s+NULL", Opts);

    private static readonly Regex AlterColumnDropNotNullRx =
        new($@"^\s*ALTER\s+(?:COLUMN\s+)?(?<column>{Ident})\s+DROP\s+NOT\s+NULL", Opts);

    private static readonly Regex AlterColumnSetDefaultRx =
        new($@"^\s*ALTER\s+(?:COLUMN\s+)?(?<column>{Ident})\s+(?:SET|DROP)\s+DEFAULT", Opts);

    // SQL Server folds type changes into ALTER COLUMN <name> <type> with no TYPE keyword.
    private static readonly Regex SqlServerAlterColumnRx =
        new($@"^\s*ALTER\s+COLUMN\s+(?<column>{Ident})\s+(?<type>[A-Za-z]\w*(?:\s*\([^)]*\))?)\s*(?<rest>.*)$", Opts);

    private static readonly Regex AddConstraintRx =
        new($@"^\s*(?:WITH\s+(?<check>NOCHECK|CHECK)\s+)?ADD\s+(?:CONSTRAINT\s+(?<name>{Ident})\s+)?(?<rest>.*)$", Opts);

    // PostgreSQL's second half of NOT VALID: scans under SHARE UPDATE EXCLUSIVE, which blocks
    // neither reads nor writes. Recognized so the safe pattern is not reported as unknown.
    private static readonly Regex ValidateConstraintRx =
        new($@"^\s*VALIDATE\s+CONSTRAINT\s+(?<name>{Ident})", Opts);

    private static readonly Regex DropConstraintRx =
        new($@"^\s*DROP\s+CONSTRAINT\s+(?:IF\s+EXISTS\s+)?(?<name>{Ident})", Opts);

    private static readonly Regex RenameColumnRx =
        new($@"^\s*RENAME\s+(?:COLUMN\s+)?(?<column>{Ident})\s+TO\s+", Opts);

    private static readonly Regex RenameTableRx =
        new(@"^\s*RENAME\s+TO\s+", Opts);

    private static readonly Regex OnlineRx =
        new(@"ONLINE\s*=\s*ON", Opts);

    private static readonly Regex NotValidRx =
        new(@"NOT\s+VALID", Opts);

    private static readonly Regex DefaultRx =
        new(@"\bDEFAULT\b", Opts);

    private static readonly Regex NotNullRx =
        new(@"\bNOT\s+NULL\b", Opts);

    /// <summary>A default containing a call is evaluated per row and forces a table rewrite.</summary>
    private static readonly Regex VolatileDefaultRx =
        new(@"\bDEFAULT\s+(?!NULL\b)(?:[A-Za-z_][A-Za-z0-9_]*\s*\(|NEXT\s+VALUE\s+FOR\b)", Opts);

    public static IReadOnlyList<MigrationStatement> Classify(string ddl, DatabaseDialect dialect) =>
        [.. Statements(ddl, dialect)
            .Where(s => !TransactionControlRx.IsMatch(s) && !EfBookkeepingRx.IsMatch(s))
            .SelectMany(s => EfDropDefaultRx.Match(s) is { Success: true } drop
                ? [ClassifyOne($"{drop.Groups["alter"].Value}[@{drop.Groups["var"].Value}]") with { RawSql = s.Trim() }]
                : Expand(s, dialect).Select(ClassifyOne))];

    private static IEnumerable<string> Statements(string ddl, DatabaseDialect dialect)
    {
        if (dialect != DatabaseDialect.SqlServer) return SqlText.SplitStatements(ddl, dialect);

        return BatchSeparatorRx.Split(SqlText.StripComments(ddl, dialect))
            .Where(batch => batch.Trim().Length > 0)
            .SelectMany(batch => ModuleRx.IsMatch(batch)
                ? [batch.Trim()]
                : SqlText.SplitStatements(batch, dialect)
                    .SelectMany(s => StatementStartRx.Split(s))
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0));
    }

    /// <summary>
    /// One statement per object changed: an ALTER TABLE with several actions and a DROP TABLE with
    /// several names would otherwise be classified by their first part alone.
    /// </summary>
    private static IEnumerable<string> Expand(string statement, DatabaseDialect dialect)
    {
        if (DropTableListRx.Match(statement) is { Success: true } drop)
        {
            foreach (var name in SqlText.SplitTopLevel(drop.Groups["names"].Value, ',', dialect))
                yield return drop.Groups["head"].Value + name + drop.Groups["tail"].Value;
            yield break;
        }

        if (AlterTableRx.Match(statement) is not { Success: true } alter)
        {
            yield return statement;
            yield break;
        }

        var head = statement[..alter.Groups["rest"].Index];
        string? verb = null;

        foreach (var action in SqlText.SplitTopLevel(alter.Groups["rest"].Value, ',', dialect))
        {
            var own = ActionVerbRx.Match(action);
            if (own.Success) verb = own.Groups["verb"].Value;

            yield return head + (own.Success || verb is null ? action : $"{verb} {action}");
        }
    }

    public static MigrationStatement ClassifyOne(string statement)
    {
        var sql = statement.Trim();

        if (CreateIndexRx.Match(sql) is { Success: true } ci)
        {
            var concurrent = ci.Groups["concurrent"].Success
                             || ci.Groups["concurrent2"].Success
                             || OnlineRx.IsMatch(ci.Groups["rest"].Value);
            var (schema, table) = Qualified(ci);

            return Build(sql, MigrationOperationKind.CreateIndex, schema, table,
                Clean(ci.Groups["index"].Value), concurrent);
        }

        if (DropIndexRx.Match(sql) is { Success: true } di)
        {
            var concurrent = di.Groups["concurrent"].Success || OnlineRx.IsMatch(di.Groups["rest"].Value);
            var (schema, name) = Qualified(di);

            return Build(sql, MigrationOperationKind.DropIndex, schema, null, name, concurrent);
        }

        if (CreateTableRx.Match(sql) is { Success: true } ct)
        {
            var (schema, table) = Qualified(ct);
            return Build(sql, MigrationOperationKind.CreateTable, schema, table, table, false);
        }

        if (DropTableRx.Match(sql) is { Success: true } dt)
        {
            var (schema, table) = Qualified(dt);
            return Build(sql, MigrationOperationKind.DropTable, schema, table, table, false);
        }

        if (TruncateRx.Match(sql) is { Success: true } tr)
        {
            var (schema, table) = Qualified(tr);
            return Build(sql, MigrationOperationKind.TruncateTable, schema, table, table, false);
        }

        if (DropSchemaRx.Match(sql) is { Success: true } ds)
            return Build(sql, MigrationOperationKind.DropSchema, null, null, Clean(ds.Groups["name"].Value), false);

        if (AlterTableRx.Match(sql) is { Success: true } at)
        {
            var (schema, table) = Qualified(at);
            return ClassifyAlterTable(sql, schema, table, at.Groups["rest"].Value.Trim());
        }

        return Build(sql, MigrationOperationKind.Unknown, null, null, null, false);
    }

    private static MigrationStatement ClassifyAlterTable(
        string sql, string? schema, string? table, string rest)
    {
        if (AlterColumnTypeRx.Match(rest) is { Success: true } act)
            return Build(sql, MigrationOperationKind.AlterColumnType, schema, table,
                Clean(act.Groups["column"].Value), OnlineRx.IsMatch(rest));

        if (AlterColumnSetNotNullRx.Match(rest) is { Success: true } snn)
            return Build(sql, MigrationOperationKind.SetNotNull, schema, table,
                Clean(snn.Groups["column"].Value), false);

        if (AlterColumnDropNotNullRx.Match(rest) is { Success: true } dnn)
            return Build(sql, MigrationOperationKind.DropNotNull, schema, table,
                Clean(dnn.Groups["column"].Value), false);

        if (AlterColumnSetDefaultRx.Match(rest) is { Success: true } sd)
            return Build(sql, MigrationOperationKind.SetColumnDefault, schema, table,
                Clean(sd.Groups["column"].Value), false);

        if (RenameColumnRx.Match(rest) is { Success: true } rc)
            return Build(sql, MigrationOperationKind.RenameColumn, schema, table,
                Clean(rc.Groups["column"].Value), false);

        if (RenameTableRx.IsMatch(rest))
            return Build(sql, MigrationOperationKind.RenameTable, schema, table, table, false);

        if (DropConstraintRx.Match(rest) is { Success: true } dcon)
            return Build(sql, MigrationOperationKind.DropConstraint, schema, table,
                Clean(dcon.Groups["name"].Value), false);

        if (ValidateConstraintRx.Match(rest) is { Success: true } vc)
            return Build(sql, MigrationOperationKind.ValidateConstraint, schema, table,
                Clean(vc.Groups["name"].Value), false);

        if (DropColumnRx.Match(rest) is { Success: true } dc)
            return Build(sql, MigrationOperationKind.DropColumn, schema, table,
                Clean(dc.Groups["column"].Value), false);

        // SQL Server writes a type change as ALTER COLUMN <name> <type>, with no TYPE keyword.
        if (rest.StartsWith("ALTER COLUMN", StringComparison.OrdinalIgnoreCase)
            && SqlServerAlterColumnRx.Match(rest) is { Success: true } ssac)
        {
            var kind = NotNullRx.IsMatch(ssac.Groups["rest"].Value)
                ? MigrationOperationKind.SetNotNull
                : MigrationOperationKind.AlterColumnType;

            return Build(sql, kind, schema, table, Clean(ssac.Groups["column"].Value), false);
        }

        if (AddConstraintRx.Match(rest) is { Success: true } ac)
        {
            var body = ac.Groups["rest"].Value;
            var name = Clean(ac.Groups["name"].Value);
            var notValid = NotValidRx.IsMatch(rest)
                           || string.Equals(ac.Groups["check"].Value, "NOCHECK", StringComparison.OrdinalIgnoreCase);

            if (body.StartsWith("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
                return Build(sql, MigrationOperationKind.AddForeignKey, schema, table, name, false, notValid);

            if (body.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase))
                return Build(sql, MigrationOperationKind.AddCheckConstraint, schema, table, name, false, notValid);

            if (body.StartsWith("PRIMARY KEY", StringComparison.OrdinalIgnoreCase))
                return Build(sql, MigrationOperationKind.AddPrimaryKey, schema, table, name, OnlineRx.IsMatch(rest));

            if (body.StartsWith("UNIQUE", StringComparison.OrdinalIgnoreCase))
                return Build(sql, MigrationOperationKind.AddUniqueConstraint, schema, table, name, OnlineRx.IsMatch(rest));

            // Not a constraint: an ADD COLUMN, possibly without the optional COLUMN keyword.
            if (AddColumnRx.Match(rest) is { Success: true } addCol)
            {
                var colRest = addCol.Groups["rest"].Value;
                return new MigrationStatement(
                    RawSql: sql,
                    Kind: MigrationOperationKind.AddColumn,
                    Schema: schema,
                    Table: table,
                    ObjectName: Clean(addCol.Groups["column"].Value),
                    IsConcurrent: false,
                    IsNotValid: false,
                    HasDefault: DefaultRx.IsMatch(colRest),
                    IsNotNull: NotNullRx.IsMatch(colRest),
                    HasVolatileDefault: VolatileDefaultRx.IsMatch(colRest));
            }
        }

        return Build(sql, MigrationOperationKind.Unknown, schema, table, null, false);
    }

    private static MigrationStatement Build(
        string sql, MigrationOperationKind kind, string? schema, string? table,
        string? objectName, bool concurrent, bool notValid = false) =>
        new(sql, kind, schema, table, objectName, concurrent, notValid,
            HasDefault: false, IsNotNull: false, HasVolatileDefault: false);

    private static (string? Schema, string? Name) Qualified(Match match) =>
        (Clean(match.Groups["schema"].Value), Clean(match.Groups["name"].Value));

    private static string? Clean(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return null;
        return identifier.Trim().Trim('[', ']', '"');
    }
}
