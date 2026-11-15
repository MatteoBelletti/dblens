using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Cascade;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Unit tests for CascadeAnalysisEngine — pure in-memory logic with no database access.
/// </summary>
public sealed class CascadePathsTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static CascadeAnalysisEngine.FkEdge Edge(
        string fromSchema, string fromTable,
        string toSchema, string toTable,
        string onDelete = "CASCADE",
        string constraint = "fk_test") =>
        new(fromSchema, fromTable, constraint,
            ["id"], toSchema, toTable, ["id"], onDelete);

    private static Dictionary<(string, string), long?> EmptyRowCounts() => [];

    private static CheckCascadePathsRequest DefaultRequest(string? startingTable = null, int maxDepth = 10) =>
        new(startingTable, maxDepth);

    private static CheckCascadePathsResult Analyze(
        IReadOnlyList<CascadeAnalysisEngine.FkEdge> fks,
        IReadOnlyList<(string Schema, string Name)> roots,
        CheckCascadePathsRequest? request = null,
        IReadOnlyDictionary<(string Schema, string Name), long?>? rowCounts = null)
    {
        return CascadeAnalysisEngine.Analyze(
            fks,
            rowCounts ?? EmptyRowCounts(),
            roots,
            request ?? DefaultRequest(),
            "PostgreSql",
            "test_db");
    }

    // ── NO ACTION beside CASCADE ───────────────────────────────────────────────

    private static CascadeAnalysisEngine.FkEdge Key(
        string constraint, string onDelete, string[] from, string[] to) =>
        new("public", "orders", constraint, from, "public", "users", to, onDelete);

    [Fact]
    public void NoAction_OverTheRowsACascadeDeletes_DoesNotBlock()
    {
        // Checked when the statement ends, after the cascade has removed every row that references the user.
        var result = Analyze(
        [
            Key("fk_user", "CASCADE", ["user_id"], ["id"]),
            Key("fk_tenant_user", "NO ACTION", ["tenant_id", "user_id"], ["tenant_id", "id"]),
        ], [("public", "users")]);

        result.Findings.Should().NotContain(f => f.RuleId == CascadeRuleIds.BreaksWithRestrict);
        result.Paths.Should().ContainSingle().Which.BrokenAt.Should().BeNull();
    }

    [Theory]
    [InlineData("NO ACTION", "approved_by")]   // other rows: those the user approved but did not create
    [InlineData("RESTRICT", "user_id")]        // checked at once, before or after the cascade
    public void KeyTheCascadeDoesNotEmpty_StillBlocks(string onDelete, string column)
    {
        var result = Analyze(
        [
            Key("fk_user", "CASCADE", ["user_id"], ["id"]),
            Key("fk_other", onDelete, [column], ["id"]),
        ], [("public", "users")]);

        result.Findings.Should().ContainSingle(f => f.RuleId == CascadeRuleIds.BreaksWithRestrict);
    }

    // ── Empty graph ────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_ReturnsResultWithSummaryFinding()
    {
        var result = CascadeAnalysisEngine.Empty("PostgreSql", "test_db");

        result.Paths.Should().BeEmpty();
        result.Findings.Should().HaveCount(1);
        result.Findings[0].RuleId.Should().Be(CascadeRuleIds.Summary);
    }

    [Fact]
    public void Empty_DisclaimerIsPresent()
    {
        var result = CascadeAnalysisEngine.Empty("PostgreSql", "test_db");

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Empty_SummaryAlwaysKeysPresent()
    {
        var result = CascadeAnalysisEngine.Empty("PostgreSql", "test_db");

        foreach (var ruleId in CascadeRuleIds.All)
            result.Summary.ByRule.Should().ContainKey(ruleId);

        foreach (var sev in Enum.GetNames<Severity>())
            result.Summary.BySeverity.Should().ContainKey(sev);
    }

    // ── Single-step CASCADE ────────────────────────────────────────────────────

    [Fact]
    public void SingleCascadeStep_ProducesOnePath_DepthOne()
    {
        // orders.user_id → users(id) CASCADE
        var fks = new[] { Edge("public", "orders", "public", "users") };
        var roots = new[] { ("public", "users") };

        var result = Analyze(fks, roots);

        result.Paths.Should().HaveCount(1);
        result.Paths[0].Depth.Should().Be(1);
        result.Paths[0].RootTable.Should().Be("public.users");
        result.Paths[0].IsCircular.Should().BeFalse();
        result.Paths[0].BrokenAt.Should().BeNull();
    }

    [Fact]
    public void SingleCascadeStep_LeafTable_IsChildTable()
    {
        var fks = new[] { Edge("public", "orders", "public", "users") };
        var roots = new[] { ("public", "users") };

        var result = Analyze(fks, roots);

        result.Paths[0].LeafTables.Should().ContainSingle()
            .Which.Should().Be("public.orders");
    }

    [Fact]
    public void SingleCascadeStep_StepDirection_ParentToChild()
    {
        var fks = new[] { Edge("public", "orders", "public", "users") };
        var roots = new[] { ("public", "users") };

        var result = Analyze(fks, roots);

        var step = result.Paths[0].Steps[0];
        step.FromTable.Should().Be("public.users");
        step.ToTable.Should().Be("public.orders");
        step.OnDelete.Should().Be("CASCADE");
    }

    // ── Multi-step chain ───────────────────────────────────────────────────────

    [Fact]
    public void ThreeLevelChain_ProducesOnePath_DepthThree()
    {
        // users → orders → order_items → item_reviews
        var fks = new[]
        {
            Edge("public", "orders", "public", "users", constraint: "fk_orders_users"),
            Edge("public", "order_items", "public", "orders", constraint: "fk_items_orders"),
            Edge("public", "item_reviews", "public", "order_items", constraint: "fk_reviews_items")
        };
        var roots = new[] { ("public", "users") };

        var result = Analyze(fks, roots);

        result.Paths.Should().HaveCount(1);
        result.Paths[0].Depth.Should().Be(3);
        result.Paths[0].Steps.Should().HaveCount(3);
    }

    [Fact]
    public void TwoBranchesFromRoot_ProducesTwoPaths()
    {
        // users → orders (branch 1) and users → addresses (branch 2)
        var fks = new[]
        {
            Edge("public", "orders",    "public", "users", constraint: "fk_orders_users"),
            Edge("public", "addresses", "public", "users", constraint: "fk_addresses_users")
        };
        var roots = new[] { ("public", "users") };

        var result = Analyze(fks, roots);

        result.Paths.Should().HaveCount(2);
        result.Paths.Should().OnlyContain(p => p.RootTable == "public.users");
        result.Paths.Select(p => p.LeafTables[0]).Should()
            .BeEquivalentTo(["public.orders", "public.addresses"]);
    }

    // ── Self-referencing ───────────────────────────────────────────────────────

    [Fact]
    public void SelfReferencingCascade_ProducesSelfReferencingFinding()
    {
        // categories.parent_id → categories(id) CASCADE
        var fks = new[]
        {
            Edge("public", "categories", "public", "categories",
                 constraint: "fk_categories_parent")
        };
        var roots = new[] { ("public", "categories") };

        var result = Analyze(fks, roots);

        result.Findings.Should().Contain(f => f.RuleId == CascadeRuleIds.SelfReferencing);
    }

    [Fact]
    public void SelfReferencingCascade_DoesNotRecurseInfinitely()
    {
        var fks = new[]
        {
            Edge("public", "categories", "public", "categories",
                 constraint: "fk_categories_parent")
        };
        var roots = new[] { ("public", "categories") };

        var result = Analyze(fks, roots);

        // Must complete and produce exactly one path (the single self-ref step)
        result.Paths.Should().HaveCount(1);
        result.Paths[0].Depth.Should().Be(1);
    }

    // ── SET NULL ───────────────────────────────────────────────────────────────

    [Fact]
    public void SetNullAction_ProducesBreaksWithSetNullFinding()
    {
        var fks = new[]
        {
            Edge("public", "audit_logs", "public", "users",
                 onDelete: "SET NULL", constraint: "fk_audit_logs_user")
        };
        var roots = new[] { ("public", "users") };

        var result = Analyze(fks, roots);

        result.Findings.Should().Contain(f => f.RuleId == CascadeRuleIds.BreaksWithSetNull);
    }

    [Fact]
    public void SetNullAction_PathHasBrokenAt()
    {
        var fks = new[]
        {
            Edge("public", "audit_logs", "public", "users",
                 onDelete: "SET NULL", constraint: "fk_audit_logs_user")
        };
        var roots = new[] { ("public", "users") };

        var result = Analyze(fks, roots);

        result.Paths.Should().ContainSingle(p => p.BrokenAt == "public.audit_logs");
    }

    // ── RESTRICT / NO ACTION ───────────────────────────────────────────────────

    [Fact]
    public void RestrictAction_ProducesBreaksWithRestrictFinding()
    {
        var fks = new[]
        {
            Edge("public", "blocked_table", "public", "users",
                 onDelete: "NO ACTION", constraint: "fk_blocked_user")
        };
        var roots = new[] { ("public", "users") };

        var result = Analyze(fks, roots);

        result.Findings.Should().Contain(f => f.RuleId == CascadeRuleIds.BreaksWithRestrict);
    }

    [Fact]
    public void RestrictAction_StepIsIncludedInPath()
    {
        var fks = new[]
        {
            Edge("public", "blocked_table", "public", "users",
                 onDelete: "RESTRICT", constraint: "fk_blocked_user")
        };
        var roots = new[] { ("public", "users") };

        var result = Analyze(fks, roots);

        result.Paths.Should().ContainSingle();
        result.Paths[0].Steps.Should().ContainSingle(s => s.OnDelete == "RESTRICT");
        result.Paths[0].BrokenAt.Should().Be("public.blocked_table");
    }

    // ── Cycle detection ────────────────────────────────────────────────────────

    [Fact]
    public void CircularCascade_IsDetected()
    {
        // A → B → C → A  (cycle)
        var fks = new[]
        {
            Edge("s", "b", "s", "a", constraint: "fk_b_a"),
            Edge("s", "c", "s", "b", constraint: "fk_c_b"),
            Edge("s", "a", "s", "c", constraint: "fk_a_c")   // closes the cycle
        };
        var roots = new[] { ("s", "a") };

        var result = Analyze(fks, roots);

        result.Findings.Should().Contain(f => f.RuleId == CascadeRuleIds.CircularPath);
        result.Paths.Should().Contain(p => p.IsCircular);
    }

    [Fact]
    public void CircularCascade_Severity_IsCritical()
    {
        var fks = new[]
        {
            Edge("s", "b", "s", "a", constraint: "fk_b_a"),
            Edge("s", "a", "s", "b", constraint: "fk_a_b")   // A → B → A
        };
        var roots = new[] { ("s", "a") };

        var result = Analyze(fks, roots);

        result.Findings
            .Where(f => f.RuleId == CascadeRuleIds.CircularPath)
            .Should().OnlyContain(f => f.Severity == Severity.Critical);
    }

    // ── Deep path ──────────────────────────────────────────────────────────────

    [Fact]
    public void SixLevelChain_ProducesDeepPathFinding()
    {
        // Build a linear chain 6 levels deep: t1 → t2 → t3 → t4 → t5 → t6
        var fks = Enumerable.Range(2, 6)
            .Select(i => Edge("s", $"t{i}", "s", $"t{i - 1}", constraint: $"fk_{i}"))
            .ToArray();
        var roots = new[] { ("s", "t1") };

        var result = Analyze(fks, roots);

        result.Findings.Should().Contain(f => f.RuleId == CascadeRuleIds.DeepPath);
        result.Findings
            .Where(f => f.RuleId == CascadeRuleIds.DeepPath)
            .Should().OnlyContain(f => f.Severity == Severity.High);
    }

    [Fact]
    public void ExactlyFiveLevelChain_DoesNotProduceDeepPathFinding()
    {
        // 5 levels (at threshold, not exceeding it)
        var fks = Enumerable.Range(2, 5)
            .Select(i => Edge("s", $"t{i}", "s", $"t{i - 1}", constraint: $"fk_{i}"))
            .ToArray();
        var roots = new[] { ("s", "t1") };

        var result = Analyze(fks, roots);

        result.Findings.Should().NotContain(f => f.RuleId == CascadeRuleIds.DeepPath);
    }

    // ── Depth cap ──────────────────────────────────────────────────────────────

    [Fact]
    public void DepthCap_TruncatesPathAtMaxDepth()
    {
        // Build 10-level chain but cap at 3
        var fks = Enumerable.Range(2, 10)
            .Select(i => Edge("s", $"t{i}", "s", $"t{i - 1}", constraint: $"fk_{i}"))
            .ToArray();
        var roots = new[] { ("s", "t1") };

        var result = Analyze(fks, roots, new CheckCascadePathsRequest(MaxDepth: 3));

        result.Paths.Should().OnlyContain(p => p.Depth <= 3);
    }

    // ── Row counts ─────────────────────────────────────────────────────────────

    [Fact]
    public void RowCounts_ArePopulatedInSteps()
    {
        var fks = new[] { Edge("public", "orders", "public", "users") };
        var roots = new[] { ("public", "users") };
        var rowCounts = new Dictionary<(string, string), long?>
        {
            [("public", "orders")] = 42L
        };

        var result = Analyze(fks, roots, rowCounts: rowCounts);

        result.Paths[0].Steps[0].EstimatedRowsAffected.Should().Be(42L);
    }

    [Fact]
    public void RowCounts_NullWhenNotProvided()
    {
        var fks = new[] { Edge("public", "orders", "public", "users") };
        var roots = new[] { ("public", "users") };

        var result = Analyze(fks, roots);

        result.Paths[0].Steps[0].EstimatedRowsAffected.Should().BeNull();
    }

    // ── Summary finding ────────────────────────────────────────────────────────

    [Fact]
    public void SummaryFinding_AlwaysPresent()
    {
        var fks = new[] { Edge("public", "orders", "public", "users") };
        var roots = new[] { ("public", "users") };

        var result = Analyze(fks, roots);

        result.Findings.Should().Contain(f => f.RuleId == CascadeRuleIds.Summary);
    }

    [Fact]
    public void SummaryFinding_Severity_IsInfo()
    {
        var fks = new[] { Edge("public", "orders", "public", "users") };
        var roots = new[] { ("public", "users") };

        var result = Analyze(fks, roots);

        result.Findings
            .Where(f => f.RuleId == CascadeRuleIds.Summary)
            .Should().OnlyContain(f => f.Severity == Severity.Info);
    }

    // ── Always-keys invariant ─────────────────────────────────────────────────

    [Fact]
    public void Summary_BySeverity_ContainsAllSeverityKeys()
    {
        var fks = new[] { Edge("public", "orders", "public", "users") };
        var roots = new[] { ("public", "users") };

        var result = Analyze(fks, roots);

        foreach (var sev in Enum.GetNames<Severity>())
            result.Summary.BySeverity.Should().ContainKey(sev);
    }

    [Fact]
    public void Summary_ByRule_ContainsAllCascadeRuleIds()
    {
        var fks = new[] { Edge("public", "orders", "public", "users") };
        var roots = new[] { ("public", "users") };

        var result = Analyze(fks, roots);

        foreach (var ruleId in CascadeRuleIds.All)
            result.Summary.ByRule.Should().ContainKey(ruleId);
    }

    // ── Deduplication ─────────────────────────────────────────────────────────

    [Fact]
    public void Findings_AreDeduplicated_WhenSameTableReachedViaTwoBranches()
    {
        // order_items reachable from users via two paths:
        // users → orders → order_items
        // users → special_orders → order_items
        var fks = new[]
        {
            Edge("s", "orders",        "s", "users",  constraint: "fk_orders_users"),
            Edge("s", "special_orders","s", "users",  constraint: "fk_special_users"),
            Edge("s", "order_items",   "s", "orders", constraint: "fk_items_orders",
                 onDelete: "SET NULL"),
            Edge("s", "order_items",   "s", "special_orders", constraint: "fk_items_special",
                 onDelete: "SET NULL")
        };
        var roots = new[] { ("s", "users") };

        var result = Analyze(fks, roots);

        var setNullFindings = result.Findings
            .Where(f => f.RuleId == CascadeRuleIds.BreaksWithSetNull && f.Table == "s.order_items")
            .ToList();

        // Should be deduplicated to one finding per (ruleId, table) pair
        setNullFindings.Should().HaveCount(1);
    }

    // ── Disclaimer ────────────────────────────────────────────────────────────

    [Fact]
    public void Disclaimer_IsAlwaysPresent()
    {
        var fks = new[] { Edge("public", "orders", "public", "users") };
        var roots = new[] { ("public", "users") };

        var result = Analyze(fks, roots);

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Disclaimer_IsAlwaysPresent_WhenEmpty()
    {
        var result = CascadeAnalysisEngine.Empty("SqlServer", "db");

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
    }

    // ── Multiple roots ────────────────────────────────────────────────────────

    [Fact]
    public void MultipleRoots_ProducePathsForEach()
    {
        var fks = new[]
        {
            Edge("public", "orders",    "public", "users",    constraint: "fk_orders_users"),
            Edge("public", "products",  "public", "categories", constraint: "fk_products_cat")
        };
        var roots = new[] { ("public", "users"), ("public", "categories") };

        var result = Analyze(fks, roots);

        result.Paths.Should().HaveCount(2);
        result.Paths.Select(p => p.RootTable).Should()
            .BeEquivalentTo(["public.users", "public.categories"]);
    }

    // ── Self-references are only "cascade" when they actually cascade ──────────

    [Fact]
    public void SelfReference_WithNoAction_IsNotReportedAsASelfReferencingCascade()
    {
        // SQL Server forbids a self-referencing ON DELETE CASCADE outright (Msg 1785), so a
        // hierarchy there is NO ACTION. Labelling it "Self-referencing CASCADE" would be a false
        // statement carrying Derived evidence and confidence 1.0.
        var result = Analyze(
            [Edge("public", "categories", "public", "categories", onDelete: "NO ACTION")],
            [("public", "categories")]);

        result.Findings.Should().NotContain(f => f.RuleId == CascadeRuleIds.SelfReferencing);
    }

    [Fact]
    public void SelfReference_WithCascade_IsReported()
    {
        var result = Analyze(
            [Edge("public", "categories", "public", "categories", onDelete: "CASCADE")],
            [("public", "categories")]);

        result.Findings.Should().Contain(f => f.RuleId == CascadeRuleIds.SelfReferencing);
    }

    // ── Path enumeration is bounded ────────────────────────────────────────────

    /// <summary>
    /// A branching factor of 4 over 8 levels is 4^8 = 65,536 paths. MaxDepth alone does not
    /// bound enumeration, which is what made the unbounded version a memory hazard.
    /// </summary>
    private static List<CascadeAnalysisEngine.FkEdge> BranchingGraph(int levels, int branching)
    {
        var edges = new List<CascadeAnalysisEngine.FkEdge>();

        for (var level = 0; level < levels; level++)
            for (var parent = 0; parent < (int)Math.Pow(branching, level); parent++)
                for (var child = 0; child < branching; child++)
                    edges.Add(Edge(
                        "public", $"t{level + 1}_{parent * branching + child}",
                        "public", level == 0 ? "root" : $"t{level}_{parent}",
                        constraint: $"fk_{level}_{parent}_{child}"));

        return edges;
    }

    [Fact]
    public void PathEnumeration_StopsAtMaxPaths()
    {
        var edges = BranchingGraph(levels: 8, branching: 4);

        var result = Analyze(edges, [("public", "root")],
            new CheckCascadePathsRequest(MaxDepth: 50, MaxPaths: 100));

        result.Paths.Count.Should().BeLessThanOrEqualTo(100);
        result.PathsTruncated.Should().BeTrue();
    }

    [Fact]
    public void PathEnumeration_TruncationIsReportedInCaveats()
    {
        var result = Analyze(
            BranchingGraph(levels: 8, branching: 4),
            [("public", "root")],
            new CheckCascadePathsRequest(MaxDepth: 50, MaxPaths: 50));

        result.Caveats.Should().Contain(c =>
            c.Contains("Path enumeration stopped") && c.Contains("complete picture"));
    }

    [Fact]
    public void PathEnumeration_SmallGraphIsNotReportedAsTruncated()
    {
        var result = Analyze(
            [Edge("public", "orders", "public", "users")],
            [("public", "users")]);

        result.PathsTruncated.Should().BeFalse();
        result.Caveats.Should().NotContain(c => c.Contains("Path enumeration stopped"));
    }

    [Fact]
    public void PathEnumeration_BudgetIsSharedAcrossRoots()
    {
        // One pathological root must not consume the budget and starve the others.
        var edges = BranchingGraph(levels: 8, branching: 4);
        edges.Add(Edge("public", "b", "public", "other_root"));

        var result = Analyze(edges, [("public", "root"), ("public", "other_root")],
            new CheckCascadePathsRequest(MaxDepth: 50, MaxPaths: 100));

        result.Paths.Count.Should().BeLessThanOrEqualTo(100);
        result.PathsTruncated.Should().BeTrue();
    }
}
