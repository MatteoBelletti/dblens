using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Denormalization;
using DbLens.Core.Analysis.EfCore;
using DbLens.Core.Analysis.Glossary;
using DbLens.Core.Analysis.Indexes;
using DbLens.Core.Analysis.Migration;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Analysis.Receipts;
using DbLens.Core.Analysis.Relationships;
using DbLens.Core.Analysis.Verification;
using DbLens.EfCore;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// One tool's output fed to another. Each tool is tested alone elsewhere; these catch the seams —
/// SQL one tool proposes that another cannot read, or two tools disagreeing about the same fact.
/// </summary>
public abstract class CompositionTestsBase(IDatabaseFixture fixture)
{
    protected IDatabaseFixture Fixture { get; } = fixture;

    private bool ShouldSkip => !Fixture.IsReachable;

    private Core.Abstractions.IDatabaseInspector Inspector => Fixture.Inspector!;

    private async Task<CheckMigrationSafetyResult> CheckAsync(string ddl) =>
        await Inspector.CheckMigrationSafetyAsync(new CheckMigrationSafetyRequest(ddl));

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ConstraintProposedForAVerifiedReference_IsUnderstoodByTheMigrationChecker()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var relationships = await Inspector.InferRelationshipsAsync(new InferRelationshipsRequest(
            Schema: Fixture.UserSchema, VerifyCandidates: true, MaxVerifiableTableRows: long.MaxValue));
        var ddl = relationships.Findings.Where(f => f.SqlFix is not null).Select(f => f.SqlFix!).ToList();

        ddl.Should().NotBeEmpty().And.OnlyContain(p => p.Kind == SqlProposalKind.Ddl);
        foreach (var proposal in ddl)
        {
            var check = await CheckAsync(proposal.Sql);
            check.Statements.Should().NotContain(s => s.Kind == MigrationOperationKind.Unknown, proposal.Sql);
            check.Summary.TablesResolved.Should().BeGreaterThan(0);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task IndexProposedForAnUncoveredForeignKey_IsUnderstoodByTheMigrationChecker()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var indexes = await Inspector.AnalyzeIndexesAsync(new AnalyzeIndexesRequest(Fixture.UserSchema, IncludeUnusedIndexes: false));
        var proposals = indexes.Findings.Where(f => f.RuleId == IndexRuleIds.FkWithoutIndex).Select(f => f.SqlFix!.Sql).ToList();

        proposals.Should().NotBeEmpty();
        foreach (var sql in proposals)
        {
            var check = await CheckAsync(sql);
            check.Statements.Should().ContainSingle().Which.Kind.Should().Be(MigrationOperationKind.CreateIndex, sql);
            check.Findings.Concat(check.Signals).Should().NotContain(
                f => f.RuleId == MigrationRuleIds.IndexNotConcurrent,
                "dblens must not propose the blocking index build its own migration checker warns about");
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task PendingEfMigrationsOfTheDemo_ScriptedAsCompareEfModelSays_AreCheckedWithTheirDrop()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var provider = Fixture.UserSchema == "public" ? "PostgreSql" : "SqlServer";
        var model = SnapshotReader.Read(EfScripts.Folder(provider));
        var migrations = (await Inspector.CompareEfModelAsync(new CompareEfModelRequest(model))).Migrations;
        migrations.HistoryFound.Should().BeTrue("db/*/04-shop.sql creates the demo's shop schema: recreate older volumes");

        migrations.ScriptCommands.Should().ContainSingle().Which.Should().StartWith(
            $"dotnet ef migrations script {model.MigrationIds[0]} {model.MigrationIds[^1]} ");
        // What that command writes: the scripts of the pending migrations, in order.
        var check = await CheckAsync(string.Join("\n", migrations.Pending.Select(id => EfScripts.Read(provider, id[15..]))));

        check.Statements.Where(s => s.Kind == MigrationOperationKind.Unknown).Select(s => s.RawSql).Should().BeEmpty();
        check.Findings.Should().Contain(f => f.RuleId == MigrationRuleIds.Destructive && f.Table == "shop.users"
            && f.Columns!.SequenceEqual(new[] { "date_of_birth" }));
        // shop.payments is created by the script; the demo's own payments table is another one.
        check.Findings.Should().NotContain(f => f.Table == $"{Fixture.UserSchema}.payments");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task VerificationProbe_PassesExplainQuery_AndReceiptReproducesTheMeasurement()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var analysis = await Inspector.AnalyzeDenormalizationAsync(new AnalyzeDenormalizationRequest(
            Fixture.UserSchema, VerifySignals: true, MaxVerifiableTableRows: long.MaxValue));
        var measured = analysis.Findings.First(f =>
            f.Evidence == Evidence.Measured && f.SqlFix?.Kind == SqlProposalKind.ReadOnlyProbe);
        var probe = measured.SqlFix!.Sql;
        var outcome = (VerificationOutcome)measured.Metadata!["verification"];

        StatementGuard.IsReadOnlySelect(probe, Inspector.Dialect).Allowed.Should().BeTrue();
        (await Inspector.ExplainQueryAsync(new ExplainQueryRequest(probe, Analyze: true))).Should().NotBeNull();

        var receipt = await Inspector.QueryWithReceiptAsync(new QueryWithReceiptRequest(probe));
        var row = receipt.Result.Rows.Single();
        Convert.ToInt64(row[0]).Should().Be(outcome.RowsCompared);
        Convert.ToInt64(row[1]).Should().Be(outcome.RowsDiverging);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Glossary_ListsEveryJoinPathTheRelationshipMapHas()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var map = await Inspector.InferRelationshipsAsync(new InferRelationshipsRequest(Schema: Fixture.UserSchema));
        var glossary = await Inspector.BuildGlossaryAsync(new GlossaryRequest(Schema: Fixture.UserSchema));

        glossary.Summary.JoinPaths.Should().Be(map.Relationships.Count);
        foreach (var e in map.Relationships)
            glossary.Markdown.Should().Contain($"`{e.FromTable}({string.Join(", ", e.FromColumns)})` → `{e.ToTable}(");
    }
}

public sealed class PostgresCompositionTests(PostgresFixture fixture)
    : CompositionTestsBase(fixture), IClassFixture<PostgresFixture>;

public sealed class SqlServerCompositionTests(SqlServerFixture fixture)
    : CompositionTestsBase(fixture), IClassFixture<SqlServerFixture>;
