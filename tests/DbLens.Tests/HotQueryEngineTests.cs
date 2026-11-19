using DbLens.Core.Analysis;
using DbLens.Core.Analysis.HotQueries;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Models;
using DbLens.Core.Analysis.Verification;
using FluentAssertions;
using static DbLens.Tests.TestSchema;

namespace DbLens.Tests;

public sealed class HotQueryEngineTests
{
    // ── Redaction ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT * FROM t WHERE name = 'Mario' AND id = 42", "SELECT * FROM t WHERE name = ? AND id = ?")]
    [InlineData("SELECT * FROM t WHERE n = N'Rossi' -- mario@x.it", "SELECT * FROM t WHERE n = N?  ")]
    [InlineData("SELECT * FROM t /* card 4242 */ WHERE x > 1.5e3 AND y = .25", "SELECT * FROM t   WHERE x > ? AND y = ?")]
    [InlineData("SELECT t1.c2, @p0, #tmp1 FROM t1 WHERE b = 0x1F", "SELECT t1.c2, @p0, #tmp1 FROM t1 WHERE b = ?")]
    [InlineData("SELECT * FROM t WHERE balance > $12.50 AND fee < -$3", "SELECT * FROM t WHERE balance > $? AND fee < -$?")]
    public void Redact_OnSqlServer_RemovesEveryValue(string sql, string expected) =>
        SqlText.Redact(sql, DatabaseDialect.SqlServer).Should().Be(expected);

    [Theory]
    [InlineData("SELECT * FROM t WHERE a = $1 AND b = 'x' AND c = $$dollar 42$$", "SELECT * FROM t WHERE a = $1 AND b = ? AND c = ?")]
    [InlineData("SELECT \"Col 1\" FROM t WHERE e = E'it\\'s' AND n = 7", "SELECT \"Col 1\" FROM t WHERE e = E? AND n = ?")]
    [InlineData("SELECT * FROM t WHERE a = 1_000_000 AND b = 0o17 AND c = 0b101 AND d = 1_000.5", "SELECT * FROM t WHERE a = ? AND b = ? AND c = ? AND d = ?")]
    public void Redact_OnPostgres_KeepsParametersAndNames(string sql, string expected) =>
        SqlText.Redact(sql, DatabaseDialect.PostgreSql).Should().Be(expected);

    // ── What counts as workload ────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT 1", true)]
    [InlineData("  with x as (select 1) select * from x", true)]
    [InlineData("(@p0 int,@p1 nvarchar(4000))SELECT * FROM t WHERE id = @p0", true)]
    [InlineData("(SELECT 1) UNION (SELECT 2)", true)]
    [InlineData("UPDATE t SET a = ?", true)]
    [InlineData("CREATE TABLE t (id int)", false)]
    [InlineData("BEGIN", false)]
    [InlineData("SET search_path = public", false)]
    public void OnlyStatementsThatReadOrWriteRows_AreWorkload(string text, bool expected) =>
        HotQueryEngine.IsDataStatement(text).Should().Be(expected);

    private static readonly (string, string)[] Tables = [("app", "orders"), ("app", "payments"), ("audit", "orders")];

    [Fact]
    public void ANameInTwoSchemas_IsTheDefaultSchemasUnlessQualified()
    {
        HotQueryEngine.TablesNamed("SELECT * FROM app.orders o JOIN payments p ON p.order_id = o.id", Tables, DatabaseDialect.PostgreSql, "public")
            .Should().Equal(("app", "orders"), ("app", "payments"));
        HotQueryEngine.TablesNamed("SELECT * FROM orders", Tables, DatabaseDialect.PostgreSql, "public")
            .Should().BeEmpty("orders exists in two schemas, neither the default");
        HotQueryEngine.TablesNamed("SELECT * FROM orders", Tables, DatabaseDialect.PostgreSql, "audit")
            .Should().Equal(("audit", "orders"));
        HotQueryEngine.TablesNamed("SELECT * FROM [audit].[orders]", Tables, DatabaseDialect.SqlServer, "dbo")
            .Should().Equal(("audit", "orders"));
    }

    [Theory]
    [InlineData("SELECT payments, orders FROM app.orders WHERE payments > $1")]
    [InlineData("INSERT INTO app.orders (payments, id) SELECT payments(1), 2")]
    [InlineData("SELECT x.payments FROM app.orders x")]
    public void AColumnOrFunctionNamedLikeATable_IsNotATable(string sql) =>
        HotQueryEngine.TablesNamed(sql, Tables, DatabaseDialect.PostgreSql, "app").Should().Equal(("app", "orders"));

    [Fact]
    public void EveryTableOfAFromList_AndOfASubquery_IsNamed() =>
        HotQueryEngine.TablesNamed(
                "SELECT * FROM (SELECT id FROM ONLY app.payments) p, app.orders o WHERE o.id IN (SELECT 1 FROM audit.orders)",
                Tables, DatabaseDialect.PostgreSql, "public")
            .Should().Equal(("app", "payments"), ("app", "orders"), ("audit", "orders"));

    // ── Ranking and rules ──────────────────────────────────────────────────────

    private static HotQueryStats Stats(string id, string text, double totalMs, long calls = 10, long rows = 10,
        long pagesRead = 100, long fromDisk = 0, long? temp = null) =>
        new(id, text, calls, totalMs, rows, pagesRead, fromDisk, temp);

    [Fact]
    public void TheWorkloadIsRankedByTotalTime_OwnProbesAndCatalogQueriesSetAside()
    {
        var ranked = HotQueryEngine.Rank(
        [
            Stats("1", "SELECT * FROM app.orders", 10),
            Stats("2", "SELECT * FROM app.payments", 50),
            Stats("3", "SELECT COUNT(*) AS compared_rows, 0 AS divergent_rows FROM app.orders", 900),
            Stats("4", "SELECT * FROM pg_class", 800),
            Stats("5", "CREATE INDEX ix ON app.orders (id)", 700),
        ], Tables, DatabaseDialect.PostgreSql, "public", new FindHotQueriesRequest());

        ranked.Select(r => r.Query.QueryId).Should().Equal("2", "1");
    }

    [Fact]
    public void Statistics_GiveTheirOwnFindings()
    {
        var request = new FindHotQueriesRequest();
        var ranked = HotQueryEngine.Rank(
        [
            Stats("reads", "SELECT * FROM app.orders WHERE lower(x) = 'y'", 100, rows: 10, pagesRead: 5_000),
            Stats("disk", "SELECT * FROM app.payments", 90, rows: 10_000, pagesRead: 10_000, fromDisk: 4_000),
            Stats("spill", "SELECT * FROM app.orders ORDER BY 1", 80, rows: 10_000, pagesRead: 100, temp: 50),
            Stats("once", "SELECT * FROM app.orders o", 70, calls: 1, rows: 1, pagesRead: 9_000),
        ], Tables, DatabaseDialect.PostgreSql, "public", request);

        var findings = HotQueryEngine.StatisticsFindings(ranked, request, DatabaseDialect.PostgreSql).ToList();

        findings.Select(f => (f.RuleId, (string)f.Metadata!["queryId"])).Should().BeEquivalentTo(
        [
            (HotQueryRuleIds.ReadsPerRow, "reads"),
            (HotQueryRuleIds.DiskReads, "disk"),
            (HotQueryRuleIds.TempSpill, "spill"),
        ], "a query that ran once has no average to speak of");
        findings.Should().OnlyContain(f => f.Evidence == Evidence.Measured);
        ranked.Single(r => r.Query.QueryId == "reads").Query.Text.Should().Contain("lower(x) = ?");
    }

    [Fact]
    public void WithoutASource_NothingIsMeasured_AndTheResultSaysSo()
    {
        var result = HotQueryEngine.Build([], [], new HotQuerySourceInfo(null, null, ["pg_stat_statements is not loaded"]), 0, []);

        result.Findings.Should().ContainSingle().Which.Description.Should().Contain("not the same as no slow query");
        result.Availability.Missing.Should().ContainSingle();
    }

    // ── Join fan-out ───────────────────────────────────────────────────────────

    private static readonly TableDetail[] Shop =
    [
        Table("orders", [Col("id", "int"), Col("billing_id", "int"), Col("shipping_id", "int")]),
        Table("addresses", [Col("id", "int")]),
        Table("items", [Col("id", "int"), Col("order_id", "int")]),
        Table("payments", [Col("id", "int"), Col("order_id", "int")]),
        Table("notes", [Col("id", "int"), Col("item_id", "int")]),
        Table("invoices", [Col("id", "int"), Col("order_id", "int")]) with
        {
            UniqueConstraints = [new UniqueConstraintDetail("uq_invoice_order", ["order_id"])],
        },
    ];

    private static IReadOnlyList<JoinFanOut.Shape> Detect(string sql)
    {
        var tables = Shop.Select(t => (t.Schema, t.Name)).ToList();
        var ranked = HotQueryEngine.Rank([Stats("q", sql, 10)], tables, DatabaseDialect.PostgreSql, "public", new FindHotQueriesRequest());
        return JoinFanOut.Detect(ranked, Shop, "public", DatabaseDialect.PostgreSql);
    }

    [Fact]
    public void TwoChildrenOfOneParent_Multiply()
    {
        var shape = Detect("SELECT * FROM orders o JOIN items i ON i.order_id = o.id LEFT JOIN payments p ON p.order_id = o.id").Single();

        shape.Parent.Name.Should().Be("orders");
        new[] { shape.FirstChild.Name, shape.SecondChild.Name }.Should().BeEquivalentTo(["items", "payments"]);
    }

    [Theory]
    [InlineData("SELECT * FROM orders o JOIN items i ON i.order_id = o.id JOIN notes n ON n.item_id = i.id")]
    [InlineData("SELECT * FROM orders o JOIN items i ON i.order_id = o.id JOIN invoices v ON v.order_id = o.id")]
    [InlineData("SELECT * FROM orders o JOIN items i ON i.order_id = o.id")]
    [InlineData("SELECT * FROM orders o JOIN addresses b ON b.id = o.billing_id JOIN addresses s ON s.id = o.shipping_id")]
    [InlineData("SELECT * FROM orders o JOIN items i ON i.order_id = o.id WHERE EXISTS (SELECT 1 FROM payments p WHERE p.order_id = o.id)")]
    [InlineData("SELECT o.id FROM orders o JOIN items i ON i.order_id = o.id UNION SELECT o.id FROM orders o JOIN payments p ON p.order_id = o.id")]
    public void AChainAOneToOneOrASingleChild_DoesNotMultiply(string sql) =>
        Detect(sql).Should().BeEmpty();

    [Fact]
    public void TheSameShapeJoinedTheOtherWayRound_IsOne() =>
        Detect("SELECT * FROM orders o JOIN payments p ON p.order_id = o.id JOIN items i ON i.order_id = o.id").Single().Target.Key
            .Should().Be(Detect("SELECT * FROM orders o JOIN items i ON i.order_id = o.id JOIN payments p ON p.order_id = o.id").Single().Target.Key);

    [Fact]
    public void TheProbe_IsAggregateOnly_AndPassesTheGuard()
    {
        var shape = Detect("SELECT * FROM orders o JOIN items i ON i.order_id = o.id JOIN payments p ON p.order_id = o.id").Single();

        foreach (var dialect in new[] { DatabaseDialect.PostgreSql, DatabaseDialect.SqlServer })
        {
            var probe = JoinFanOut.Probe(shape, new Dictionary<(string, string), long?>(), dialect);
            probe.Sql.Should().Contain("compared_rows").And.Contain("divergent_rows").And.NotContain(";");
            StatementGuard.IsReadOnlySelect(probe.Sql, dialect).Allowed.Should().BeTrue();
            if (dialect == DatabaseDialect.SqlServer) probe.Sql.Should().Contain("COUNT_BIG(");
        }
    }

    [Fact]
    public void NoMoreRowsThanSeparateReads_IsAFactAtInfo()
    {
        var shape = Detect("SELECT * FROM orders o JOIN items i ON i.order_id = o.id JOIN payments p ON p.order_id = o.id").Single();
        var probe = JoinFanOut.Probe(shape, new Dictionary<(string, string), long?>(), DatabaseDialect.PostgreSql);

        var refuted = JoinFanOut.Apply(JoinFanOut.ToSignal(shape), VerificationMeasurement.Measured(probe, 40, 20, 1));

        refuted.Evidence.Should().Be(Evidence.Measured);
        refuted.Severity.Should().Be(Severity.Info);
        VerificationMeasurement.Measured(probe, 0, 0, 1).Status.Should().Be(VerificationStatus.Inconclusive);
    }
}
