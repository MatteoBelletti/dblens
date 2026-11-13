namespace DbLens.Core.Analysis;

/// <summary>
/// What a finding is standing on. This is the difference between a fact and a hypothesis,
/// and it is deliberately a separate axis from <see cref="Severity"/> and Confidence:
/// a high-severity conjecture is still a conjecture.
///
/// Results split on this. <see cref="Inferred"/> findings are returned in a distinct
/// <c>Signals</c> list rather than mixed into <c>Findings</c>, so that a consumer summarizing
/// the response cannot flatten "the schema declares a circular cascade" and "a column is
/// named like an email address" into the same kind of statement.
/// </summary>
public enum Evidence
{
    /// <summary>
    /// Inferred from naming or structural heuristics. A hypothesis to check against the data,
    /// never a conclusion. A column named "fiscal_code" probably holds one — or holds nothing,
    /// or holds something else entirely.
    /// </summary>
    Inferred = 0,

    /// <summary>
    /// Derived deterministically from catalog metadata. True by construction: if the catalog
    /// says two foreign keys form a cycle of CASCADE deletes, they do.
    /// </summary>
    Derived = 1,

    /// <summary>
    /// Read from a physical measurement the engine performed — index fragmentation, scan
    /// counters, actual row counts from an executed plan. True at the moment it was taken,
    /// and subject to change afterwards.
    /// </summary>
    Measured = 2,
}

public static class EvidenceExtensions
{
    /// <summary>
    /// True when the finding asserts something about the database rather than guessing at it.
    /// Used to partition results into Findings and Signals.
    /// </summary>
    public static bool IsFactual(this Evidence evidence) => evidence != Evidence.Inferred;
}
