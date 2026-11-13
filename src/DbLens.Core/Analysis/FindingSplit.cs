namespace DbLens.Core.Analysis;

/// <summary>
/// Separates what a tool established from what it suspects. Every diagnostic result exposes the
/// two as distinct lists so a consumer cannot present a naming heuristic as a schema fact.
/// </summary>
public sealed record FindingSplit(
    /// <summary>Findings backed by measurement or by catalog metadata.</summary>
    IReadOnlyList<Finding> Findings,
    /// <summary>Findings inferred from naming or structural heuristics — hypotheses to verify.</summary>
    IReadOnlyList<Finding> Signals,
    bool Truncated)
{
    public static readonly FindingSplit Empty = new([], [], false);

    public int Total => Findings.Count + Signals.Count;

    /// <summary>Both lists, facts first. Use only for aggregate counting, never for display.</summary>
    public IEnumerable<Finding> All => Findings.Concat(Signals);

    /// <summary>
    /// Partitions by <see cref="Evidence"/> and applies the shared finding cap to the combined
    /// total. Facts are kept in preference to signals: when the budget is exhausted, an
    /// established problem is worth more than a guess.
    /// </summary>
    public static FindingSplit Partition(IEnumerable<Finding> all)
    {
        var findings = new List<Finding>();
        var signals = new List<Finding>();

        foreach (var finding in all)
            (finding.Evidence.IsFactual() ? findings : signals).Add(finding);

        if (findings.Count + signals.Count <= DiagnosticLimits.MaxFindingCount)
            return new FindingSplit(findings, signals, Truncated: false);

        if (findings.Count >= DiagnosticLimits.MaxFindingCount)
            return new FindingSplit(
                findings.Take(DiagnosticLimits.MaxFindingCount).ToList(), [], Truncated: true);

        var signalBudget = DiagnosticLimits.MaxFindingCount - findings.Count;
        return new FindingSplit(findings, signals.Take(signalBudget).ToList(), Truncated: true);
    }

    /// <summary>Appends a finding that must never be dropped, such as an always-present summary.</summary>
    public FindingSplit WithMandatory(Finding finding) =>
        finding.Evidence.IsFactual()
            ? this with { Findings = [..Findings, finding] }
            : this with { Signals = [..Signals, finding] };

    /// <summary>Count per severity across both lists. All five levels are always present.</summary>
    public IReadOnlyDictionary<string, int> BySeverity() =>
        Enum.GetValues<Severity>()
            .ToDictionary(s => s.ToString(), s => All.Count(f => f.Severity == s));

    /// <summary>Count per rule across both lists. Every rule in <paramref name="allRuleIds"/> is present.</summary>
    public IReadOnlyDictionary<string, int> ByRule(IReadOnlyList<string> allRuleIds) =>
        allRuleIds.ToDictionary(r => r, r => All.Count(f => f.RuleId == r));

    public const string FilterHint = "Use the schema/tables filters for a more targeted analysis.";

    /// <summary>The caveat to emit when the shared cap cut the results. Null when it did not.</summary>
    public string? TruncationCaveat(string? hint = FilterHint) =>
        !Truncated
            ? null
            : $"Results truncated to {DiagnosticLimits.MaxFindingCount} entries." + (hint is null ? "" : " " + hint);

    /// <summary>
    /// The caveat explaining the split, so a caller that only reads Findings still learns that
    /// Signals exist and why they are held apart.
    /// </summary>
    public string? SignalCaveat() =>
        Signals.Count == 0
            ? null
            : $"{Signals.Count} result(s) are reported under Signals rather than Findings. " +
              "Signals are inferred from column names and table structure, not measured or read " +
              "from catalog guarantees — treat them as hypotheses to confirm against the data, " +
              "and do not report them as established problems.";
}
