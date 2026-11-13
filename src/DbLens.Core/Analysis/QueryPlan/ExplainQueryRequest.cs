namespace DbLens.Core.Analysis.QueryPlan;

public sealed record ExplainQueryRequest(
    /// <summary>The statement to explain. Exactly one statement; only SELECT may be executed.</summary>
    string Sql,
    /// <summary>
    /// When true the query is actually executed to collect real row counts and timings.
    /// Guarded twice: <see cref="StatementGuard"/> rejects anything that is not a pure SELECT,
    /// and the inspector runs it inside a transaction that is always rolled back.
    /// </summary>
    bool Analyze = false,
    /// <summary>Estimated row count above which a full table scan is reported.</summary>
    long LargeScanRowThreshold = 10_000,
    /// <summary>
    /// Factor by which actual and estimated rows may differ before the planner's statistics
    /// are reported as stale. Only evaluated when Analyze is true.
    /// </summary>
    double EstimateMismatchFactor = 10.0,
    /// <summary>Row count above which the inner side of a nested loop is reported as expensive.</summary>
    long NestedLoopRowThreshold = 10_000
);
