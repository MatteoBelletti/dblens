namespace DbLens.Core.Analysis;

public enum SqlProposalKind
{
    /// <summary>Changes the database. For human review, never run automatically.</summary>
    Ddl = 0,

    /// <summary>One pure SELECT returning aggregates only: safe to run and to re-run.</summary>
    ReadOnlyProbe = 1,
}

/// <summary>
/// SQL attached to a finding together with what running it would do. The kind is part of the
/// value, not a flag beside it, so SQL cannot be published without saying which it is.
/// </summary>
public sealed record SqlProposal(
    SqlProposalKind Kind,
    string Sql,
    /// <summary>One line stating what running this would do.</summary>
    string Intent)
{
    /// <summary>Computed from the kind: no caller can mark a DDL statement safe.</summary>
    public bool SafeToExecuteAutomatically => Kind == SqlProposalKind.ReadOnlyProbe;

    public static SqlProposal Ddl(string sql, string intent) =>
        new(SqlProposalKind.Ddl, sql, intent);

    public static SqlProposal Probe(string sql, string intent) =>
        new(SqlProposalKind.ReadOnlyProbe, sql, intent);
}
