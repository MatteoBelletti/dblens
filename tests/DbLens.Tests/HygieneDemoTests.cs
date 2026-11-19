using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Hygiene;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>demo_good is the acceptance schema: nothing but the summary, with verification on.</summary>
public abstract class HygieneDemoTestsBase(IDatabaseFixture fixture)
{
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task DemoGood_GivesOnlyTheSummary()
    {
        Skip.If(!fixture.IsReachable, TestSkip.Reason);

        var result = await fixture.Inspector!.AnalyzeTableHygieneAsync(new AnalyzeTableHygieneRequest(Schema: "demo_good", VerifySignals: true));

        result.Findings.Concat(result.Signals).Should().ContainSingle().Which.RuleId.Should().Be(HygieneRuleIds.Summary);
    }
}

public sealed class PostgresHygieneDemoTests(PostgresFixture fixture)
    : HygieneDemoTestsBase(fixture), IClassFixture<PostgresFixture>;

public sealed class SqlServerHygieneDemoTests(SqlServerFixture fixture)
    : HygieneDemoTestsBase(fixture), IClassFixture<SqlServerFixture>;

/// <summary>The same defects give the same findings on both engines.</summary>
[Collection(HygieneDatabases.Name)]
public sealed class HygieneParityTests(PostgresHygieneFixture pg, SqlServerHygieneFixture ss)
{
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task BothEngines_FindTheSameDefects()
    {
        Skip.If(!pg.IsReachable || !ss.IsReachable, TestSkip.Reason);

        var request = new AnalyzeTableHygieneRequest(VerifySignals: true, UpdateCountersMinDays: 0);
        var onPostgres = await pg.Inspector!.AnalyzeTableHygieneAsync(request);
        var onSqlServer = await ss.Inspector!.AnalyzeTableHygieneAsync(request);

        static IEnumerable<string> Shape(AnalyzeTableHygieneResult r) =>
            r.Findings.Concat(r.Signals).Select(f => $"{f.RuleId} {f.Table} {string.Join(",", f.Columns ?? [])} {f.Evidence}");

        Shape(onSqlServer).Should().BeEquivalentTo(Shape(onPostgres));
        onSqlServer.Summary.BySeverity.Should().Equal(onPostgres.Summary.BySeverity);
    }
}

/// <summary>The tool itself: an unreadable snapshot costs the EF mapping, not the analysis.</summary>
public sealed class AnalyzeTableHygieneToolTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task AMalformedSnapshot_IsACaveat_AndTheRestRuns()
    {
        Skip.If(!fixture.IsReachable, TestSkip.Reason);

        var folder = Directory.CreateTempSubdirectory("dblens-snapshot-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder.FullName, "BrokenModelSnapshot.cs"), "class NotASnapshot { }");
            var tool = new DbLens.Mcp.Tools.AnalyzeTableHygieneTool(
                Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection()),
                new DbLens.Mcp.Infrastructure.DatabaseInspectorFactory(
                    Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
                    new DbLens.Mcp.Infrastructure.ServerPolicy(false, false, false)));

            var result = await tool.AnalyzeTableHygiene(
                schema: "demo_good", modelSnapshotPath: folder.FullName,
                connectionString: Environment.GetEnvironmentVariable("DBLENS_TEST_PG_CONNECTION")
                    ?? "Host=localhost;Port=5433;Database=dblens_demo;Username=dblens_reader;Password=readerpassword");

            result.Caveats.Should().Contain(c => c.StartsWith("The EF model was not read") && c.Contains("ModelSnapshot"));
            result.Findings.Should().ContainSingle(f => f.RuleId == HygieneRuleIds.Summary);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
