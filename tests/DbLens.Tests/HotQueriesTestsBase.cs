using System.Text.Json;
using DbLens.Core.Abstractions;
using DbLens.Core.Analysis;
using DbLens.Core.Analysis.HotQueries;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// A database with a known workload, run before the tests read it: a parent joined to two 1:N
/// children, a selective filter through a function on a 200,000-row table, and a query whose
/// literals and comment hold personal data that must never come back.
/// </summary>
public interface IHotFixture
{
    bool IsReachable { get; }

    IDatabaseInspector? Inspector { get; }

    string ExpectedSource { get; }
}

public abstract class HotQueriesTestsBase(IHotFixture fixture)
{
    /// <summary>Words of the workload's literals and comment.</summary>
    public static readonly string[] Secrets = ["mario.rossi", "Mario Rossi", "4242.42"];

    protected IHotFixture Fixture { get; } = fixture;

    protected bool ShouldSkip => !Fixture.IsReachable;

    protected Task<FindHotQueriesResult> Find(FindHotQueriesRequest? request = null) =>
        Fixture.Inspector!.FindHotQueriesAsync(request ?? new FindHotQueriesRequest());

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ListsTheWorkload_HeaviestFirst()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Find();

        result.Availability.Source.Should().Be(Fixture.ExpectedSource);
        result.Availability.Missing.Should().BeEmpty();
        result.Queries.Select(q => q.TotalTimeMs).Should().BeInDescendingOrder();
        result.Queries.Should().Contain(q =>
            q.Tables.Contains("hot.orders") && q.Tables.Contains("hot.order_items") && q.Tables.Contains("hot.payments"));
        result.Queries.Should().OnlyContain(q => q.Tables.Count > 0, "only queries naming a user table are the workload");
        result.Queries.Should().NotContain(q => q.Text.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase));
        result.Summary.ByRule.Keys.Should().BeEquivalentTo(HotQueryEngine.AllRuleIds);
        result.Findings.Should().ContainSingle(f => f.RuleId == HotQueryRuleIds.Summary);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task NoLiteralNorCommentOfTheWorkload_LeavesTheTool()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Find();
        var json = JsonSerializer.Serialize(result);

        foreach (var secret in Secrets)
            json.Should().NotContainEquivalentOf(secret);

        // The query that held them is listed, its values replaced: "?" here, or PostgreSQL's own "$n".
        result.Queries.Should().ContainSingle(q => q.Text.Contains("o.note ="))
            .Which.Text.Should().MatchRegex(@"o\.note = (\?|\$\d+) AND p\.amount > (\?|\$\d+)");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ASelectiveQueryReadingTheWholeTable_IsMeasured()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Find();

        result.Findings.Should().Contain(f => f.RuleId == HotQueryRuleIds.ReadsPerRow && f.Table == "hot.events")
            .Which.Evidence.Should().Be(Evidence.Measured);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ThePlanOfTheSelectiveQuery_ShowsTheFullScan()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Find();
        var selective = result.Queries.Single(q => q.Text.Contains("lower(e.kind)", StringComparison.OrdinalIgnoreCase));

        result.Summary.PlansRead.Should().BeGreaterThan(0);
        // A filter through LOWER() cannot use an index: the plan reads the whole table and filters after.
        result.Findings.Should().Contain(f =>
            f.RuleId == QueryPlanRuleIds.ScanWithFilter && f.Table.EndsWith("events")
            && (string)f.Metadata!["queryId"] == selective.QueryId);
    }
}
