using DbLens.Core.Abstractions;
using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Cascade;
using DbLens.Core.Analysis.Fragmentation;
using DbLens.Core.Analysis.Gdpr;
using DbLens.Core.Analysis.Glossary;
using DbLens.Core.Analysis.Indexes;
using DbLens.Core.Analysis.Relationships;
using DbLens.Core.Models;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// A throwaway database of catalog shapes the demo schema does not have: names that contain dots
/// or differ only in case, partial indexes, INCLUDE columns, views. Created by the fixture and
/// dropped afterwards, so nothing here can disturb the counts the demo tests assert.
/// </summary>
public interface IEdgeFixture
{
    bool IsReachable { get; }

    IDatabaseInspector? Inspector { get; }

    /// <summary>A login that can read the edge schema's tables and nothing about the server.</summary>
    IDatabaseInspector? LimitedInspector { get; }

    /// <summary>Scans edge.account's primary key, so the database has seen a workload (<see cref="IndexWorkload"/>).</summary>
    Task RunWorkloadAsync();
}

/// <summary>
/// Catalog shapes that once made a tool fail, or answer about the wrong object, on both dialects.
/// </summary>
public abstract class EdgeCatalogTestsBase(IEdgeFixture fixture)
{
    protected const string DottedSchema = "edge.dot";
    protected const string Schema = "edge";

    protected IEdgeFixture Fixture { get; } = fixture;

    protected bool ShouldSkip => !Fixture.IsReachable;

    protected IDatabaseInspector Inspector => Fixture.Inspector!;

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Diagnostics_ReadASchemaWhoseNameContainsADot()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Inspector.AnalyzeIndexesAsync(new AnalyzeIndexesRequest(DottedSchema));

        result.Caveats.Should().NotContain(c => c.Contains("could not be inspected"));
        result.Findings.Should().ContainSingle(f => f.RuleId == IndexRuleIds.FkWithoutIndex)
            .Which.Table.Should().Be($"{DottedSchema}.child");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task UnusedIndex_CountsFromTheDatabaseCreation_NotTheServerStart()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // The fixture creates this database after the server started, as a restore of a dump would.
        await Fixture.RunWorkloadAsync();
        var result = await Inspector.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema, ["covered"], IncludeUnusedIndexes: true, UnusedIndexMinDays: 0));

        var unused = result.Findings.Where(f => f.RuleId == IndexRuleIds.UnusedIndex).ToList();
        unused.Should().NotBeEmpty();
        unused.Should().OnlyContain(f =>
            (string)f.Metadata!["stats_since"] == nameof(StatsWindowStart.DatabaseCreated)
            && (double)f.Metadata["stats_window_days"] < 1);
        result.Caveats.Should().Contain(c => c.Contains("since the database was created"));
        result.Caveats.Should().NotContain(c => c.Contains("cannot see when the database"));

        var skipped = await Inspector.AnalyzeIndexesAsync(new AnalyzeIndexesRequest(Schema, ["covered"]));
        skipped.Findings.Should().NotContain(f => f.RuleId == IndexRuleIds.UnusedIndex,
            "a database created minutes ago has far fewer than the 7 days of counts the rule needs");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task TableDetails_FindTablesWhoseNamesContainDots()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Inspector.GetTableDetailsAsync(
            new TableDetailsRequest([$"{DottedSchema}.tbl.with.dot", $"{DottedSchema}.child", "tbl.with.dot"], DottedSchema));

        result.NotFound.Should().BeEmpty();
        result.Tables.Select(t => (t.Schema, t.Name)).Should().Equal(
            (DottedSchema, "tbl.with.dot"), (DottedSchema, "child"));
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Cascade_StartsFromATableWhoseNameContainsDots()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Inspector.CheckCascadePathsAsync(
            new CheckCascadePathsRequest($"{DottedSchema}.tbl.with.dot"));

        result.Paths.Should().ContainSingle().Which.LeafTables.Should().Equal($"{DottedSchema}.child");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Cascade_UnknownStartingTable_IsAnErrorNotAnEmptyAnswer()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var act = () => Inspector.CheckCascadePathsAsync(new CheckCascadePathsRequest($"{Schema}.no_such_table"));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*was not found*");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Relationships_SurviveTablesWhoseNamesDifferOnlyInCase()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var relationships = await Inspector.InferRelationshipsAsync(new InferRelationshipsRequest(Schema));
        var glossary = await Inspector.BuildGlossaryAsync(new GlossaryRequest(Schema));

        relationships.Summary.TablesAnalyzed.Should().BeGreaterThan(2);
        glossary.Markdown.Should().Contain("Twin").And.Contain("twin");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task PartialIndex_DoesNotCoverAForeignKey()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Inspector.AnalyzeIndexesAsync(new AnalyzeIndexesRequest(Schema, ["kid"]));

        result.Findings.Should().Contain(f => f.RuleId == IndexRuleIds.FkWithoutIndex && f.Table == $"{Schema}.kid");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task IncludeColumns_AreReportedApartFromTheKey_AndAreNotDuplicates()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var details = await Inspector.GetTableDetailsAsync(new TableDetailsRequest([$"{Schema}.covered"]));
        var covering = details.Tables.Single().Indexes.Single(i => i.Name == "ix_a_incl");

        covering.Columns.Should().Equal("a");
        covering.IncludedColumns.Should().Equal("b");

        var analysis = await Inspector.AnalyzeIndexesAsync(new AnalyzeIndexesRequest(Schema, ["covered"]));

        analysis.Findings.Should().NotContain(f => f.RuleId == IndexRuleIds.DuplicateIndex);
        analysis.Findings.Where(f => f.RuleId == IndexRuleIds.RedundantIndex)
            .Select(f => f.SqlFix!.Sql)
            .Should().OnlyContain(sql => !sql.Contains("ix_a_b"), "the widest index is the one the others fold into");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ScopeThatMatchesNothing_SaysSo_InsteadOfReturningACleanResult()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var noSchema = await Inspector.ScanGdprAsync(new ScanGdprRequest("no_such_schema"));
        var noTable = await Inspector.AnalyzeIndexesAsync(new AnalyzeIndexesRequest(Schema, ["kid", "no_such_table"]));

        noSchema.Signals.Should().BeEmpty();
        noSchema.Caveats.Should().ContainSingle().Which.Should().Contain("No tables were found in schema 'no_such_schema'");
        noTable.Findings.Should().NotBeEmpty("the table that does exist is still analyzed");
        noTable.Caveats.Should().Contain(c => c.Contains("No table named 'no_such_table'"));
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task TableDetails_AcceptNamesWrittenWithQuotes()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var quoted = SqlIdentifier.Qualify(DottedSchema, "tbl.with.dot", Inspector.Dialect);

        var result = await Inspector.GetTableDetailsAsync(new TableDetailsRequest([quoted]));

        result.NotFound.Should().BeEmpty();
        result.Tables.Single().Name.Should().Be("tbl.with.dot");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task TableDetails_NameTheDatabase_EvenWhenNothingIsFound()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Inspector.GetTableDetailsAsync(new TableDetailsRequest(["no_such_table"]));

        result.NotFound.Should().Equal("no_such_table");
        result.Database.Should().Be("dblens_edge");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task LoginWithoutStatisticsPermissions_LosesTheMeasurement_NotTheAnalysis()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var indexes = await Fixture.LimitedInspector!.AnalyzeIndexesAsync(new AnalyzeIndexesRequest(Schema, ["kid"]));
        var fragmentation = await Fixture.LimitedInspector!.AnalyzeFragmentationAsync(new AnalyzeFragmentationRequest(Schema, ["kid"]));

        indexes.Findings.Should().Contain(f => f.RuleId == IndexRuleIds.FkWithoutIndex);
        fragmentation.Findings.Should().BeEmpty();
        (fragmentation.StatsAvailable && indexes.Caveats.All(c => !c.Contains("could not be read")))
            .Should().BeFalse("at least one of the two statistics sources is closed to this login, and the result must say so");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task DescendingKeyColumns_AreRead_AndAMixedOrderIsNotADuplicate()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var details = await Inspector.GetTableDetailsAsync(new TableDetailsRequest([$"{Schema}.ordered"]));
        var indexes = details.Tables.Single().Indexes.ToDictionary(i => i.Name);

        indexes["ix_ordered"].DescendingColumns.Should().BeNull();
        indexes["ix_ordered_mixed"].DescendingColumns.Should().Equal("b");
        indexes["ix_ordered_mixed"].Definition.Should().Contain("DESC");
        indexes["ix_ordered_reversed"].DescendingColumns.Should().Equal("a", "b");

        var analysis = await Inspector.AnalyzeIndexesAsync(new AnalyzeIndexesRequest(Schema, ["ordered"]));

        analysis.Findings.Where(f => f.RuleId is IndexRuleIds.DuplicateIndex or IndexRuleIds.RedundantIndex)
            .Should().ContainSingle("only the fully reversed index is the plain one read backwards")
            .Which.SqlFix!.Sql.Should().Contain("ix_ordered_reversed");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task NoActionKey_BlocksOnlyWhereACascadeLeavesRowsBehind()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Inspector.CheckCascadePathsAsync(new CheckCascadePathsRequest($"{Schema}.account"));

        result.Findings.Where(f => f.RuleId == CascadeRuleIds.BreaksWithRestrict).Select(f => f.Table)
            .Should().Equal($"{Schema}.approval");
        result.Paths.Should().Contain(p => p.LeafTables.Contains($"{Schema}.membership") && p.BrokenAt == null);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Views_ReportTheirColumns()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Inspector.GetTableDetailsAsync(new TableDetailsRequest([$"{Schema}.v_kid"]));

        var view = result.Tables.Single();
        view.Kind.Should().Be("view");
        view.Columns.Select(c => c.Name).Should().Equal("id", "parent_id");
    }
}
