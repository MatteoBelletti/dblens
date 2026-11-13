using System.Text.RegularExpressions;

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
    private const string QualifiedName = $@"(?<qualified>(?:{Ident}\s*\.\s*)?{Ident})";

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

    public static IReadOnlyList<MigrationStatement> Classify(string ddl)
    {
        return SqlText.SplitStatements(ddl)
            .Select(ClassifyOne)
            .ToList();
    }

    public static MigrationStatement ClassifyOne(string statement)
    {
        var sql = statement.Trim();

        if (CreateIndexRx.Match(sql) is { Success: true } ci)
        {
            var concurrent = ci.Groups["concurrent"].Success
                             || ci.Groups["concurrent2"].Success
                             || OnlineRx.IsMatch(ci.Groups["rest"].Value);
            var (schema, table) = SplitQualified(ci.Groups["qualified"].Value);

            return Build(sql, MigrationOperationKind.CreateIndex, schema, table,
                Clean(ci.Groups["index"].Value), concurrent);
        }

        if (DropIndexRx.Match(sql) is { Success: true } di)
        {
            var concurrent = di.Groups["concurrent"].Success || OnlineRx.IsMatch(di.Groups["rest"].Value);
            var (schema, name) = SplitQualified(di.Groups["qualified"].Value);

            return Build(sql, MigrationOperationKind.DropIndex, schema, null, name, concurrent);
        }

        if (CreateTableRx.Match(sql) is { Success: true } ct)
        {
            var (schema, table) = SplitQualified(ct.Groups["qualified"].Value);
            return Build(sql, MigrationOperationKind.CreateTable, schema, table, table, false);
        }

        if (DropTableRx.Match(sql) is { Success: true } dt)
        {
            var (schema, table) = SplitQualified(dt.Groups["qualified"].Value);
            return Build(sql, MigrationOperationKind.DropTable, schema, table, table, false);
        }

        if (AlterTableRx.Match(sql) is { Success: true } at)
        {
            var (schema, table) = SplitQualified(at.Groups["qualified"].Value);
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

    private static (string? Schema, string? Name) SplitQualified(string qualified)
    {
        if (string.IsNullOrWhiteSpace(qualified)) return (null, null);

        var parts = qualified.Split('.', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (Clean(parts[0]), Clean(parts[1]))
            : (null, Clean(parts[0]));
    }

    private static string? Clean(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return null;
        return identifier.Trim().Trim('[', ']', '"');
    }
}
