using DbLens.Core.Analysis;
using DbLens.Core.Analysis.QueryPlan;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// plan-implicit-conversion on each engine's own way of comparing a column with a value of another
/// type. SQL Server names the conversion in a plan warning (derived); PostgreSQL only shows the cast
/// in a sequential scan's filter (a signal).
/// </summary>
public abstract class PlanConversionTestsBase(IDatabaseFixture fixture)
{
    private bool ShouldSkip => !fixture.IsReachable;

    /// <summary>A query on the demo that converts <see cref="Column"/> of <see cref="Table"/>.</summary>
    protected abstract string Query { get; }

    protected abstract string Table { get; }

    protected abstract string Column { get; }

    protected abstract Evidence Evidence { get; }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task AColumnComparedWithAnotherType_IsReported()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await fixture.Inspector!.ExplainQueryAsync(new ExplainQueryRequest(Query));

        var finding = result.Findings.Concat(result.Signals)
            .Should().ContainSingle(f => f.RuleId == QueryPlanRuleIds.ImplicitConversion).Subject;
        finding.Table.Should().Be(Table);
        finding.Columns.Should().Equal(Column);
        finding.Evidence.Should().Be(Evidence);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task AComparisonInTheColumnsType_IsNot()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await fixture.Inspector!.ExplainQueryAsync(
            new ExplainQueryRequest($"SELECT id FROM {fixture.UserSchema}.audit_logs WHERE user_id = 1"));

        result.Findings.Concat(result.Signals).Should().NotContain(f => f.RuleId == QueryPlanRuleIds.ImplicitConversion);
    }
}

public sealed class PostgresPlanConversionTests(PostgresFixture fixture)
    : PlanConversionTestsBase(fixture), IClassFixture<PostgresFixture>
{
    // The literal reads like a cast, and must not be taken for one.
    protected override string Query => "SELECT id FROM public.audit_logs WHERE user_id = 1.5 AND action <> '(entity_id)::numeric'";
    protected override string Table => "public.audit_logs";
    protected override string Column => "user_id";
    protected override Evidence Evidence => Evidence.Inferred;
}

public sealed class SqlServerPlanConversionTests(SqlServerFixture fixture)
    : PlanConversionTestsBase(fixture), IClassFixture<SqlServerFixture>
{
    protected override string Query => "SELECT id FROM dbo.audit_logs WHERE ip_address = N'1.2.3.4'";
    protected override string Table => "dbo.audit_logs";
    protected override string Column => "ip_address";
    protected override Evidence Evidence => Evidence.Derived;
}
