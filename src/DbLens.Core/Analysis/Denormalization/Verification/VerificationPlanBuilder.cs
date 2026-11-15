using DbLens.Core.Analysis.Denormalization.Rules;
using DbLens.Core.Analysis.Verification;
using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Denormalization.Verification;

/// <summary>
/// Turns signals into the read-only queries that would test them. Pure: it decides what may run
/// and never opens a connection. A signal whose <see cref="SqlProposal"/> is a probe is tested by
/// that query; the rules that propose DDL build their probe here.
/// </summary>
public static class VerificationPlanBuilder
{
    public static IReadOnlyList<VerificationProbe> Build(
        IReadOnlyList<Finding> signals,
        IReadOnlyList<TableDetail> tables,
        IReadOnlyDictionary<(string Schema, string Table), long?> rowCounts,
        AnalyzeDenormalizationRequest request,
        DatabaseDialect dialect)
    {
        var byQualifiedName = tables.ToDictionary(t => t.Qualified(), StringComparer.Ordinal);
        List<VerificationProbe> probes = [];

        foreach (var signal in signals)
        {
            if (!byQualifiedName.TryGetValue(signal.Table, out var table)) continue;

            var probe = BuildProbe(signal, table, dialect);
            if (probe is null) continue;

            probes.Add(probe with
            {
                ApproximateRowCount = rowCounts.GetValueOrDefault((table.Schema, table.Name)),
            });
        }

        // Cheapest first, so a tight budget buys the most measurements. Unknown sizes sort last;
        // they are skipped anyway, but ordering keeps the plan stable and easy to reason about.
        return [.. probes
            .OrderBy(p => p.ApproximateRowCount ?? long.MaxValue)
            .ThenBy(p => p.Target.Key, StringComparer.Ordinal)];
    }


    private static VerificationProbe? BuildProbe(Finding signal, TableDetail table, DatabaseDialect dialect)
    {
        // Rules whose SqlProposal is already the probe.
        if (signal.SqlFix is { Kind: SqlProposalKind.ReadOnlyProbe } proposal)
        {
            var kind = signal.RuleId == DenormalizationRuleIds.DerivedColumn
                ? VerificationProbeKind.CachedCountDivergence
                : VerificationProbeKind.CopiedValueDivergence;

            return new VerificationProbe(
                Target(signal), kind, proposal.Sql, DescribeMeasurement(signal, kind), ApproximateRowCount: null);
        }

        // Rules whose SqlProposal is a DDL sketch: the probe is built by the owning rule.
        return signal.RuleId switch
        {
            DenormalizationRuleIds.MultiValueColumn => BuildDelimiterProbe(signal, table, dialect),
            DenormalizationRuleIds.RepeatingGroup => BuildOccupancyProbe(signal, table, dialect),
            _ => null,
        };
    }

    private static VerificationProbe? BuildDelimiterProbe(
        Finding signal, TableDetail table, DatabaseDialect dialect)
    {
        var columnName = signal.Columns?.SingleOrDefault();
        if (columnName is null) return null;

        var column = table.Columns.FirstOrDefault(c =>
            string.Equals(c.Name, columnName, StringComparison.Ordinal));
        if (column is null) return null;

        var sql = MultiValueColumnRule.BuildDelimiterProbe(table, column, dialect);
        if (sql is null) return null;

        return new VerificationProbe(
            Target(signal),
            VerificationProbeKind.DelimiterPresence,
            sql,
            $"non-null '{columnName}' values that contain a ',', ';' or '|'",
            ApproximateRowCount: null);
    }

    private static VerificationProbe? BuildOccupancyProbe(
        Finding signal, TableDetail table, DatabaseDialect dialect)
    {
        var members = signal.Columns;
        if (members is null || members.Count == 0) return null;

        var sql = RepeatingGroupRule.BuildOccupancyProbe(table, members, dialect);
        if (sql is null) return null;

        return new VerificationProbe(
            Target(signal),
            VerificationProbeKind.RepeatingGroupOccupancy,
            sql,
            $"rows that fill every one of {members.Count} slots ({string.Join(", ", members)})",
            ApproximateRowCount: null);
    }

    /// <summary>
    /// A noun phrase naming exactly what the numerator counts, including which tables were
    /// compared. It becomes the promoted finding's Title, and a Title marked Measured must not
    /// leave the reader to guess what was measured.
    /// </summary>
    private static string DescribeMeasurement(Finding signal, VerificationProbeKind kind)
    {
        var column = signal.Columns?.FirstOrDefault() ?? "?";
        string Meta(string key) =>
            signal.Metadata is not null && signal.Metadata.TryGetValue(key, out var v) ? v.ToString() ?? "" : "";

        return kind switch
        {
            VerificationProbeKind.CopiedValueDivergence =>
                $"rows where '{Meta("comparedChild")}.{column}' differs from '{Meta("comparedParent")}.{column}'",
            VerificationProbeKind.CachedCountDivergence =>
                $"rows whose stored '{column}' differs from the live count of '{Meta("childTable")}' rows",
            _ => signal.SqlFix?.Intent ?? "",
        };
    }

    private static VerificationTarget Target(Finding signal) =>
        new(signal.RuleId, signal.Table, signal.Columns ?? []);
}
