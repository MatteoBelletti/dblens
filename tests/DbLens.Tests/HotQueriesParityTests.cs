using DbLens.Core.Analysis;
using DbLens.Core.Analysis.HotQueries;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>The same workload gives the same picture on both engines; their sources differ by design.</summary>
[Collection(HotDatabases.Name)]
public sealed class HotQueriesParityTests(PostgresHotFixture pg, SqlServerHotFixture ss)
{
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task BothEngines_FindTheSameProblemsInTheSameWorkload()
    {
        Skip.If(!pg.IsReachable || !ss.IsReachable, TestSkip.Reason);

        var request = new FindHotQueriesRequest(VerifyJoins: true);
        var onPostgres = await pg.Inspector!.FindHotQueriesAsync(request);
        var onSqlServer = await ss.Inspector!.FindHotQueriesAsync(request);

        // Query ids and page counts are the engine's own: the rule and the table are what must agree.
        static IEnumerable<string> Shape(FindHotQueriesResult r) =>
            r.Findings.Where(f => f.RuleId is HotQueryRuleIds.JoinFanOut or HotQueryRuleIds.ReadsPerRow)
                .Select(f => $"{f.RuleId} {f.Table}");
        static string FanOut(FindHotQueriesResult r) => r.Findings.Single(f => f.RuleId == HotQueryRuleIds.JoinFanOut).Title;

        Shape(onSqlServer).Should().BeEquivalentTo(Shape(onPostgres));
        FanOut(onSqlServer).Should().Be(FanOut(onPostgres), "the measurement is of the data, the same on both");
        // A genuine difference: each engine has its own statistics source.
        onPostgres.Availability.Source.Should().Be("pg_stat_statements");
        onSqlServer.Availability.Source.Should().Be("Query Store");
    }
}
