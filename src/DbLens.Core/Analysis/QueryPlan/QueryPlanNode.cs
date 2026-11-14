namespace DbLens.Core.Analysis.QueryPlan;

/// <summary>
/// Dialect-neutral classification of a plan operator. The inspector maps engine-specific
/// node names onto this so the analysis engine stays dialect-agnostic.
/// </summary>
public enum QueryPlanNodeKind
{
    Other,
    /// <summary>Full scan of a table's rows: PostgreSQL "Seq Scan", SQL Server "Table Scan"/"Clustered Index Scan".</summary>
    TableScan,
    /// <summary>Ranged or full read of an index: "Index Scan", "Index Only Scan", "Index Scan" (SQL Server).</summary>
    IndexScan,
    /// <summary>Point or range lookup through an index: "Index Seek", PostgreSQL bitmap/index lookups.</summary>
    IndexSeek,
    NestedLoop,
    HashJoin,
    MergeJoin,
    Sort,
    Aggregate,
}

/// <summary>
/// One node of a normalized execution plan. Estimated values are always present; actual values
/// are populated only when the query was executed (EXPLAIN ANALYZE / actual plan).
/// </summary>
public sealed record QueryPlanNode(
    /// <summary>Engine-specific operator name, preserved verbatim for display.</summary>
    string NodeType,
    QueryPlanNodeKind Kind,
    /// <summary>Table or view the node reads, when it reads one.</summary>
    string? Relation,
    string? IndexName,
    double EstimatedRows,
    double EstimatedCost,
    /// <summary>Rows actually produced, per execution. Null when the query was not executed.</summary>
    double? ActualRows,
    /// <summary>Elapsed time for this node. Null when the query was not executed.</summary>
    double? ActualTimeMs,
    /// <summary>Number of times the node was executed (inner side of a nested loop runs many times).</summary>
    long? Loops,
    /// <summary>True when the operator spilled to disk instead of completing in memory.</summary>
    bool SpilledToDisk,
    /// <summary>Filter or predicate applied by this node, when the engine reports one.</summary>
    string? Filter,
    IReadOnlyList<QueryPlanNode> Children,
    /// <summary>Rows the node read in total, when the engine reports it: a filter may discard most of them.</summary>
    double? RowsRead = null
)
{
    /// <summary>Depth-first walk over this node and all of its descendants.</summary>
    public IEnumerable<QueryPlanNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.DescendantsAndSelf())
                yield return node;
    }

    /// <summary>Total rows produced across every execution of this node.</summary>
    public double? TotalActualRows => ActualRows is null ? null : ActualRows * (Loops ?? 1);
}
