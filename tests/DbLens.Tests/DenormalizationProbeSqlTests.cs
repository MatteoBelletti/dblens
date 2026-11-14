using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Denormalization;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Models;
using FluentAssertions;

using static DbLens.Tests.TestSchema;

namespace DbLens.Tests;

/// <summary>
/// Tests for the SQL that denormalization rules attach to their signals.
///
/// Two properties matter more than the exact text. First, a probe must return counts only:
/// verification exists to test a heuristic, not to become a back door around the promise that
/// this tool never returns row data. Second, a probe must survive StatementGuard, because the
/// guard is what will let the inspector execute it.
/// </summary>
public sealed class DenormalizationProbeSqlTests
{
    // ── Builders ───────────────────────────────────────────────────────────────

    private static AnalyzeDenormalizationResult Analyze(
        IReadOnlyList<TableDetail> tables, DatabaseDialect dialect) =>
        DenormalizationAnalysisEngine.Analyze(tables, new AnalyzeDenormalizationRequest(), dialect);

    private static SqlProposal? ProposalFor(
        IReadOnlyList<TableDetail> tables, string ruleId, DatabaseDialect dialect) =>
        Analyze(tables, dialect).Signals.FirstOrDefault(f => f.RuleId == ruleId)?.SqlFix;

    // ── Fixtures ───────────────────────────────────────────────────────────────

    /// <summary>customers.order_count with exactly one child table — the verifiable shape.</summary>
    private static TableDetail[] CountCacheFixture(string columnName = "order_count")
    {
        var fk = Fk("public", "orders", "customer_id", "public", "customers", "id");
        return
        [
            Table("customers", [Col("id", "bigint"), Col(columnName, "integer")], incoming: [fk]),
            Table("orders", [Col("id", "bigint"), Col("customer_id", "bigint")], outgoing: [fk]),
        ];
    }

    /// <summary>
    /// fiscal_code copied from parent to FK-linked child. Defaults to varchar, which is
    /// comparable on both dialects — "text" deliberately is not, and has its own test.
    /// </summary>
    private static TableDetail[] CopiedColumnFixture(string dataType = "varchar")
    {
        var fk = Fk("public", "notes", "profile_id", "public", "profiles", "id");
        return
        [
            Table("profiles", [Col("id", "bigint"), Col("fiscal_code", dataType)], incoming: [fk]),
            Table("notes", [Col("id", "bigint"), Col("profile_id", "bigint"), Col("fiscal_code", dataType)],
                outgoing: [fk]),
        ];
    }

    // ── The probes return counts, never row values ─────────────────────────────

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.SqlServer)]
    public void CachedCountProbe_SelectsOnlyAggregates(DatabaseDialect dialect)
    {
        var sql = ProposalFor(CountCacheFixture(), DenormalizationRuleIds.DerivedColumn, dialect)!.Sql;

        sql.Should().Contain("compared_rows");
        sql.Should().Contain("divergent_rows");
        // The old query selected p.id and the stored value; a probe must never do that.
        sql.Should().NotContain("AS stored,");
        sql.Should().NotContain("AS actual,");
    }

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.SqlServer)]
    public void CopiedColumnProbe_SelectsOnlyAggregates(DatabaseDialect dialect)
    {
        var sql = ProposalFor(CopiedColumnFixture(), DenormalizationRuleIds.DuplicatedColumn, dialect)!.Sql;

        sql.Should().Contain("compared_rows");
        sql.Should().Contain("divergent_rows");
        sql.Should().NotContain("child_value");
        sql.Should().NotContain("parent_value");
    }

    [Fact]
    public void CachedCountProbe_CountsTheForeignKeyNotStar()
    {
        // COUNT(*) under a LEFT JOIN returns 1 for a parent with no children at all,
        // which would report every childless parent as divergent.
        var sql = ProposalFor(CountCacheFixture(), DenormalizationRuleIds.DerivedColumn,
            DatabaseDialect.PostgreSql)!.Sql;

        sql.Should().Contain("COUNT(c.\"customer_id\")");
    }

    // ── Probes are marked executable; sketches are not ─────────────────────────

    [Fact]
    public void Probes_AreMarkedSafeToExecute()
    {
        ProposalFor(CountCacheFixture(), DenormalizationRuleIds.DerivedColumn, DatabaseDialect.PostgreSql)!
            .SafeToExecuteAutomatically.Should().BeTrue();
        ProposalFor(CopiedColumnFixture(), DenormalizationRuleIds.DuplicatedColumn, DatabaseDialect.PostgreSql)!
            .SafeToExecuteAutomatically.Should().BeTrue();
    }

    // ── Only counts can be verified ────────────────────────────────────────────

    [Theory]
    [InlineData("order_count")]
    [InlineData("item_count")]
    [InlineData("num_orders")]
    public void CountFamilyNames_GetAProbe(string columnName)
    {
        ProposalFor(CountCacheFixture(columnName), DenormalizationRuleIds.DerivedColumn,
            DatabaseDialect.PostgreSql).Should().NotBeNull();
    }

    [Theory]
    [InlineData("total")]
    [InlineData("order_total")]
    [InlineData("amount_sum")]
    [InlineData("price_avg")]
    public void SumFamilyNames_GetNoProbe(string columnName)
    {
        // Checking a monetary total against COUNT(children) disagrees on nearly every row.
        // The column being summed is nowhere in the catalog, so there is nothing to compare.
        var tables = CountCacheFixture(columnName);
        var signal = Analyze(tables, DatabaseDialect.PostgreSql).Signals
            .Single(f => f.RuleId == DenormalizationRuleIds.DerivedColumn);

        signal.SqlFix.Should().BeNull();
        signal.Description.Should().Contain("cannot be checked against the data");
        signal.Metadata!["aggregateFamily"].Should().Be("SumLike");
    }

    // ── Preconditions that must suppress the probe ─────────────────────────────

    [Fact]
    public void CompositePrimaryKey_GetsNoProbe()
    {
        var fk = Fk("public", "orders", "customer_id", "public", "customers", "id");
        var tables = new[]
        {
            Table("customers", [Col("id", "bigint"), Col("tenant_id", "bigint"), Col("order_count", "integer")],
                incoming: [fk], pkColumns: ["id", "tenant_id"]),
            Table("orders", [Col("id", "bigint"), Col("customer_id", "bigint")], outgoing: [fk]),
        };

        ProposalFor(tables, DenormalizationRuleIds.DerivedColumn, DatabaseDialect.PostgreSql)
            .Should().BeNull();
    }

    [Fact]
    public void AmbiguousChildTable_GetsNoProbe()
    {
        // Two children and no name match: picking one would be a coin flip, and a measurement
        // against the wrong table is worse than no measurement.
        var fkA = Fk("public", "alpha", "customer_id", "public", "customers", "id", "fk_a");
        var fkB = Fk("public", "beta", "customer_id", "public", "customers", "id", "fk_b");

        var tables = new[]
        {
            Table("customers", [Col("id", "bigint"), Col("widget_count", "integer")], incoming: [fkA, fkB]),
            Table("alpha", [Col("id", "bigint"), Col("customer_id", "bigint")], outgoing: [fkA]),
            Table("beta", [Col("id", "bigint"), Col("customer_id", "bigint")], outgoing: [fkB]),
        };

        ProposalFor(tables, DenormalizationRuleIds.DerivedColumn, DatabaseDialect.PostgreSql)
            .Should().BeNull();
    }

    [Fact]
    public void AmbiguousChildResolvedByName_GetsAProbeAndRecordsHow()
    {
        var fkOrders = Fk("public", "orders", "customer_id", "public", "customers", "id", "fk_o");
        var fkOther = Fk("public", "visits", "customer_id", "public", "customers", "id", "fk_v");

        var tables = new[]
        {
            Table("customers", [Col("id", "bigint"), Col("order_count", "integer")],
                incoming: [fkOrders, fkOther]),
            Table("orders", [Col("id", "bigint"), Col("customer_id", "bigint")], outgoing: [fkOrders]),
            Table("visits", [Col("id", "bigint"), Col("customer_id", "bigint")], outgoing: [fkOther]),
        };

        var signal = Analyze(tables, DatabaseDialect.PostgreSql).Signals
            .Single(f => f.RuleId == DenormalizationRuleIds.DerivedColumn);

        signal.SqlFix.Should().NotBeNull();
        // How the child was chosen stays inferred even when the count is measured.
        signal.Metadata!["childResolution"].Should().Be("name-match");
    }

    [Fact]
    public void SoleChildResolution_IsRecordedToo()
    {
        var signal = Analyze(CountCacheFixture(), DatabaseDialect.PostgreSql).Signals
            .Single(f => f.RuleId == DenormalizationRuleIds.DerivedColumn);

        signal.Metadata!["childResolution"].Should().Be("sole-fk");
    }

    [Fact]
    public void PostgresTextIsComparable_ButSqlServerTextIsNot()
    {
        // "text" is the ordinary string type on PostgreSQL and the deprecated LOB type with no
        // equality operator on SQL Server. One shared exclusion list would silently disable
        // verification for most PostgreSQL string columns.
        ProposalFor(CopiedColumnFixture("text"), DenormalizationRuleIds.DuplicatedColumn,
            DatabaseDialect.PostgreSql).Should().NotBeNull();
        ProposalFor(CopiedColumnFixture("text"), DenormalizationRuleIds.DuplicatedColumn,
            DatabaseDialect.SqlServer).Should().BeNull();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("polygon")]
    public void PostgresNonComparableTypes_GetNoProbe(string dataType)
    {
        ProposalFor(CopiedColumnFixture(dataType), DenormalizationRuleIds.DuplicatedColumn,
            DatabaseDialect.PostgreSql).Should().BeNull();
    }

    // ── Guard conformance: every probe must be executable ──────────────────────

    public static TheoryData<DatabaseDialect> Dialects() =>
        new() { DatabaseDialect.PostgreSql, DatabaseDialect.SqlServer };

    [Theory]
    [MemberData(nameof(Dialects))]
    public void EveryProbe_PassesTheStatementGuard(DatabaseDialect dialect)
    {
        var fixtures = new[] { CountCacheFixture(), CopiedColumnFixture() };

        foreach (var tables in fixtures)
        {
            var probes = Analyze(tables, dialect).Signals
                .Select(f => f.SqlFix)
                .Where(p => p is { Kind: SqlProposalKind.ReadOnlyProbe })
                .ToList();

            probes.Should().NotBeEmpty();

            foreach (var probe in probes)
            {
                var guard = StatementGuard.IsReadOnlySelect(probe!.Sql, dialect);
                guard.Allowed.Should().BeTrue(
                    "a generated probe must be executable; rejected with: {0}", guard.RejectionReason);

                probe.Sql.Should().NotContain(";", "a probe must be a single statement");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void ProbesAreInertAgainstHostileIdentifiers(DatabaseDialect dialect)
    {
        // An identifier is attacker-influenced whenever the attacker can create a table.
        var fk = Fk("public", "x\"; DROP TABLE y --", "a]b; DROP", "public", "customers", "id");
        var tables = new[]
        {
            Table("customers", [Col("id", "bigint"), Col("order_count", "integer")], incoming: [fk]),
            Table("x\"; DROP TABLE y --", [Col("id", "bigint"), Col("a]b; DROP", "bigint")], outgoing: [fk]),
        };

        var probes = Analyze(tables, dialect).Signals
            .Select(f => f.SqlFix)
            .Where(p => p is { Kind: SqlProposalKind.ReadOnlyProbe })
            .ToList();

        foreach (var probe in probes)
        {
            StatementGuard.IsReadOnlySelect(probe!.Sql, dialect).Allowed.Should().BeTrue(
                "quoting must render the payload inert ({0})", dialect);

            var stripped = SqlText.StripCommentsAndStrings(probe.Sql, dialect);
            stripped.Should().NotContain("DROP");
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void EveryProbe_QuotesItsIdentifiers(DatabaseDialect dialect)
    {
        var sql = ProposalFor(CountCacheFixture(), DenormalizationRuleIds.DerivedColumn, dialect)!.Sql;

        var open = dialect == DatabaseDialect.PostgreSql ? "\"customers\"" : "[customers]";
        var col = dialect == DatabaseDialect.PostgreSql ? "\"order_count\"" : "[order_count]";

        sql.Should().Contain(open);
        sql.Should().Contain(col);
    }
}
