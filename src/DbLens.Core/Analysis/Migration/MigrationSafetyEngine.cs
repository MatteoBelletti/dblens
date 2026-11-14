using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Migration;

/// <summary>
/// Pure in-memory migration safety analysis. No database access — combines statements classified
/// by <see cref="DdlClassifier"/> with table context supplied by the caller, so the same operation
/// is reported differently on a 10-row table and a 50-million-row table.
/// </summary>
public static class MigrationSafetyEngine
{
    public const string Disclaimer =
        "Statements are matched by shape, not parsed: a statement reported as Unknown was not " +
        "recognized, which is not the same as safe. " +
        "Lock behaviour also depends on engine version and configuration — PostgreSQL has removed " +
        "table rewrites for several operations over successive releases, and SQL Server's ONLINE " +
        "options depend on edition. " +
        "Row counts come from cached statistics and may be stale. " +
        "Always rehearse a migration against a copy of production before running it.";

    public static CheckMigrationSafetyResult Analyze(
        IReadOnlyList<MigrationStatement> statements,
        IReadOnlyDictionary<(string Schema, string Table), MigrationTableContext> tables,
        CheckMigrationSafetyRequest request,
        DatabaseDialect dialect)
    {
        var findings = new List<Finding>();
        var caveats = new List<string>();
        var resolved = new HashSet<(string, string)>();

        foreach (var statement in statements)
        {
            var context = ResolveContext(statement, tables, request.DefaultSchema);
            if (context is not null) resolved.Add((context.Schema, context.Name));

            findings.AddRange(EvaluateStatement(statement, context, request, dialect));
        }

        var unrecognized = statements.Count(s => s.Kind == MigrationOperationKind.Unknown);
        if (unrecognized > 0)
            caveats.Add(
                $"{unrecognized} statement(s) were not recognized and no lock behaviour was inferred " +
                "for them. Review those statements manually — unrecognized does not mean safe.");

        var unresolvedTables = statements
            .Where(s => s.Table is not null && ResolveContext(s, tables, request.DefaultSchema) is null)
            .Select(s => s.Table!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unresolvedTables.Count > 0)
            caveats.Add(
                "These tables were not found in the catalog, so findings about them carry no size " +
                $"context: {string.Join(", ", unresolvedTables)}. " +
                "This is expected for tables the migration itself creates.");

        if (tables.Values.Any(t => t.ApproximateRowCount is null))
            caveats.Add(
                "Some row counts are unavailable because statistics have not been collected. " +
                "Run ANALYZE (PostgreSQL) or UPDATE STATISTICS (SQL Server) for accurate sizing.");

        var split = FindingSplit.Partition(findings);

        if (split.TruncationCaveat(hint: null) is { } truncation)
            caveats.Add(truncation);

        // The summary is appended after truncation so it can never be dropped.
        split = split.WithMandatory(BuildSummaryFinding(statements, findings, unrecognized));

        if (split.SignalCaveat() is { } signalCaveat)
            caveats.Add(signalCaveat);

        return Build(split, statements, caveats, unrecognized, resolved.Count);
    }

    public static CheckMigrationSafetyResult Empty() =>
        Build(
            FindingSplit.Empty.WithMandatory(BuildSummaryFinding([], [], 0)),
            [], [], 0, 0);

    private static CheckMigrationSafetyResult Build(
        FindingSplit split,
        IReadOnlyList<MigrationStatement> statements,
        IReadOnlyList<string> caveats,
        int unrecognized,
        int tablesResolved) =>
        new(
            Statements: statements,
            Findings: split.Findings,
            Signals: split.Signals,
            Summary: BuildSummary(split, statements.Count, unrecognized, tablesResolved),
            Caveats: caveats,
            Truncated: split.Truncated,
            Disclaimer: Disclaimer);

    // ── Per-statement rules ────────────────────────────────────────────────────

    private static IEnumerable<Finding> EvaluateStatement(
        MigrationStatement statement,
        MigrationTableContext? context,
        CheckMigrationSafetyRequest request,
        DatabaseDialect dialect)
    {
        var isLarge = context?.ApproximateRowCount >= request.LargeTableRowThreshold;
        var target = Qualify(statement, context);

        switch (statement.Kind)
        {
            case MigrationOperationKind.AlterColumnType:
                yield return BuildRewriteFinding(statement, context, target, isLarge, dialect,
                    "Changing a column's type rewrites every row of the table and holds an exclusive " +
                    "lock for the whole operation.");
                break;

            case MigrationOperationKind.AddColumn when statement.HasVolatileDefault:
                yield return BuildRewriteFinding(statement, context, target, isLarge, dialect,
                    "Adding a column whose DEFAULT is a function call evaluates that function for " +
                    "every existing row, which rewrites the table.");
                break;

            case MigrationOperationKind.AddColumn when statement.IsNotNull && !statement.HasDefault:
                yield return BuildBlockingFinding(statement, context, target, isLarge, Severity.High,
                    "Adding a NOT NULL column without a DEFAULT fails outright if the table has any " +
                    "rows. Add the column as nullable, backfill in batches, then set NOT NULL.");
                break;

            case MigrationOperationKind.SetNotNull:
                yield return BuildBlockingFinding(statement, context, target, isLarge,
                    isLarge ? Severity.High : Severity.Medium,
                    dialect == DatabaseDialect.PostgreSql
                        ? "SET NOT NULL scans the whole table to verify no NULLs remain, holding an " +
                          "ACCESS EXCLUSIVE lock throughout. On PostgreSQL 12+ you can avoid the blocking " +
                          "scan by first adding a NOT VALID CHECK (col IS NOT NULL), validating it, then " +
                          "setting NOT NULL."
                        : "Changing a column to NOT NULL scans the table to verify no NULLs remain and " +
                          "holds a schema modification lock throughout.");
                break;

            case MigrationOperationKind.CreateIndex when !statement.IsConcurrent:
                yield return BuildIndexFinding(statement, context, target, isLarge, dialect);
                break;

            case MigrationOperationKind.AddForeignKey when !statement.IsNotValid:
            case MigrationOperationKind.AddCheckConstraint when !statement.IsNotValid:
                yield return BuildConstraintScanFinding(statement, context, target, isLarge, dialect);
                break;

            case MigrationOperationKind.AddPrimaryKey:
            case MigrationOperationKind.AddUniqueConstraint when !statement.IsConcurrent:
                yield return BuildBlockingFinding(statement, context, target, isLarge,
                    isLarge ? Severity.High : Severity.Low,
                    "Adding a primary key or unique constraint builds a unique index over the whole " +
                    "table while holding an exclusive lock.");
                break;

            case MigrationOperationKind.DropTable:
            case MigrationOperationKind.DropColumn:
            case MigrationOperationKind.TruncateTable:
            case MigrationOperationKind.DropSchema:
                yield return BuildDestructiveFinding(statement, context, target);
                break;

            case MigrationOperationKind.RenameColumn:
            case MigrationOperationKind.RenameTable:
                yield return BuildRenameFinding(statement, target);
                break;
        }
    }

    private static Finding BuildRewriteFinding(
        MigrationStatement statement, MigrationTableContext? context, string target,
        bool isLarge, DatabaseDialect dialect, string why)
    {
        return new Finding(
            RuleId:     MigrationRuleIds.TableRewrite,
            Severity:   isLarge ? Severity.Critical : Severity.Medium,
            Category:   FindingCategory.Structure,
            Confidence: 0.9,
            Evidence: Evidence.Derived,
            Table:      target,
            Columns:    statement.ObjectName is null ? null : [statement.ObjectName],
            Title:      $"Full table rewrite on '{target}'{SizeSuffix(context)}",
            Description:
                why + " " + SizeSentence(context) +
                (isLarge
                    ? " At this size the rewrite will hold the lock long enough to stall all traffic " +
                      "against the table. Consider the expand-and-contract pattern: add a new column, " +
                      "backfill it in batches, switch reads and writes over, then drop the old column."
                    : " At this size the rewrite should complete quickly, but it still takes an " +
                      "exclusive lock for its duration.") +
                (dialect == DatabaseDialect.PostgreSql
                    ? " Note that PostgreSQL avoids the rewrite for some type changes (for example " +
                      "widening varchar); verify against your server version."
                    : ""),
            SqlFix:     null,
            Metadata:   BuildMetadata(statement, context));
    }

    private static Finding BuildBlockingFinding(
        MigrationStatement statement, MigrationTableContext? context, string target,
        bool isLarge, Severity severity, string why)
    {
        return new Finding(
            RuleId:     MigrationRuleIds.BlockingLock,
            Severity:   severity,
            Category:   FindingCategory.Structure,
            Confidence: 0.85,
            Evidence: Evidence.Derived,
            Table:      target,
            Columns:    statement.ObjectName is null ? null : [statement.ObjectName],
            Title:      $"Blocking operation on '{target}'{SizeSuffix(context)}",
            Description: why + " " + SizeSentence(context),
            SqlFix:     null,
            Metadata:   BuildMetadata(statement, context));
    }

    private static Finding BuildIndexFinding(
        MigrationStatement statement, MigrationTableContext? context, string target,
        bool isLarge, DatabaseDialect dialect)
    {
        var fix = dialect == DatabaseDialect.PostgreSql
            ? $"CREATE INDEX CONCURRENTLY {statement.ObjectName ?? "idx_name"} ON {target} (...);"
            : $"CREATE INDEX [{statement.ObjectName ?? "idx_name"}] ON {target} (...) WITH (ONLINE = ON);";

        return new Finding(
            RuleId:     MigrationRuleIds.IndexNotConcurrent,
            Severity:   isLarge ? Severity.High : Severity.Medium,
            Category:   FindingCategory.Performance,
            Confidence: 0.9,
            Evidence: Evidence.Derived,
            Table:      target,
            Columns:    null,
            Title:      $"Index built with writes blocked on '{target}'{SizeSuffix(context)}",
            Description:
                (dialect == DatabaseDialect.PostgreSql
                    ? "CREATE INDEX without CONCURRENTLY blocks all writes to the table until the index " +
                      "is complete. CONCURRENTLY builds without blocking writes, at the cost of two " +
                      "table passes and the possibility of leaving an invalid index if it fails."
                    : "CREATE INDEX without ONLINE = ON holds a lock that blocks writes (and, for a " +
                      "clustered index, reads) until the build completes. ONLINE = ON requires a " +
                      "supporting edition.") +
                " " + SizeSentence(context) +
                (dialect == DatabaseDialect.PostgreSql
                    ? " CONCURRENTLY cannot run inside a transaction block, so it must be separated " +
                      "from the rest of a transactional migration."
                    : ""),
            SqlFix:     SqlProposal.Ddl(fix, "A safer form of the same migration step."),
            Metadata:   BuildMetadata(statement, context));
    }

    private static Finding BuildConstraintScanFinding(
        MigrationStatement statement, MigrationTableContext? context, string target,
        bool isLarge, DatabaseDialect dialect)
    {
        var isFk = statement.Kind == MigrationOperationKind.AddForeignKey;

        var fix = dialect == DatabaseDialect.PostgreSql
            ? $"-- Two steps: take the lock briefly, then validate without blocking writes.\n" +
              $"ALTER TABLE {target} ADD CONSTRAINT {statement.ObjectName ?? "constraint_name"} ... NOT VALID;\n" +
              $"ALTER TABLE {target} VALIDATE CONSTRAINT {statement.ObjectName ?? "constraint_name"};"
            : $"-- Add without checking existing rows, then verify separately.\n" +
              $"ALTER TABLE {target} WITH NOCHECK ADD CONSTRAINT {statement.ObjectName ?? "constraint_name"} ...;";

        return new Finding(
            RuleId:     MigrationRuleIds.UnvalidatedConstraint,
            Severity:   isLarge ? Severity.High : Severity.Low,
            Category:   FindingCategory.Structure,
            Confidence: 0.85,
            Evidence: Evidence.Derived,
            Table:      target,
            Columns:    null,
            Title:
                $"{(isFk ? "Foreign key" : "Check constraint")} validated against every row of '{target}'" +
                SizeSuffix(context),
            Description:
                $"Adding this {(isFk ? "foreign key" : "check constraint")} scans the entire table to " +
                "verify existing rows, holding a lock for the duration. " +
                (isFk ? "A foreign key also locks the referenced table. " : "") +
                SizeSentence(context) +
                (dialect == DatabaseDialect.PostgreSql
                    ? " Adding it NOT VALID takes only a brief lock; a subsequent VALIDATE CONSTRAINT " +
                      "performs the scan without blocking writes."
                    : " Adding it WITH NOCHECK skips the scan, but leaves the constraint untrusted, " +
                      "which the optimizer will not use for query simplification."),
            SqlFix:     SqlProposal.Ddl(fix, "A safer form of the same migration step."),
            Metadata:   BuildMetadata(statement, context));
    }

    private static Finding BuildDestructiveFinding(
        MigrationStatement statement, MigrationTableContext? context, string target)
    {
        var isTable = statement.Kind != MigrationOperationKind.DropColumn;
        var (verb, what) = statement.Kind switch
        {
            MigrationOperationKind.TruncateTable => ("empties", $"deletes every row of '{target}'"),
            MigrationOperationKind.DropSchema => ("drops", $"destroys '{target}' and everything in it"),
            _ => ("drops", $"destroys '{target}' and all rows in it"),
        };

        return new Finding(
            RuleId:     MigrationRuleIds.Destructive,
            Severity:   Severity.Critical,
            Category:   FindingCategory.Structure,
            Confidence: 1.0,
            Evidence: Evidence.Derived,
            Table:      target,
            Columns:    statement.ObjectName is null || isTable ? null : [statement.ObjectName],
            Title:      isTable
                ? $"Irreversible: {verb} {(statement.Kind == MigrationOperationKind.DropSchema ? "" : "table ")}'{target}'{SizeSuffix(context)}"
                : $"Irreversible: drops column '{statement.ObjectName}' from '{target}'",
            Description:
                (isTable
                    ? $"This statement {what}."
                    : $"This statement destroys the data in column '{statement.ObjectName}' of '{target}'.") +
                " The data cannot be recovered by rolling the migration back — a rollback script can " +
                "recreate the structure but not its contents. " +
                (statement.Kind == MigrationOperationKind.DropSchema ? "" : SizeSentence(context)) +
                (context?.IncomingForeignKeys > 0
                    ? $" {context.IncomingForeignKeys} foreign key(s) reference this table; the drop will " +
                      "fail or cascade depending on how they are declared."
                    : "") +
                " Confirm a backup exists and that no application still reads this object.",
            SqlFix:     null,
            Metadata:   BuildMetadata(statement, context));
    }

    private static Finding BuildRenameFinding(MigrationStatement statement, string target)
    {
        return new Finding(
            RuleId:     MigrationRuleIds.BlockingLock,
            Severity:   Severity.Medium,
            Category:   FindingCategory.Structure,
            Confidence: 0.8,
            Evidence: Evidence.Derived,
            Table:      target,
            Columns:    statement.ObjectName is null ? null : [statement.ObjectName],
            Title:      $"Rename on '{target}' breaks running application code",
            Description:
                "A rename is fast and takes only a brief lock, but it is not backward compatible: " +
                "any deployed code still referring to the old name fails immediately afterwards. " +
                "Unless the application is stopped, use expand-and-contract — add the new name, " +
                "make the application write to both, migrate readers, then remove the old name.",
            SqlFix:     null,
            Metadata:   new Dictionary<string, object>
            {
                ["kind"] = statement.Kind.ToString(),
                ["object"] = statement.ObjectName ?? "",
            });
    }

    private static Finding BuildSummaryFinding(
        IReadOnlyList<MigrationStatement> statements,
        IReadOnlyList<Finding> findings,
        int unrecognized)
    {
        var critical = findings.Count(f => f.Severity == Severity.Critical);
        var high = findings.Count(f => f.Severity == Severity.High);

        return new Finding(
            RuleId:     MigrationRuleIds.Summary,
            Severity:   Severity.Info,
            Category:   FindingCategory.Structure,
            Confidence: 1.0,
            Evidence: Evidence.Derived,
            Table:      statements.Count == 0 ? "none" : $"{statements.Count} statement(s)",
            Columns:    null,
            Title:
                $"Migration analysis complete: {statements.Count} statement(s), " +
                $"{critical} critical, {high} high",
            Description:
                $"Analyzed {statements.Count} statement(s), of which {unrecognized} could not be " +
                "classified. " +
                (critical > 0
                    ? "This migration contains at least one irreversible or table-rewriting operation — " +
                      "review those findings before running it."
                    : high > 0
                        ? "This migration contains operations that hold blocking locks; check the sizes " +
                          "of the affected tables."
                        : "No blocking or destructive operations were identified."),
            SqlFix:     null,
            Metadata:   new Dictionary<string, object>
            {
                ["statementCount"] = statements.Count,
                ["unrecognized"] = unrecognized,
                ["criticalCount"] = critical,
                ["highCount"] = high,
            });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static MigrationTableContext? ResolveContext(
        MigrationStatement statement,
        IReadOnlyDictionary<(string Schema, string Table), MigrationTableContext> tables,
        string? defaultSchema)
    {
        if (statement.Table is null) return null;

        var schema = statement.Schema ?? defaultSchema;
        if (schema is not null && tables.TryGetValue((schema, statement.Table), out var exact))
            return exact;

        // Fall back to a unique bare-name match when the statement had no schema prefix.
        var matches = tables.Values
            .Where(t => string.Equals(t.Name, statement.Table, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private static string Qualify(MigrationStatement statement, MigrationTableContext? context)
    {
        if (context is not null) return $"{context.Schema}.{context.Name}";
        if (statement.Schema is not null && statement.Table is not null)
            return $"{statement.Schema}.{statement.Table}";
        return statement.Table ?? statement.ObjectName ?? "(unknown)";
    }

    private static string SizeSuffix(MigrationTableContext? context) =>
        context?.ApproximateRowCount is { } rows ? $" (~{Num.N0(rows)} rows)" : "";

    private static string SizeSentence(MigrationTableContext? context)
    {
        if (context is null)
            return "The table was not found in the catalog, so no size context is available.";

        if (context.ApproximateRowCount is not { } rows)
            return "Row count statistics are unavailable for this table, so the duration cannot be estimated.";

        var mb = context.TotalSizeBytes / 1024.0 / 1024.0;
        return $"The table holds approximately {Num.N0(rows)} rows ({Num.F1(mb)} MB).";
    }

    private static Dictionary<string, object> BuildMetadata(
        MigrationStatement statement, MigrationTableContext? context) =>
        new()
        {
            ["kind"] = statement.Kind.ToString(),
            ["object"] = statement.ObjectName ?? "",
            ["approximateRowCount"] = context?.ApproximateRowCount ?? -1,
            ["totalSizeBytes"] = context?.TotalSizeBytes ?? -1,
        };

    private static MigrationSummary BuildSummary(
        FindingSplit split,
        int statementCount,
        int unrecognized,
        int tablesResolved) =>
        new(
            TotalFindings: split.Findings.Count,
            TotalSignals: split.Signals.Count,
            StatementsAnalyzed: statementCount,
            StatementsUnrecognized: unrecognized,
            TablesResolved: tablesResolved,
            BySeverity: split.BySeverity(),
            ByRule: split.ByRule(MigrationRuleIds.All));
}
