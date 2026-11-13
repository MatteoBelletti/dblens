using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Relationships;
using DbLens.Core.Analysis.Verification;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for infer_relationships, run against every dialect. Expected counts come from
/// the shipments fixture in db/*/02-seed.sql and are exact.
/// Skipped automatically when the database is not reachable.
/// </summary>
public abstract class RelationshipInferenceTestsBase(IDatabaseFixture fixture)
{
    protected IDatabaseFixture Fixture { get; } = fixture;

    private bool ShouldSkip => !Fixture.IsReachable;

    private Task<InferRelationshipsResult> InferAsync(bool verify) =>
        Fixture.Inspector!.InferRelationshipsAsync(new InferRelationshipsRequest(
            Schema: Fixture.UserSchema, VerifyCandidates: verify, MaxVerifiableTableRows: long.MaxValue));

    private string Q(string table) => $"{Fixture.UserSchema}.{table}";

    private static VerificationOutcome Outcome(Finding f) => (VerificationOutcome)f.Metadata!["verification"];

    private static Finding ForColumn(InferRelationshipsResult r, string column) =>
        r.Findings.Concat(r.Signals).Single(f => (f.Columns ?? []).Contains(column));

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Unverified_SuggestsExactlyTheThreeUndeclaredReferences()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await InferAsync(verify: false);

        result.Findings.Should().BeEmpty();
        result.Verification.Should().BeNull();
        result.Signals.Select(s => s.Columns!.Single()).Should()
            .BeEquivalentTo(["order_id", "user_id", "product_id"],
                "warehouse_id has no plausible parent and must not be guessed at");
        result.Signals.Should().OnlyContain(s => s.Table == Q("shipments"));
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Map_ContainsTheDeclaredKeys()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await InferAsync(verify: false);

        result.Relationships.Should().Contain(e =>
            e.Evidence == RelationshipEvidence.Declared && e.FromTable == Q("orders") && e.ToTable == Q("users"));
        result.Summary.DeclaredRelationships.Should().BeGreaterThan(5);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task CleanReference_IsVerifiedWithZeroOrphans()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await InferAsync(verify: true);
        var outcome = Outcome(ForColumn(result, "order_id"));

        outcome.Status.Should().Be("Confirmed");
        outcome.RowsCompared.Should().Be(6);
        outcome.RowsDiverging.Should().Be(0);
        result.Relationships.Should().Contain(e =>
            e.FromTable == Q("shipments") && e.ToTable == Q("orders") && e.Evidence == RelationshipEvidence.Verified);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task DirtyReference_IsVerifiedWithOneOrphan()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await InferAsync(verify: true);
        var outcome = Outcome(ForColumn(result, "user_id"));

        outcome.RowsCompared.Should().Be(6);
        outcome.RowsDiverging.Should().Be(1);
        result.Relationships.Should().Contain(e =>
            e.FromTable == Q("shipments") && e.ToTable == Q("users")
            && e.Evidence == RelationshipEvidence.VerifiedWithOrphans && e.OrphanRows == 1);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ContradictedReference_IsDroppedFromTheMap()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await InferAsync(verify: true);
        var outcome = Outcome(ForColumn(result, "product_id"));

        outcome.RowsCompared.Should().Be(6);
        outcome.RowsDiverging.Should().Be(5);
        result.Relationships.Should().NotContain(e => e.FromTable == Q("shipments") && e.ToTable == Q("products"));
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Verified_EveryMeasuredEntryIsAFinding()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await InferAsync(verify: true);

        result.Findings.Should().HaveCount(3);
        result.Findings.Should().OnlyContain(f => f.Evidence == Evidence.Measured);
        result.Signals.Should().BeEmpty();
        result.Verification!.Failed.Should().Be(0, "every generated probe must run on this engine");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task DemoGood_HasOnlyDeclaredRelationships()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Fixture.Inspector!.InferRelationshipsAsync(
            new InferRelationshipsRequest(Schema: "demo_good", VerifyCandidates: true));

        result.Signals.Should().BeEmpty();
        result.Findings.Should().BeEmpty();
        result.Relationships.Should().OnlyContain(e => e.Evidence == RelationshipEvidence.Declared);
    }
}

public sealed class PostgresRelationshipInferenceTests(PostgresFixture fixture)
    : RelationshipInferenceTestsBase(fixture), IClassFixture<PostgresFixture>;

public sealed class SqlServerRelationshipInferenceTests(SqlServerFixture fixture)
    : RelationshipInferenceTestsBase(fixture), IClassFixture<SqlServerFixture>;
