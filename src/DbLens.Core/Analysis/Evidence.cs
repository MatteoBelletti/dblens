namespace DbLens.Core.Analysis;

/// <summary>
/// What a finding stands on: the difference between a fact and a hypothesis. A separate axis from
/// <see cref="Severity"/> and confidence — a high-severity conjecture is still a conjecture — and
/// the one results are split on, so a summary cannot flatten the two into the same statement.
/// </summary>
public enum Evidence
{
    /// <summary>A naming or structural heuristic: a hypothesis to check against the data.</summary>
    Inferred = 0,

    /// <summary>Derived from catalog metadata: true by construction.</summary>
    Derived = 1,

    /// <summary>Measured by the engine (scan counters, fragmentation, actual rows): true when it was taken.</summary>
    Measured = 2,
}

public static class EvidenceExtensions
{
    /// <summary>Facts go in Findings, the rest in Signals.</summary>
    public static bool IsFactual(this Evidence evidence) => evidence != Evidence.Inferred;
}
