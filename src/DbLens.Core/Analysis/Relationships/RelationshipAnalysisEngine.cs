using DbLens.Core.Analysis.Verification;
using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Relationships;

/// <summary>
/// Builds the relationship map — declared keys plus inferred ones — and, when asked, applies the
/// measurements that test the inferred ones. Pure: no database access.
/// </summary>
public static class RelationshipAnalysisEngine
{
    public const string Disclaimer =
        "Declared relationships come from the catalog and are facts. Inferred relationships come from " +
        "column names and types only: a name that looks like a reference is a hypothesis, not a " +
        "relationship, until the data confirms it. Verified entries were checked with read-only " +
        "aggregate queries that read counts only — no row value was returned.";

    /// <summary>The unverified analysis, plus the candidates behind its signals for the verification pass.</summary>
    public sealed record Analysis(
        InferRelationshipsResult Result,
        IReadOnlyList<UndeclaredForeignKeyRule.Candidate> Candidates);

    public static Analysis Analyze(IReadOnlyList<TableDetail> tables)
    {
        var declared = tables
            .SelectMany(t => t.ForeignKeysOutgoing.Select(fk => new RelationshipEdge(
                $"{fk.FromSchema}.{fk.FromTable}", fk.FromColumns,
                $"{fk.ToSchema}.{fk.ToTable}", fk.ToColumns,
                RelationshipEvidence.Declared, fk.ConstraintName)))
            .ToList();

        var found = UndeclaredForeignKeyRule.Find(tables);
        var split = FindingSplit.Partition(found.Candidates.Select(UndeclaredForeignKeyRule.ToSignal));

        var inferred = found.Candidates.Select(c => new RelationshipEdge(
            c.Child.Qualified(), [c.Column.Name], c.Parent.Qualified(), [c.ParentKey],
            RelationshipEvidence.Inferred, c.Basis));

        var caveats = new List<string>();
        if (found.AmbiguousSkipped > 0)
            caveats.Add(
                $"{found.AmbiguousSkipped} column(s) matched more than one possible parent table by name and " +
                "were left out rather than guessed at.");
        if (split.SignalCaveat() is { } signalCaveat) caveats.Add(signalCaveat);
        if (split.TruncationCaveat() is { } truncation) caveats.Add(truncation);

        var result = Build(
            [..declared, ..inferred], split, caveats, tables.Count, found.AmbiguousSkipped, Disclaimer, verification: null);

        return new Analysis(result, found.Candidates);
    }

    public static InferRelationshipsResult Empty() =>
        Build([], FindingSplit.Empty, [], 0, 0, Disclaimer, verification: null);

    /// <summary>Probes for the candidates still represented by a signal (truncation may have dropped some).</summary>
    public static IReadOnlyList<VerificationProbe> BuildPlan(
        Analysis analysis,
        IReadOnlyDictionary<(string Schema, string Table), long?> rowCounts,
        DatabaseDialect dialect)
    {
        var signalled = analysis.Result.Signals.Select(TargetOf).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);

        return analysis.Candidates
            .Select(c => (Candidate: c, Sql: UndeclaredForeignKeyRule.BuildInclusionProbe(c, dialect)))
            .Where(x => x.Sql is not null && signalled.Contains(TargetOf(x.Candidate).Key))
            .Select(x => new VerificationProbe(
                TargetOf(x.Candidate),
                VerificationProbeKind.InclusionDependency,
                x.Sql!,
                $"non-null '{x.Candidate.Child.Qualified()}.{x.Candidate.Column.Name}' values with no match in " +
                $"'{x.Candidate.Parent.Qualified()}.{x.Candidate.ParentKey}'",
                rowCounts.GetValueOrDefault((x.Candidate.Child.Schema, x.Candidate.Child.Name))))
            .OrderBy(p => p.ApproximateRowCount ?? long.MaxValue)
            .ThenBy(p => p.Target.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Reclassifies measured candidates. Three outcomes, because "the name lied" and "the reference
    /// is real but dirty" are different facts that deserve different responses.
    /// </summary>
    public static InferRelationshipsResult ApplyVerification(
        Analysis analysis,
        IReadOnlyList<VerificationMeasurement> measurements,
        DatabaseDialect dialect)
    {
        var byKey = measurements.ToDictionary(m => m.Target.Key, StringComparer.Ordinal);
        var candidates = analysis.Candidates.ToDictionary(c => TargetOf(c).Key, StringComparer.Ordinal);
        var unverified = analysis.Result;

        var reclassified = new List<Finding>(unverified.Findings);
        var edges = unverified.Relationships.Where(e => e.Evidence == RelationshipEvidence.Declared).ToList();

        foreach (var signal in unverified.Signals)
        {
            var key = TargetOf(signal).Key;
            var candidate = candidates[key];

            if (!byKey.TryGetValue(key, out var m)
                || m.Status is not (VerificationStatus.Confirmed or VerificationStatus.Refuted))
            {
                reclassified.Add(m is null ? signal : signal with { Metadata = WithOutcome(signal, m, null) });
                edges.Add(InferredEdge(candidate, RelationshipEvidence.Inferred, null, null));
                continue;
            }

            var compared = m.Denominator!.Value;
            var orphans = m.Numerator!.Value;
            var orphanRatio = (double)orphans / compared;
            var target = $"'{candidate.Parent.Qualified()}.{candidate.ParentKey}'";
            var source = $"'{candidate.Child.Qualified()}.{candidate.Column.Name}'";

            if (orphans == 0)
            {
                reclassified.Add(signal with
                {
                    Evidence = Evidence.Measured,
                    Confidence = 1.0,
                    Title = $"{source}: all {Num.N0(compared)} non-null values exist in {target}",
                    Description =
                        $"MEASURED: every one of {Num.N0(compared)} non-null values of {source} matches a row of " +
                        $"{target}. Read with a read-only aggregate query; no row values were returned.\n\n" +
                        "WHAT THIS MAY MEAN: this behaves as a foreign key the schema does not declare, so the " +
                        "database is not enforcing it. Values matching is strong evidence of a reference, not " +
                        "proof of intent — confirm with whoever owns the schema before declaring the constraint.",
                    SqlFix = UndeclaredForeignKeyRule.BuildConstraint(candidate, dialect),
                    Metadata = WithOutcome(signal, m, 0),
                });
                edges.Add(InferredEdge(candidate, RelationshipEvidence.Verified, compared, 0));
            }
            else if (orphanRatio <= 0.5)
            {
                reclassified.Add(signal with
                {
                    Evidence = Evidence.Measured,
                    Confidence = 1.0,
                    Severity = Severity.Medium,
                    Title = $"{source}: {Num.N0(orphans)} of {Num.N0(compared)} values have no match in {target}",
                    Description =
                        $"MEASURED: {Num.N0(orphans)} of {Num.N0(compared)} non-null values of {source} " +
                        $"({Num.P0(orphanRatio)}) match no row of {target}; the rest do. Read with a read-only " +
                        "aggregate query; no row values were returned.\n\n" +
                        "WHAT THIS MAY MEAN: most values resolve, which suggests a real reference with orphaned " +
                        "rows — references to parents that were deleted or never existed. A foreign key cannot " +
                        "be added until they are resolved, and a join on this path silently drops them.",
                    Metadata = WithOutcome(signal, m, orphanRatio),
                });
                edges.Add(InferredEdge(candidate, RelationshipEvidence.VerifiedWithOrphans, compared, orphans));
            }
            else
            {
                // The data contradicts the name. Reported as a fact, and left out of the join map.
                reclassified.Add(signal with
                {
                    Evidence = Evidence.Measured,
                    Confidence = 1.0,
                    Severity = Severity.Info,
                    Title = $"{source}: {Num.N0(orphans)} of {Num.N0(compared)} values have no match in {target}",
                    Description =
                        $"MEASURED: {Num.P0(orphanRatio)} of the non-null values of {source} match no row of " +
                        $"{target}. Despite the name, the data does not support a reference, and this path has " +
                        "been left out of the relationship map.",
                    Metadata = WithOutcome(signal, m, orphanRatio),
                });
            }
        }

        var split = FindingSplit.Partition(reclassified);
        var caveats = unverified.Caveats
            .Where(c => !c.Contains("reported under Signals rather than Findings", StringComparison.Ordinal))
            .ToList();

        var inconclusive = measurements.Count(x => x.Status == VerificationStatus.Inconclusive);
        if (inconclusive > 0)
            caveats.Add(
                $"{inconclusive} candidate(s) had no non-null values to compare and remain unverified — an " +
                "empty column neither confirms nor refutes a reference.");
        if (split.SignalCaveat() is { } signalCaveat) caveats.Add(signalCaveat);

        return Build(edges, split, caveats, unverified.Summary.TablesAnalyzed,
            unverified.Summary.AmbiguousCandidatesSkipped, Disclaimer,
            VerificationSummary.From(measurements, attempted: true));
    }

    private static RelationshipEdge InferredEdge(
        UndeclaredForeignKeyRule.Candidate c, RelationshipEvidence evidence, long? compared, long? orphans) =>
        new(c.Child.Qualified(), [c.Column.Name], c.Parent.Qualified(), [c.ParentKey], evidence, c.Basis, compared, orphans);

    private static IReadOnlyDictionary<string, object> WithOutcome(Finding signal, VerificationMeasurement m, double? ratio)
    {
        var copy = signal.Metadata is null ? new Dictionary<string, object>() : new Dictionary<string, object>(signal.Metadata);
        copy["verification"] = new VerificationOutcome(
            m.Status.ToString(), m.MeasurementDescription, m.Denominator, m.Numerator,
            ratio is null ? null : Math.Round(ratio.Value, 4), m.Status is VerificationStatus.Confirmed or VerificationStatus.Refuted ? null : m.Status.ToString(), m.Sql);
        return copy;
    }

    private static VerificationTarget TargetOf(UndeclaredForeignKeyRule.Candidate c) =>
        new(RelationshipRuleIds.UndeclaredForeignKey, c.Child.Qualified(), [c.Column.Name]);

    private static VerificationTarget TargetOf(Finding f) => new(f.RuleId, f.Table, f.Columns ?? []);

    private static InferRelationshipsResult Build(
        IReadOnlyList<RelationshipEdge> edges,
        FindingSplit split,
        IReadOnlyList<string> caveats,
        int tablesAnalyzed,
        int ambiguous,
        string disclaimer,
        VerificationSummary? verification) =>
        new(
            Relationships: edges,
            Findings: split.Findings,
            Signals: split.Signals,
            Summary: new RelationshipSummary(
                TotalFindings: split.Findings.Count,
                TotalSignals: split.Signals.Count,
                TablesAnalyzed: tablesAnalyzed,
                DeclaredRelationships: edges.Count(e => e.Evidence == RelationshipEvidence.Declared),
                InferredRelationships: edges.Count(e => e.Evidence == RelationshipEvidence.Inferred),
                VerifiedRelationships: edges.Count(e => e.Evidence is RelationshipEvidence.Verified or RelationshipEvidence.VerifiedWithOrphans),
                AmbiguousCandidatesSkipped: ambiguous,
                BySeverity: split.BySeverity(),
                ByRule: split.ByRule(RelationshipRuleIds.All)),
            Caveats: caveats,
            Truncated: split.Truncated,
            Disclaimer: disclaimer,
            Verification: verification);
}
