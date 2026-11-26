using DbLens.Core.Analysis;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Models;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>Parameters in a statement given to explain_query: <c>$1</c> from pg_stat_statements, <c>@__p_0</c> from EF Core's logs.</summary>
public sealed class ParameterNamesTests
{
    [Theory]
    [InlineData("SELECT * FROM t WHERE id = $1 AND x = $2 AND y = $1", DatabaseDialect.PostgreSql, "$1,$2")]
    [InlineData("SELECT * FROM t WHERE id = @__p_0 AND x = @p1 AND y = @__p_0", DatabaseDialect.PostgreSql, "@__p_0,@p1")]
    [InlineData("SELECT * FROM t WHERE id = @p0", DatabaseDialect.SqlServer, "@p0")]
    [InlineData("SELECT @@ROWCOUNT", DatabaseDialect.SqlServer, "")]
    [InlineData("SELECT * FROM t WHERE doc @@ to_tsquery('a') AND tags @> ARRAY[1]", DatabaseDialect.PostgreSql, "")]
    [InlineData("SELECT '$1', '@p0', \"@p1\" FROM t -- @p2 and $3", DatabaseDialect.PostgreSql, "")]
    [InlineData("SELECT 1", DatabaseDialect.PostgreSql, "")]
    public void ParametersAreFoundInTheCodeOnly(string sql, DatabaseDialect dialect, string expected) =>
        string.Join(',', SqlText.ParameterNames(sql, dialect)).Should().Be(expected);

    [Fact]
    public void NamedParameters_BecomePositionalInOrderOfFirstAppearance_AndTheRestOfTheTextIsUntouched()
    {
        var (sql, count) = SqlText.PositionalParameters("SELECT '@a' /* @c */, \"@d\" FROM t WHERE x = @__p_0 AND y = @p1 AND z = @__p_0");

        sql.Should().Be("SELECT '@a' /* @c */, \"@d\" FROM t WHERE x = $1 AND y = $2 AND z = $1");
        count.Should().Be(2);
    }

    [Fact]
    public void PositionalParameters_AreCountedByTheirHighestNumber_AndLeftAlone()
    {
        SqlText.PositionalParameters("SELECT $3, $1").Should().Be(("SELECT $3, $1", 3));
        SqlText.PositionalParameters("SELECT 1").Should().Be(("SELECT 1", 0));
    }

    [Fact]
    public void BothFormsInOneText_AreRefused() =>
        new Action(() => SqlText.PositionalParameters("SELECT $1, @p0")).Should().Throw<ArgumentException>().WithMessage("*both*");
}

public abstract class ParameterizedExplainTestsBase(IDatabaseFixture fixture)
{
    private bool ShouldSkip => !fixture.IsReachable;

    protected abstract string QueryWithParameter { get; }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task AnalyzeWithParameters_IsRefused_ForTheValuesAreMissing()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var call = () => fixture.Inspector!.ExplainQueryAsync(new ExplainQueryRequest(QueryWithParameter, Analyze: true));

        await call.Should().ThrowAsync<ArgumentException>().WithMessage("*cannot be executed*analyze false*");
    }
}

public sealed class PostgresParameterizedExplainTests(PostgresFixture fixture)
    : ParameterizedExplainTestsBase(fixture), IClassFixture<PostgresFixture>
{
    protected override string QueryWithParameter => "SELECT id FROM public.users WHERE id = $1";

    [SkippableTheory]
    [InlineData("SELECT id FROM public.users WHERE id = $1", false)]
    [InlineData("SELECT id FROM public.users WHERE id = @__p_0 AND email = @__p_1", true)]
    [Trait("Category", "Integration")]
    public async Task AStatementWithParameters_GetsItsGenericPlan_AndSaysSo(string sql, bool named)
    {
        Skip.If(!fixture.IsReachable, TestSkip.Reason);

        var result = await fixture.Inspector!.ExplainQueryAsync(new ExplainQueryRequest(sql));

        result.PlanText.Should().Contain("users");
        result.Caveats.Should().Contain(c => c.Contains("Generic plan") && c.Contains("without their values"));
        result.Caveats.Any(c => c.Contains("@__p_0 = $1")).Should().Be(named);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task APreparedStatementIsNotLeftOnTheConnection()
    {
        Skip.If(!fixture.IsReachable, TestSkip.Reason);

        for (var i = 0; i < 3; i++)
            await fixture.Inspector!.ExplainQueryAsync(new ExplainQueryRequest(QueryWithParameter));
    }
}

public sealed class SqlServerParameterizedExplainTests(SqlServerFixture fixture)
    : ParameterizedExplainTestsBase(fixture), IClassFixture<SqlServerFixture>
{
    protected override string QueryWithParameter => "SELECT id FROM dbo.users WHERE id = @p0";

    // A genuine difference: SHOWPLAN_XML needs every variable declared, and a guessed type plans another query.
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task AnEstimatedPlanWithParameters_IsRefused_NotPlannedWithAGuessedType()
    {
        Skip.If(!fixture.IsReachable, TestSkip.Reason);

        var call = () => fixture.Inspector!.ExplainQueryAsync(new ExplainQueryRequest(QueryWithParameter));

        await call.Should().ThrowAsync<ArgumentException>().WithMessage("*parameters (@p0)*types declared*");
    }
}
