using DbLens.Core.Analysis.Glossary;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Analysis.Receipts;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// query_with_receipt, build_glossary and the read-only-login rule against every dialect. Expected
/// figures come from db/*/02-seed.sql. Counts read payments, never orders: SQL Server answers
/// COUNT(*) on orders by scanning idx_orders_status, which the unused-index test needs untouched.
/// </summary>
public abstract class ReceiptAndPrivilegeTestsBase(IDatabaseFixture fixture)
{
    protected IDatabaseFixture Fixture { get; } = fixture;

    private bool ShouldSkip => !Fixture.IsReachable;

    private string Q(string table) => $"{Fixture.UserSchema}.{table}";

    private Task<QueryWithReceiptResult> RunAsync(string sql, string? check = null, int maxRows = 100) =>
        Fixture.Inspector!.QueryWithReceiptAsync(new QueryWithReceiptRequest(sql, check, MaxRows: maxRows));

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Receipt_CarriesTheFigure_TheSqlAndTheTablesRead()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var sql = $"SELECT COUNT(*) AS payments FROM {Q("payments")}";
        var result = await RunAsync(sql);

        Convert.ToInt64(result.Result.Rows.Single().Single()).Should().Be(35);
        result.Receipt.Sql.Should().Be(sql);
        result.Receipt.TablesTouched.Should().Contain(t => t.Equals("payments", StringComparison.OrdinalIgnoreCase));
        result.Receipt.Database.Should().Be("dblens_demo");
        result.Caveats.Should().Contain(c => c.Contains("No cross-check"));
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task CrossCheck_ByAnotherRoute_Agrees()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await RunAsync(
            $"SELECT COUNT(*) FROM {Q("payments")}",
            $"SELECT SUM(n) FROM (SELECT payment_method, COUNT(*) AS n FROM {Q("payments")} GROUP BY payment_method) per_method");

        result.Receipt.CrossCheck!.Status.Should().Be(CrossCheckStatus.Agree);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task CrossCheck_ThatDiffers_IsReportedAsDisagreeing()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await RunAsync($"SELECT COUNT(*) FROM {Q("payments")}", $"SELECT COUNT(*) FROM {Q("users")}");

        result.Receipt.CrossCheck!.Status.Should().Be(CrossCheckStatus.Disagree);
        result.Caveats.Should().Contain(c => c.Contains("DISAGREES"));
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Rows_AreCapped()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await RunAsync($"SELECT id FROM {Q("order_items")}", maxRows: 5);

        result.Result.Rows.Should().HaveCount(5);
        result.Result.Truncated.Should().BeTrue();
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Write_IsRejectedBeforeReachingTheDatabase()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var act = () => RunAsync($"DELETE FROM {Q("orders")}");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task WriteCapableLogin_OnThisMachine_MayExecute()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var admin = Fixture.CreateAdminInspector();

        (await admin.ExplainQueryAsync(new ExplainQueryRequest("SELECT 1", Analyze: true))).Should().NotBeNull(
            "a database on localhost is the developer's own; the read-only-login check does not apply");
        (await admin.QueryWithReceiptAsync(new QueryWithReceiptRequest("SELECT 1"))).Result.Rows.Should().ContainSingle();
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task WriteCapableLogin_IsRefused_WhenTheCheckCoversLocalhost()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var admin = Fixture.CreateAdminInspector(readOnlyLoginOnLocalhost: true);

        await ((Func<Task>)(() => admin.QueryWithReceiptAsync(new QueryWithReceiptRequest("SELECT 1"))))
            .Should().ThrowAsync<DbLens.Core.Models.ExecutionRefusedException>().WithMessage("*login can write*");
        await ((Func<Task>)(() => admin.ExplainQueryAsync(new ExplainQueryRequest("SELECT 1", Analyze: true))))
            .Should().ThrowAsync<DbLens.Core.Models.ExecutionRefusedException>();

        var estimated = await admin.ExplainQueryAsync(new ExplainQueryRequest("SELECT 1"));
        estimated.Should().NotBeNull("an estimated plan executes nothing and needs no read-only login");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ReadOnlyLogin_MayExecute()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var plan = await Fixture.Inspector!.ExplainQueryAsync(new ExplainQueryRequest($"SELECT COUNT(*) FROM {Q("users")}", Analyze: true));

        plan.Should().NotBeNull();
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Glossary_ShowsDocumentedMeanings_AndTheViewDerivedJoin()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Fixture.Inspector!.BuildGlossaryAsync(new GlossaryRequest(Schema: Fixture.UserSchema, VerifyJoins: true));

        result.Markdown.Should().Contain("Meaning: Customer orders, one row per checkout");
        result.Markdown.Should().Contain("Order total in EUR, VAT included");
        result.Markdown.Should().Contain($"`{Q("shipments")}(ship_to)` → `{Q("addresses")}(id)` — verified");
        result.Summary.DocumentedColumns.Should().Be(1);
    }
}

public sealed class PostgresReceiptAndPrivilegeTests(PostgresFixture fixture)
    : ReceiptAndPrivilegeTestsBase(fixture), IClassFixture<PostgresFixture>;

public sealed class SqlServerReceiptAndPrivilegeTests(SqlServerFixture fixture)
    : ReceiptAndPrivilegeTestsBase(fixture), IClassFixture<SqlServerFixture>;
