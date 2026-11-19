using DbLens.Core.Analysis.EfCore;
using DbLens.EfCore;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>The same model and the same drift give the same findings on both engines.</summary>
[Collection(EfDatabases.Name)]
public sealed class EfDriftParityTests(PostgresEfFixture pg, SqlServerEfFixture ss)
{
    private static EfModel Model(string provider) =>
        SnapshotReader.Read(EfScripts.Folder(provider));

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task BothEngines_ReportTheSameDrift()
    {
        Skip.If(!pg.IsReachable || !ss.IsReachable, TestSkip.Reason);

        var onPostgres = await pg.Drifted!.CompareEfModelAsync(new CompareEfModelRequest(Model("PostgreSql")));
        var onSqlServer = await ss.Drifted!.CompareEfModelAsync(new CompareEfModelRequest(Model("SqlServer")));

        static IEnumerable<string> Shape(CompareEfModelResult r) =>
            r.Findings.Where(f => f.Table.StartsWith("shop.")).Select(f => $"{f.RuleId} {f.Table} {string.Join(",", f.Columns ?? [])}");

        Shape(onSqlServer).Should().BeEquivalentTo(Shape(onPostgres));
        onSqlServer.Summary.BySeverity.Should().Equal(onPostgres.Summary.BySeverity);
        onSqlServer.Migrations.Pending.Count.Should().Be(onPostgres.Migrations.Pending.Count);
    }
}
