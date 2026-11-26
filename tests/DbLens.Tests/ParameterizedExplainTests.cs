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
    [InlineData("SELECT * FROM t WHERE price > $5", DatabaseDialect.SqlServer, "")]
    [InlineData("SELECT * FROM t WHERE tags <@ARRAY['a'] AND doc @> '{}'", DatabaseDialect.PostgreSql, "")]
    [InlineData("EXEC sp_executesql N'SELECT * FROM t WHERE id = @p0', N'@p0 int', @p0 = 5", DatabaseDialect.SqlServer, "")]
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

    [Theory]
    [InlineData("SELECT $0")]
    [InlineData("SELECT $99999999999")]
    [InlineData("SELECT $65536")]
    public void ANumberPostgreSqlWouldNotAccept_IsRefusedWithAReason(string sql) =>
        new Action(() => SqlText.PositionalParameters(sql)).Should().Throw<ArgumentException>().WithMessage("*not a parameter number*");

    [Fact]
    public void BothFormsInOneText_AreRefused() =>
        new Action(() => SqlText.PositionalParameters("SELECT $1, @p0")).Should().Throw<ArgumentException>().WithMessage("*both*");
}

/// <summary>Refusals that come before any connection: offline, for both dialects.</summary>
public sealed class ParameterizedExplainRefusalTests
{
    private const string Connection = "Server=localhost,1;Database=x;User Id=u;Password=p;TrustServerCertificate=true";

    private static DbLens.Core.Abstractions.IDatabaseInspector Inspector(DatabaseDialect dialect) => dialect == DatabaseDialect.SqlServer
        ? new DbLens.SqlServer.SqlServerInspector(Connection, Microsoft.Extensions.Logging.Abstractions.NullLogger<DbLens.SqlServer.SqlServerInspector>.Instance)
        : new DbLens.Postgres.PostgresInspector("Host=localhost;Port=1;Database=x;Username=u;Password=p", Microsoft.Extensions.Logging.Abstractions.NullLogger<DbLens.Postgres.PostgresInspector>.Instance);

    // Analyze has no values to run with, on either engine; SQL Server refuses an estimated plan too, a genuine difference:
    // SHOWPLAN_XML needs every variable declared, and a guessed type would plan another query. Its advice is not "analyze false".
    [Theory]
    [InlineData(DatabaseDialect.PostgreSql, "SELECT id FROM users WHERE id = $1", true, "cannot be executed*analyze false")]
    [InlineData(DatabaseDialect.SqlServer, "SELECT id FROM users WHERE id = @p0", true, "cannot be executed*replace them with values of the right type")]
    [InlineData(DatabaseDialect.SqlServer, "SELECT id FROM users WHERE id = @p0", false, "parameters (@p0)*types declared")]
    public async Task AStatementWithParameters_IsRefusedWhereNoPlanOrRunIsPossible(DatabaseDialect dialect, string sql, bool analyze, string message)
    {
        var call = () => Inspector(dialect).ExplainQueryAsync(new ExplainQueryRequest(sql, Analyze: analyze));

        (await call.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().MatchEquivalentOf($"*{message}*");
    }

    [Fact]
    public async Task SqlServer_AnalyzeWithParameters_DoesNotPointToAnEstimatedPlan()
    {
        var call = () => Inspector(DatabaseDialect.SqlServer).ExplainQueryAsync(new ExplainQueryRequest("SELECT 1 WHERE 1 = @p0", Analyze: true));

        (await call.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().NotContain("analyze false");
    }
}

public sealed class PostgresParameterizedExplainTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string QueryWithParameter = "SELECT id FROM public.users WHERE id = $1";

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

        // The pool hands back the connection the last call used.
        await using var connection = new Npgsql.NpgsqlConnection(TestConnections.PostgreSql);
        await connection.OpenAsync();
        await using var count = new Npgsql.NpgsqlCommand("SELECT count(*) FROM pg_prepared_statements WHERE name = 'dblens_generic_plan'", connection);
        ((long)(await count.ExecuteScalarAsync())!).Should().Be(0);
    }
}
