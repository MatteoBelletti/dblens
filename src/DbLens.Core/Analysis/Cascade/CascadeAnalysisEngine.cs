namespace DbLens.Core.Analysis.Cascade;

/// <summary>
/// Pure in-memory cascade path analysis. No database access — operates on an FK graph
/// supplied by the caller (dialect-specific inspector).
/// </summary>
public static class CascadeAnalysisEngine
{
    public const string Disclaimer =
        "This tool only analyzes FK constraints declared at the schema level. " +
        "Application-level deletions (ORM code, custom triggers) are not visible here.";

    private const int MaxDepthHardCap = 50;
    private const int DeepPathThreshold = 5;

    /// <summary>
    /// Raw FK edge as fetched from the database catalog.
    /// FromTable is the referencing (child) table; ToTable is the referenced (parent) table.
    /// </summary>
    public sealed record FkEdge(
        string FromSchema, string FromTable, string ConstraintName,
        IReadOnlyList<string> FromColumns,
        string ToSchema, string ToTable,
        IReadOnlyList<string> ToColumns,
        string OnDelete);

    public static CheckCascadePathsResult Analyze(
        IReadOnlyList<FkEdge> allFks,
        IReadOnlyDictionary<(string Schema, string Name), long?> rowCounts,
        IReadOnlyList<(string Schema, string Name)> rootTables,
        CheckCascadePathsRequest request,
        string dialect,
        string database)
    {
        var effectiveMaxDepth = Math.Min(
            Math.Max(request.MaxDepth, 1),
            MaxDepthHardCap);

        List<Finding> findings = [];
        List<CascadePath> paths = [];
        List<string> caveats = [];

        // Build lookup: (toSchema, toTable) → list of FkEdges pointing to that table
        var edgesByTarget = allFks
            .GroupBy(e => (e.ToSchema, e.ToTable))
            .ToDictionary(g => g.Key, g => g.ToList());

        // The path budget is global, not per root: a single pathological root must not be able
        // to exhaust memory before the remaining roots are even reached.
        var maxPaths = Math.Max(request.MaxPaths, 1);
        var pathsTruncated = false;

        foreach (var root in rootTables)
        {
            if (paths.Count >= maxPaths)
            {
                pathsTruncated = true;
                break;
            }

            var rootPaths = BuildPathsFromRoot(
                root, edgesByTarget, rowCounts, effectiveMaxDepth, maxPaths - paths.Count);

            paths.AddRange(rootPaths.Paths);
            if (rootPaths.Truncated) pathsTruncated = true;
        }

        // Produce findings from paths
        var allTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            allTables.Add(path.RootTable);
            foreach (var step in path.Steps)
                allTables.Add(step.ToTable);

            if (path.IsCircular)
                findings.Add(BuildCircularFinding(path));

            if (path.Depth > DeepPathThreshold && !path.IsCircular)
                findings.Add(BuildDeepPathFinding(path));

            foreach (var step in path.Steps)
            {
                if (step.OnDelete == "SET NULL")
                    findings.Add(BuildSetNullFinding(path.RootTable, step));
                else if (step.OnDelete is "RESTRICT" or "NO ACTION")
                    findings.Add(BuildRestrictFinding(path.RootTable, step));
            }

            // Only a self-reference that actually cascades is one. A NO ACTION hierarchy — the only
            // kind SQL Server allows — is already reported by the restrict rule.
            foreach (var step in path.Steps.Where(s => s.FromTable == s.ToTable && s.OnDelete == "CASCADE"))
                findings.Add(BuildSelfReferencingFinding(step));
        }

        // Deduplicate findings (same RuleId + Table can appear across multiple paths)
        findings = DeduplicateFindings(findings);

        var split = FindingSplit.Partition(findings);

        if (split.TruncationCaveat("Use the schema or startingTable filter for a more targeted analysis.") is { } truncation)
            caveats.Add(truncation);

        // Always-present cascade-summary finding, appended after truncation so it is never dropped.
        var maxDepthReached = paths.Count > 0 ? paths.Max(p => p.Depth) : 0;
        split = split.WithMandatory(
            BuildSummaryFinding(paths.Count, maxDepthReached, allTables.Count, rootTables));

        var summary = BuildSummary(split, paths, maxDepthReached, allTables.Count);

        if (rowCounts.Values.Any(v => v is null))
            caveats.Add(
                "Some row counts are null — statistics may not have been collected yet. " +
                "Run ANALYZE (PostgreSQL) or UPDATE STATISTICS (SQL Server) for accurate estimates.");
        else if (rowCounts.Count > 0)
            caveats.Add(
                "Row counts are approximated from cached database statistics and may not reflect the current state. " +
                "Run ANALYZE (PostgreSQL) or UPDATE STATISTICS (SQL Server) for more accurate estimates.");

        if (pathsTruncated)
            caveats.Add(
                $"Path enumeration stopped at the limit of {Num.N0(maxPaths)} paths, so the cascade " +
                "graph was only partially explored and findings below that point are missing. " +
                "This normally means the schema has a densely branching CASCADE structure. " +
                "Narrow the analysis with startingTable or schema, or raise maxPaths, " +
                "and do NOT read the result as a complete picture of the cascade graph.");

        return new CheckCascadePathsResult(
            Dialect: dialect,
            Database: database,
            GeneratedAt: DateTime.UtcNow.ToString("O"),
            Paths: paths,
            Findings: split.Findings,
            Signals: split.Signals,
            Summary: summary,
            Caveats: caveats,
            Truncated: split.Truncated,
            PathsTruncated: pathsTruncated,
            Disclaimer: Disclaimer);
    }

    public static CheckCascadePathsResult Empty(string dialect, string database)
    {
        var split = FindingSplit.Empty.WithMandatory(BuildSummaryFinding(0, 0, 0, []));

        return new CheckCascadePathsResult(
            Dialect: dialect,
            Database: database,
            GeneratedAt: DateTime.UtcNow.ToString("O"),
            Paths: [],
            Findings: split.Findings,
            Signals: split.Signals,
            Summary: BuildSummary(split, [], 0, 0),
            Caveats: [],
            Truncated: false,
            PathsTruncated: false,
            Disclaimer: Disclaimer);
    }

    // ── Path building ────────────────────────────────────────────────────────

    private static (List<CascadePath> Paths, bool Truncated) BuildPathsFromRoot(
        (string Schema, string Name) root,
        Dictionary<(string, string), List<FkEdge>> edgesByTarget,
        IReadOnlyDictionary<(string Schema, string Name), long?> rowCounts,
        int maxDepth,
        int maxPaths)
    {
        List<CascadePath> result = [];
        var truncated = false;
        var qualifiedRoot = $"{root.Schema}.{root.Name}";

        // Stack item: (steps built so far, current table, set of qualified names already in this path)
        var stack = new Stack<(List<CascadeStep> Steps, (string Schema, string Name) Current, HashSet<string> Visited)>();
        stack.Push(([], root, [qualifiedRoot]));

        while (stack.Count > 0)
        {
            // Stop as soon as the budget is met: the traversal is abandoned rather than
            // completed and then trimmed, so an exploding graph is never fully materialized.
            if (result.Count >= maxPaths)
            {
                truncated = true;
                break;
            }

            var (steps, current, visited) = stack.Pop();
            var qualifiedCurrent = $"{current.Schema}.{current.Name}";

            if (!edgesByTarget.TryGetValue(current, out var incomingEdges) || incomingEdges.Count == 0)
            {
                // Leaf node — no tables point to current with any FK
                if (steps.Count > 0)
                    result.Add(FinalizePath(qualifiedRoot, steps, brokenAt: null, isCircular: false));
                continue;
            }

            foreach (var edge in incomingEdges)
            {
                // NO ACTION is checked when the statement ends: by then a CASCADE key over the
                // same rows has deleted them, so the key blocks nothing and is not a step.
                if (edge.OnDelete == "NO ACTION" && incomingEdges.Any(cascade => DeletesEveryRowOf(cascade, edge)))
                    continue;

                var qualifiedFrom = $"{edge.FromSchema}.{edge.FromTable}";
                var rowCount = rowCounts.GetValueOrDefault((edge.FromSchema, edge.FromTable));

                var step = new CascadeStep(
                    FromTable: qualifiedCurrent,
                    ToTable: qualifiedFrom,
                    ViaConstraint: edge.ConstraintName,
                    OnDelete: edge.OnDelete,
                    EstimatedRowsAffected: rowCount);

                var newSteps = new List<CascadeStep>(steps) { step };

                // Self-referencing FK (table points to itself)
                if (edge.FromSchema == current.Schema && edge.FromTable == current.Name)
                {
                    result.Add(FinalizePath(qualifiedRoot, newSteps, brokenAt: null, isCircular: false));
                    continue;
                }

                // Cycle detection: is the from-table already in the current path?
                if (visited.Contains(qualifiedFrom))
                {
                    result.Add(FinalizePath(qualifiedRoot, newSteps, brokenAt: null, isCircular: true));
                    continue;
                }

                // Non-CASCADE action → broken chain
                if (edge.OnDelete != "CASCADE")
                {
                    result.Add(FinalizePath(qualifiedRoot, newSteps, brokenAt: qualifiedFrom, isCircular: false));
                    continue;
                }

                // Depth cap
                if (newSteps.Count >= maxDepth)
                {
                    result.Add(FinalizePath(qualifiedRoot, newSteps, brokenAt: null, isCircular: false));
                    continue;
                }

                // Continue BFS/DFS into the from-table
                var newVisited = new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase)
                    { qualifiedFrom };
                stack.Push((newSteps, (edge.FromSchema, edge.FromTable), newVisited));
            }
        }

        // Work left on the stack means the budget cut the traversal short.
        if (stack.Count > 0) truncated = true;

        return (result, truncated);
    }

    /// <summary>
    /// Whether every child row that references a parent through <paramref name="key"/> also
    /// references it through <paramref name="cascade"/>: same child table, and each column pair of
    /// the CASCADE key is part of the other key. A key on another column (approved_by beside
    /// created_by) keeps rows the cascade does not reach, and does block.
    /// </summary>
    private static bool DeletesEveryRowOf(FkEdge cascade, FkEdge key)
    {
        if (cascade.OnDelete != "CASCADE" || (cascade.FromSchema, cascade.FromTable) != (key.FromSchema, key.FromTable))
            return false;

        var pairs = key.FromColumns.Zip(key.ToColumns).ToHashSet();
        return cascade.FromColumns.Zip(cascade.ToColumns).All(pairs.Contains);
    }

    private static CascadePath FinalizePath(
        string rootTable,
        List<CascadeStep> steps,
        string? brokenAt,
        bool isCircular)
    {
        var leaves = steps.Count > 0
            ? [steps[^1].ToTable]
            : (IReadOnlyList<string>)[rootTable];

        return new CascadePath(
            RootTable: rootTable,
            Steps: steps,
            Depth: steps.Count,
            LeafTables: leaves,
            IsCircular: isCircular,
            BrokenAt: brokenAt);
    }

    // ── Finding builders ─────────────────────────────────────────────────────

    private static Finding BuildCircularFinding(CascadePath path)
    {
        var lastStep = path.Steps[^1];
        return new Finding(
            RuleId: CascadeRuleIds.CircularPath,
            Severity: Severity.Critical,
            Category: FindingCategory.Structure,
            Confidence: 1.0,
            Evidence: Evidence.Derived,
            Table: path.RootTable,
            Columns: null,
            Title: $"Circular CASCADE path detected from '{path.RootTable}'",
            Description:
                $"A circular ON DELETE CASCADE chain was detected: the path starting at '{path.RootTable}' " +
                $"eventually reaches '{lastStep.ToTable}', which is already in the chain. " +
                "This can cause runtime errors or unexpected mass deletions.",
            SqlFix: null,
            Metadata: new Dictionary<string, object>
            {
                ["path"] = string.Join(" → ", path.Steps.Select(s => s.ToTable).Prepend(path.RootTable))
            });
    }

    private static Finding BuildDeepPathFinding(CascadePath path)
    {
        var chain = string.Join(" → ", path.Steps.Select(s => s.ToTable).Prepend(path.RootTable));
        return new Finding(
            RuleId: CascadeRuleIds.DeepPath,
            Severity: Severity.High,
            Category: FindingCategory.Structure,
            Confidence: 1.0,
            Evidence: Evidence.Derived,
            Table: path.RootTable,
            Columns: null,
            Title: $"Deep CASCADE path from '{path.RootTable}' (depth {path.Depth})",
            Description:
                $"The CASCADE chain from '{path.RootTable}' reaches depth {path.Depth}, " +
                $"exceeding the warning threshold of {DeepPathThreshold}. " +
                "Deep cascade chains are hard to reason about and increase the risk of unintended mass deletions.",
            SqlFix: null,
            Metadata: new Dictionary<string, object>
            {
                ["depth"] = path.Depth,
                ["chain"] = chain
            });
    }

    private static Finding BuildSetNullFinding(string rootTable, CascadeStep step)
    {
        return new Finding(
            RuleId: CascadeRuleIds.BreaksWithSetNull,
            Severity: Severity.Info,
            Category: FindingCategory.Structure,
            Confidence: 1.0,
            Evidence: Evidence.Derived,
            Table: step.ToTable,
            Columns: null,
            Title: $"CASCADE chain from '{rootTable}' breaks with SET NULL at '{step.ToTable}'",
            Description:
                $"The FK '{step.ViaConstraint}' on '{step.ToTable}' referencing '{step.FromTable}' " +
                $"uses ON DELETE SET NULL. Rows in '{step.ToTable}' will not be deleted — " +
                "their FK column will be set to NULL instead. " +
                "This is relevant for GDPR right-to-erasure: data is retained (possibly anonymized).",
            SqlFix: null,
            Metadata: new Dictionary<string, object>
            {
                ["rootTable"] = rootTable,
                ["constraint"] = step.ViaConstraint
            });
    }

    private static Finding BuildRestrictFinding(string rootTable, CascadeStep step)
    {
        return new Finding(
            RuleId: CascadeRuleIds.BreaksWithRestrict,
            Severity: Severity.High,
            Category: FindingCategory.Structure,
            Confidence: 1.0,
            Evidence: Evidence.Derived,
            Table: step.ToTable,
            Columns: null,
            Title: $"CASCADE chain from '{rootTable}' blocked by NO ACTION/RESTRICT at '{step.ToTable}'",
            Description:
                $"The FK '{step.ViaConstraint}' on '{step.ToTable}' referencing '{step.FromTable}' " +
                $"uses ON DELETE {step.OnDelete}. A DELETE on '{step.FromTable}' will fail at runtime " +
                $"if any rows in '{step.ToTable}' still reference it through this key. " +
                "This is a latent constraint violation bug.",
            SqlFix: null,
            Metadata: new Dictionary<string, object>
            {
                ["rootTable"] = rootTable,
                ["constraint"] = step.ViaConstraint,
                ["action"] = step.OnDelete
            });
    }

    private static Finding BuildSelfReferencingFinding(CascadeStep step)
    {
        return new Finding(
            RuleId: CascadeRuleIds.SelfReferencing,
            Severity: Severity.Info,
            Category: FindingCategory.Structure,
            Confidence: 1.0,
            Evidence: Evidence.Derived,
            Table: step.FromTable,
            Columns: null,
            Title: $"Self-referencing CASCADE on '{step.FromTable}'",
            Description:
                $"The FK '{step.ViaConstraint}' on '{step.FromTable}' references the same table " +
                "with ON DELETE CASCADE. Deleting a row will cascade-delete all rows that reference it " +
                "in the same table (e.g. child nodes in a hierarchy). " +
                "This is usually intentional but worth documenting.",
            SqlFix: null,
            Metadata: new Dictionary<string, object>
            {
                ["constraint"] = step.ViaConstraint
            });
    }

    private static Finding BuildSummaryFinding(
        int totalPaths,
        int maxDepth,
        int totalTables,
        IReadOnlyList<(string Schema, string Name)> rootTables)
    {
        var rootList = rootTables.Count > 0
            ? string.Join(", ", rootTables.Select(r => $"{r.Schema}.{r.Name}"))
            : "none";

        return new Finding(
            RuleId: CascadeRuleIds.Summary,
            Severity: Severity.Info,
            Category: FindingCategory.Structure,
            Confidence: 1.0,
            Evidence: Evidence.Derived,
            Table: rootList.Length <= 200 ? rootList : $"{rootTables.Count} tables analyzed",
            Columns: null,
            Title: $"CASCADE analysis complete: {totalPaths} path(s), max depth {maxDepth}, {totalTables} table(s) involved",
            Description:
                $"Analyzed {rootTables.Count} root table(s). " +
                $"Found {totalPaths} distinct cascade path(s). " +
                $"Maximum cascade depth: {maxDepth}. " +
                $"Total tables involved in cascade chains: {totalTables}. " +
                "Row counts are approximated from cached DBMS statistics.",
            SqlFix: null,
            Metadata: new Dictionary<string, object>
            {
                ["totalPaths"] = totalPaths,
                ["maxDepth"] = maxDepth,
                ["totalTablesInvolved"] = totalTables
            });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<Finding> DeduplicateFindings(List<Finding> findings)
    {
        // Keep only the first occurrence of (RuleId, Table) pairs
        HashSet<(string, string)> seen = [];
        var result = new List<Finding>(findings.Count);
        foreach (var f in findings)
        {
            if (seen.Add((f.RuleId, f.Table)))
                result.Add(f);
        }
        return result;
    }

    private static CascadeSummary BuildSummary(
        FindingSplit split,
        IReadOnlyList<CascadePath> paths,
        int maxDepth,
        int totalTables) =>
        new(
            TotalFindings: split.Findings.Count,
            TotalSignals: split.Signals.Count,
            TotalPaths: paths.Count,
            MaxDepth: maxDepth,
            TotalTablesInvolved: totalTables,
            BySeverity: split.BySeverity(),
            ByRule: split.ByRule(CascadeRuleIds.All));
}
