using DbLens.Core.Analysis.EfCore;
using DbLens.Core.Analysis.Gdpr;
using DbLens.Core.Analysis.Verification;
using DbLens.Core.Models;
using static DbLens.Core.Analysis.Names;

namespace DbLens.Core.Analysis.Hygiene;

/// <summary>
/// Columns nobody fills and tables whose creation and update dates are missing or not kept. Pure.
/// Statistics and names give signals; a probe of two counts turns one into a fact. No value of a
/// column ever reaches a result, not even the one value of a single-valued column.
/// </summary>
public static class HygieneEngine
{
    public sealed record Input(
        IReadOnlyList<TableDetail> Tables,
        IReadOnlyDictionary<(string Schema, string Name), long?> RowCounts,
        IReadOnlyList<ColumnStatistics> Statistics,
        /// <summary>Updates per table, null when unknown; a table absent from it is not known either.</summary>
        IReadOnlyDictionary<(string Schema, string Name), long>? Updates,
        IReadOnlyList<EfModelMatch> Models,
        DatabaseDialect Dialect,
        /// <summary>The models' mappings dblens does not compare (EfModel.Unsupported).</summary>
        IReadOnlyList<string>? Unsupported = null);

    /// <summary>
    /// A measurable check. A signal stands on its own; a check that is only a question (is this
    /// column still filled?) is reported only when the probe answers it.
    /// </summary>
    public sealed record Check(Finding Finding, VerificationProbe Probe, bool IsSignal);

    public sealed record Plan(IReadOnlyList<Finding> Findings, IReadOnlyList<Check> Checks, IReadOnlyList<string> Caveats);

    public static Plan Analyze(Input input, AnalyzeTableHygieneRequest request)
    {
        var vocabulary = AuditVocabulary.Default;
        var statistics = input.Statistics.ToDictionary(s => (s.Schema, s.Table, s.Column));
        var mapped = MappedColumns(input.Models);
        var owners = TableOwners(input.Models);
        List<Finding> findings = [];
        List<Check> checks = [];
        List<string> caveats = [];
        var unseen = 0;

        foreach (var table in input.Tables)
        {
            var key = (table.Schema, table.Name);
            var rows = input.RowCounts.GetValueOrDefault(key);
            var created = AuditColumns(table, vocabulary.Created);
            var updated = AuditColumns(table, vocabulary.Updated);
            var excluded = ExcludedColumns(table);
            var audit = created.Concat(updated).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var column in table.Columns)
            {
                var ef = mapped.GetValueOrDefault((table.Schema, table.Name, column.Name));

                if (IsLegacyName(column, vocabulary))
                    findings.Add(LegacyName(table, column, ef));

                if (excluded.Contains(column.Name)) continue;
                var stats = statistics.GetValueOrDefault((table.Schema, table.Name, column.Name));
                if (stats is null) unseen++;

                if (stats is { AllNull: true })
                    checks.Add(AlwaysNull(table, column, rows, ef, input.Dialect));
                else if (stats is { SingleValue: true } && !audit.Contains(column.Name))
                    checks.Add(SingleValue(table, column, rows, ef, request, input.Dialect));

                if (request.VerifySignals && created.Count == 1 && column.IsNullable && !audit.Contains(column.Name)
                    && stats is not { AllNull: true } && rows >= request.MinRecentRows)
                    checks.Add(Abandoned(table, column, created[0], rows, ef, request, input.Dialect));
            }

            if (request.VerifySignals && created.Count > 1)
                caveats.Add($"{Name(table)} has {created.Count} creation columns ({string.Join(", ", created.Select(c => c.Name))}): " +
                            "which one dates a row is not known, so no column of it was checked for being abandoned.");

            AuditRules(table, rows, created, updated, input, request, owners.GetValueOrDefault((table.Schema, table.Name)), findings, checks);
        }

        if (unseen > 0)
            caveats.Add($"{Num.N0(unseen)} column(s) have no planner statistics, so whether they are always NULL or constant was " +
                        "not seen. On SQL Server statistics exist only on columns that were indexed or filtered on; on " +
                        "PostgreSQL, after ANALYZE.");

        findings.AddRange(NotInModel(input));
        return new Plan(findings, checks, caveats);
    }

    // ── Columns ────────────────────────────────────────────────────────────────

    private static Check AlwaysNull(TableDetail t, ColumnDetail c, long? rows, EfRef? ef, DatabaseDialect dialect) => new(
        Signal(HygieneRuleIds.ColumnAlwaysNull, Severity.Low, 0.8, t, c,
            $"{Name(t)}.{c.Name} looks NULL in every row",
            $"The planner's statistics sample no value in {c.Name}. A column nothing writes is dead weight, or a " +
            "feature that was never finished. Statistics are a sample: verifySignals counts the rows.",
            ef, DropColumnFix(ef, c)),
        Probe(HygieneRuleIds.ColumnAlwaysNull, VerificationProbeKind.NoValue, t, [c.Name], rows, dialect,
            $"SELECT {SqlCount.Of(dialect)} AS compared_rows, {SqlCount.Of(dialect, Q(c.Name, dialect))} AS divergent_rows FROM {Table(t, dialect)}",
            "rows that hold a value"),
        IsSignal: true);

    private static Check SingleValue(TableDetail t, ColumnDetail c, long? rows, EfRef? ef, AnalyzeTableHygieneRequest request, DatabaseDialect dialect) => new(
        Signal(HygieneRuleIds.ColumnSingleValue, Severity.Low, 0.7, t, c,
            $"{Name(t)}.{c.Name} looks like it holds one value in every filled row",
            $"The planner's statistics see a single distinct value in {c.Name}. A constant is not information: it belongs " +
            "in code or configuration, or the column is no longer written. The value itself is not reported.",
            ef, DropColumnFix(ef, c)),
        Probe(HygieneRuleIds.ColumnSingleValue, VerificationProbeKind.SingleValue, t, [c.Name], rows, dialect,
            $"SELECT {SqlCount.Of(dialect, Q(c.Name, dialect))} AS compared_rows, {SqlCount.Of(dialect, "DISTINCT " + Q(c.Name, dialect))} AS divergent_rows FROM {Table(t, dialect)}",
            "distinct values among the filled rows", request.MinSingleValueRows),
        IsSignal: true);

    private static Check Abandoned(
        TableDetail t, ColumnDetail c, ColumnDetail created, long? rows, EfRef? ef, AnalyzeTableHygieneRequest request, DatabaseDialect dialect)
    {
        var since = dialect == DatabaseDialect.SqlServer
            ? $"DATEADD(DAY, -{request.RecentDays}, SYSDATETIME())"
            : $"NOW() - INTERVAL '{request.RecentDays} days'";

        return new Check(
            Signal(HygieneRuleIds.ColumnAbandoned, Severity.Medium, 0.6, t, c,
                $"{Name(t)}.{c.Name} may no longer be filled",
                $"Checked against the rows created in the last {request.RecentDays} days ({created.Name}): a column older rows " +
                "hold and new rows do not was abandoned by the code that writes the table.",
                ef, DropColumnFix(ef, c)),
            Probe(HygieneRuleIds.ColumnAbandoned, VerificationProbeKind.NoValue, t, [c.Name], rows, dialect,
                $"SELECT {SqlCount.Of(dialect)} AS compared_rows, {SqlCount.Of(dialect, Q(c.Name, dialect))} AS divergent_rows " +
                $"FROM {Table(t, dialect)} WHERE {Q(created.Name, dialect)} >= {since}",
                $"rows created in the last {request.RecentDays} days that hold a value", request.MinRecentRows),
            IsSignal: false);
    }

    private static Finding LegacyName(TableDetail t, ColumnDetail c, EfRef? ef) =>
        Signal(HygieneRuleIds.ColumnLegacyName, Severity.Info, 0.4, t, c,
            $"{Name(t)}.{c.Name} is named or documented as left behind",
            $"Its name or comment marks it as old, temporary or deprecated. Check whether anything still reads it " +
            "before removing it.",
            ef, DropColumnFix(ef, c));

    /// <summary>
    /// Columns of a mapped table no property of any model maps. A table holding an entity whose mapping
    /// dblens does not compare (JSON, table splitting, TPT…) is skipped: its columns are not all known.
    /// </summary>
    private static IEnumerable<Finding> NotInModel(Input input)
    {
        var unsupported = input.Unsupported ?? [];
        var tables = input.Models.SelectMany(m => m.Tables).Where(t => t.Table is not null)
            .GroupBy(t => (t.Table!.Schema, t.Table.Name));

        foreach (var group in tables)
        {
            if (group.SelectMany(t => t.Entities).Any(e => NamedIn(unsupported, e.Name))) continue;

            var table = group.First().Table!;
            var owner = group.First().Owner.Name;
            var modeled = group.SelectMany(t => t.Columns).Select(c => c.Property.Column).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var column in table.Columns.Where(c => !modeled.Contains(c.Name)))
                yield return new Finding(
                    HygieneRuleIds.ColumnNotInModel, Severity.Medium, FindingCategory.Structure, 1.0, Evidence.Derived,
                    Name(table), [column.Name],
                    $"{Name(table)}.{column.Name} is not in the EF model",
                    $"The snapshot maps {Name(table)} to {Short(owner)} and no property to {column.Name}. In a " +
                    "code-first database removing a property drops its column, so this one was added or kept by hand: " +
                    "another application may write it, or it was left behind. Map it, or drop it with a migration.",
                    null,
                    Ef(new EfRef(owner, null),
                        $"Map it: a property on {Short(owner)} with .HasColumnName(\"{column.Name}\"). Or drop it: " +
                        $"migrationBuilder.DropColumn(name: \"{column.Name}\", table: \"{table.Name}\", schema: \"{table.Schema}\")."));
        }
    }

    private static bool NamedIn(IEnumerable<string> sentences, string entity) =>
        sentences.Any(s => s.Split(' ').Any(word => word.TrimEnd(',', '.') == entity));

    // ── Creation and update dates ──────────────────────────────────────────────

    private static void AuditRules(
        TableDetail t, long? rows, IReadOnlyList<ColumnDetail> created, IReadOnlyList<ColumnDetail> updated, Input input,
        AnalyzeTableHygieneRequest request, EfRef? owner, List<Finding> findings, List<Check> checks)
    {
        long? updates = input.Updates?.TryGetValue((t.Schema, t.Name), out var n) == true ? n : null;
        var big = rows >= request.MinTableRows;

        if (big && created.Count == 0)
            findings.Add(TableSignal(HygieneRuleIds.MissingCreated, Severity.Low, 0.6, t,
                $"{Name(t)} has no creation date",
                $"{Num.N0(rows!.Value)} rows and no column recording when each was created: no question about when data " +
                "arrived can be answered from the table itself.",
                owner, AuditFix));

        // Known to be insert-only: nothing to record.
        if (big && updated.Count == 0 && updates is not 0)
            findings.Add(TableSignal(HygieneRuleIds.MissingUpdated, updates > 0 ? Severity.Medium : Severity.Low, 0.6, t,
                $"{Name(t)} has no update date",
                updates > 0
                    ? $"Its rows were updated {Num.N0(updates.Value)} times since the counters started, and nothing records when."
                    : "Nothing records when a row last changed. Whether the table is ever updated is not known: the " +
                      "engine's counters do not cover enough time, cannot be read, or hold nothing for it.",
                owner, AuditFix));

        foreach (var c in created.Where(c => c.IsNullable))
            findings.Add(new Finding(HygieneRuleIds.CreatedNullable, Severity.Low, FindingCategory.Structure, 1.0, Evidence.Derived,
                Name(t), [c.Name], $"{Name(t)}.{c.Name} accepts NULL",
                "A creation date that may be missing is one no query can rely on.", null, Ef(owner, AuditFix)));

        foreach (var c in created.Concat(updated).Where(c => IsWithoutTimeZone(c, input.Dialect)))
            findings.Add(new Finding(HygieneRuleIds.WithoutTimeZone, Severity.Low, FindingCategory.Structure, 1.0, Evidence.Derived,
                Name(t), [c.Name], $"{Name(t)}.{c.Name} is a {c.FullDataType}, without a time zone",
                "The value means a different instant on every server or session time zone that reads it. EF Core writes " +
                "DateTime.UtcNow into it only if every writer agrees to.", null,
                Ef(owner, input.Dialect == DatabaseDialect.PostgreSql
                    ? "Remove HasColumnType(\"timestamp without time zone\") and Npgsql.EnableLegacyTimestampBehavior, then add a migration (timestamptz)."
                    : "Map it to datetimeoffset (DateTimeOffset) with HasColumnType, then add a migration.")));

        if (created.Count == 0 || updated.Count == 0) return;
        var (first, last) = (created[0], updated[0]);

        if (updates > 0)
            checks.Add(new Check(
                TableSignal(HygieneRuleIds.UpdatedNotMaintained, Severity.Medium, 0.6, t,
                    $"{Name(t)}.{last.Name} may not be kept up to date",
                    $"The table is updated ({Num.N0(updates.Value)} since the counters started) and has {first.Name} and " +
                    $"{last.Name}. If no row has {last.Name} different from {first.Name}, nothing writes it on update: no " +
                    "interceptor sets it, or the updates go through ExecuteUpdate, ExecuteDelete or plain SQL, which skip the " +
                    "SaveChanges interceptors.",
                    owner, AuditFix, [last.Name]),
                Probe(HygieneRuleIds.UpdatedNotMaintained, VerificationProbeKind.NoValue, t, [last.Name], rows, input.Dialect,
                    TimestampPairSql(t, first, last, "<>", input.Dialect), $"rows where {last.Name} differs from {first.Name}",
                    request.MinTableRows),
                IsSignal: true));

        if (request.VerifySignals)
            checks.Add(new Check(
                TableSignal(HygieneRuleIds.UpdateBeforeCreate, Severity.Low, 0.5, t,
                    $"{Name(t)}.{last.Name} may precede {first.Name}",
                    "A row changed before it was created means one of the two dates is written from another clock or time zone.",
                    owner, AuditFix, [last.Name]),
                Probe(HygieneRuleIds.UpdateBeforeCreate, VerificationProbeKind.RowsMatching, t, [last.Name], rows, input.Dialect,
                    TimestampPairSql(t, first, last, "<", input.Dialect), $"rows where {last.Name} is earlier than {first.Name}"),
                IsSignal: false));
    }

    private static string TimestampPairSql(TableDetail t, ColumnDetail created, ColumnDetail updated, string op, DatabaseDialect dialect) =>
        $"SELECT {SqlCount.Of(dialect)} AS compared_rows, " +
        $"{SqlCount.Of(dialect, $"CASE WHEN {Q(updated.Name, dialect)} {op} {Q(created.Name, dialect)} THEN 1 END")} AS divergent_rows " +
        $"FROM {Table(t, dialect)} WHERE {Q(created.Name, dialect)} IS NOT NULL AND {Q(updated.Name, dialect)} IS NOT NULL";

    private const string AuditFix =
        "public interface IAuditable { DateTime? CreatedAt { get; set; } DateTime? UpdatedAt { get; set; } } — implemented by " +
        "the entity; a SaveChangesInterceptor sets CreatedAt on Added and UpdatedAt on Added and Modified; every " +
        "ExecuteUpdate adds .SetProperty(e => e.UpdatedAt, DateTime.UtcNow). On a table that already has rows keep " +
        "CreatedAt nullable: a non-nullable DateTime makes EF add the column with a dummy default, and " +
        "HasDefaultValueSql(\"now()\") gives every existing row the migration's time — either way a false creation date.";

    // ── Verification ───────────────────────────────────────────────────────────

    /// <summary>The check as measured: a fact when confirmed, a refuted signal at Info, the rest as they were.</summary>
    public static Finding? Apply(Check check, VerificationMeasurement m)
    {
        var finding = check.Finding;

        if (m.Status is not (VerificationStatus.Confirmed or VerificationStatus.Refuted))
            return check.IsSignal ? finding with { Metadata = VerificationOutcome.Attach(finding.Metadata, m, null) } : null;

        var (compared, counted) = (m.Denominator!.Value, m.Numerator!.Value);
        var ratio = (double)counted / compared;
        var measured = $"MEASURED: {Num.N0(counted)} {m.MeasurementDescription}, of {Num.N0(compared)} rows compared. " +
                       "Read with a read-only aggregate query; no value was returned.";

        if (m.Status == VerificationStatus.Refuted)
            return check.IsSignal
                ? finding with
                {
                    Evidence = Evidence.Measured, Confidence = 1.0, Severity = Severity.Info,
                    Title = $"{finding.Table}: not borne out — {Num.N0(counted)} {m.MeasurementDescription} of {Num.N0(compared)}",
                    Description = measured + "\n\nWHAT WAS SUSPECTED: " + finding.Description,
                    Metadata = VerificationOutcome.Attach(finding.Metadata, m, ratio),
                }
                : null;

        var column = finding.Columns is [var c, ..] ? $"{finding.Table}.{c}" : finding.Table;
        return finding with
        {
            Evidence = Evidence.Measured,
            Confidence = 1.0,
            Title = finding.RuleId switch
            {
                HygieneRuleIds.ColumnAlwaysNull => $"{column} is NULL in all {Num.N0(compared)} rows",
                HygieneRuleIds.ColumnSingleValue => $"{column} holds one value in all {Num.N0(compared)} filled rows",
                HygieneRuleIds.ColumnAbandoned => $"{column} is empty in all {Num.N0(compared)} recent rows",
                HygieneRuleIds.UpdatedNotMaintained => $"{column} equals the creation date in all {Num.N0(compared)} rows",
                _ => $"{column}: {Num.N0(counted)} {m.MeasurementDescription} of {Num.N0(compared)} ({Num.P0(ratio)})",
            },
            Description = measured + "\n\nWHAT THIS MAY MEAN: " + finding.Description,
            Metadata = VerificationOutcome.Attach(finding.Metadata, m, ratio),
        };
    }

    public static AnalyzeTableHygieneResult Build(
        int tablesAnalyzed, IEnumerable<Finding> findings, IReadOnlyList<string> caveats, VerificationSummary? verification)
    {
        var split = FindingSplit.Partition(findings).WithMandatory(new Finding(
            HygieneRuleIds.Summary, Severity.Info, FindingCategory.Structure, 1.0, Evidence.Derived, "(scope)", null,
            $"{Num.N0(tablesAnalyzed)} tables checked for unused columns and audit dates",
            "Signals come from the planner's statistics and from names; facts from the catalog, the EF model or a count.",
            null, null));

        List<string> all = [.. caveats];
        if (split.TruncationCaveat() is { } truncation) all.Add(truncation);
        if (split.SignalCaveat() is { } signal) all.Add(signal);

        return new AnalyzeTableHygieneResult(
            split.Findings, split.Signals,
            new HygieneSummary(split.Findings.Count, split.Signals.Count, tablesAnalyzed, split.BySeverity(), split.ByRule(HygieneRuleIds.All)),
            all, split.Truncated, verification);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private sealed record EfRef(string Entity, string? Property);

    /// <summary>Every column the models map, with the entity and property that map it.</summary>
    private static IReadOnlyDictionary<(string, string, string), EfRef> MappedColumns(IReadOnlyList<EfModelMatch> models) =>
        models.SelectMany(m => m.Tables)
            .Where(t => t.Table is not null)
            .SelectMany(t => t.Columns.Where(c => c.Column is not null)
                .Select(c => (Key: (t.Table!.Schema, t.Table.Name, c.Column!.Name), Ref: new EfRef(c.Entity.Name, c.Property.Name))))
            .DistinctBy(x => x.Key)
            .ToDictionary(x => x.Key, x => x.Ref);

    /// <summary>The entity each mapped table belongs to.</summary>
    private static Dictionary<(string Schema, string Name), EfRef> TableOwners(IReadOnlyList<EfModelMatch> models) =>
        models.SelectMany(m => m.Tables)
            .Where(t => t.Table is not null)
            .DistinctBy(t => (t.Table!.Schema, t.Table.Name))
            .ToDictionary(t => (t.Table!.Schema, t.Table.Name), t => new EfRef(t.Owner.Name, null));

    private static IReadOnlyDictionary<string, object>? Ef(EfRef? ef, string code)
    {
        var fix = new Dictionary<string, object> { ["code"] = code };
        if (ef is not null)
        {
            fix["entity"] = ef.Entity;
            if (ef.Property is not null) fix["property"] = ef.Property;
        }

        return new Dictionary<string, object> { ["ef"] = fix };
    }

    private static string DropColumnFix(EfRef? ef, ColumnDetail c) =>
        (ef?.Property is { } p ? $"Remove {Short(ef.Entity)}.{p}" : $"Remove the property mapped to {c.Name}") +
        " in two releases, since running instances still select every mapped column: first remove the property and " +
        "delete the DropColumn from the generated migration; then, once no running version reads it, add a migration " +
        $"with migrationBuilder.DropColumn(\"{c.Name}\", …).";

    private static Finding Signal(
        string ruleId, Severity severity, double confidence, TableDetail t, ColumnDetail c, string title, string description,
        EfRef? ef, string fix) =>
        new(ruleId, severity, FindingCategory.Structure, confidence, Evidence.Inferred, Name(t), [c.Name], title, description, null, Ef(ef, fix));

    private static Finding TableSignal(
        string ruleId, Severity severity, double confidence, TableDetail t, string title, string description,
        EfRef? owner, string fix, IReadOnlyList<string>? columns = null) =>
        new(ruleId, severity, FindingCategory.Structure, confidence, Evidence.Inferred, Name(t), columns, title, description, null,
            Ef(owner, fix));

    private static VerificationProbe Probe(
        string ruleId, VerificationProbeKind kind, TableDetail t, IReadOnlyList<string> columns, long? rows, DatabaseDialect dialect,
        string sql, string measures, long minimumCompared = 1) =>
        new(new VerificationTarget(ruleId, Name(t), columns), kind, sql, measures, rows, minimumCompared);

    private static List<ColumnDetail> AuditColumns(TableDetail t, IReadOnlyList<string> names) =>
        [.. names.SelectMany(n => t.Columns.Where(c => PiiClassifier.Normalize(c.Name) == n && IsDateTime(c)))];

    private static bool IsDateTime(ColumnDetail c) =>
        c.DataType.Contains("timestamp", StringComparison.OrdinalIgnoreCase)
        || c.DataType.Contains("date", StringComparison.OrdinalIgnoreCase);

    private static bool IsWithoutTimeZone(ColumnDetail c, DatabaseDialect dialect) => dialect == DatabaseDialect.PostgreSql
        ? c.DataType.Equals("timestamp without time zone", StringComparison.OrdinalIgnoreCase)
        : c.DataType is "datetime" or "smalldatetime";

    private static bool IsLegacyName(ColumnDetail c, AuditVocabulary vocabulary) =>
        PiiClassifier.Normalize(c.Name).Split('_').Any(vocabulary.LegacyTokens.Contains)
        || (c.Comment is { } comment && (comment.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
                                         || comment.Contains("obsolet", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Keys, indexed, constrained, referencing, generated and identity columns: in use by construction.</summary>
    private static HashSet<string> ExcludedColumns(TableDetail t)
    {
        HashSet<string> excluded = new(StringComparer.Ordinal);
        excluded.UnionWith(t.PrimaryKey?.Columns ?? []);
        foreach (var index in t.Indexes) { excluded.UnionWith(index.Columns); excluded.UnionWith(index.IncludedColumns ?? []); }
        foreach (var fk in t.ForeignKeysOutgoing) excluded.UnionWith(fk.FromColumns);
        foreach (var unique in t.UniqueConstraints) excluded.UnionWith(unique.Columns);
        excluded.UnionWith(t.Columns.Where(c => c.IsGenerated || c.IsIdentity).Select(c => c.Name));
        return excluded;
    }

    private static string Q(string name, DatabaseDialect dialect) => SqlIdentifier.Quote(name, dialect);

    private static string Table(TableDetail t, DatabaseDialect dialect) => SqlIdentifier.Qualify(t.Schema, t.Name, dialect);

    private static string Name(TableDetail t) => $"{t.Schema}.{t.Name}";
}
