namespace DbLens.Core.Analysis;

/// <summary>
/// What running a finding's SQL would actually do. The three kinds are not interchangeable and
/// the difference is not cosmetic: one of them creates tables.
/// </summary>
public enum SqlProposalKind
{
    /// <summary>
    /// A concrete statement that is correct as written and CHANGES the database. Requires human
    /// review and a maintenance window; never run automatically.
    /// </summary>
    Ddl = 0,

    /// <summary>
    /// A single pure SELECT returning aggregates only. Safe for dblens to execute itself under
    /// the statement guard and a rolled-back transaction, and safe for a caller to re-run.
    /// </summary>
    ReadOnlyProbe = 1,
}

/// <summary>
/// SQL attached to a finding, carrying what it is rather than leaving a consumer to guess.
///
/// This used to be a bare string. Some rules put DDL in it and others runnable SELECTs — so anything that executed
/// the field would eventually create a table. The kind is now unreachable without constructing
/// this record, which is the same reasoning that made <see cref="Evidence"/> a required
/// parameter: a separate flag is a thing an author can forget, and forgetting this one is
/// destructive.
/// </summary>
public sealed record SqlProposal(
    SqlProposalKind Kind,
    string Sql,
    /// <summary>One line stating what running this would do. Surfaced verbatim to the caller.</summary>
    string Intent)
{
    /// <summary>
    /// True only for <see cref="SqlProposalKind.ReadOnlyProbe"/>. Computed, never settable:
    /// no caller can mark a DDL statement safe.
    /// </summary>
    public bool SafeToExecuteAutomatically => Kind == SqlProposalKind.ReadOnlyProbe;

    public static SqlProposal Ddl(string sql, string intent) =>
        new(SqlProposalKind.Ddl, sql, intent);

    public static SqlProposal Probe(string sql, string intent) =>
        new(SqlProposalKind.ReadOnlyProbe, sql, intent);
}
