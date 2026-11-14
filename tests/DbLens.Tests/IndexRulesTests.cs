using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Indexes;
using DbLens.Core.Models;
using FluentAssertions;
using static DbLens.Tests.TestSchema;

namespace DbLens.Tests;

/// <summary>
/// The index rules propose DROP INDEX and CREATE INDEX, so each must only fire when one index
/// really does the other's work: same access method, all rows, every column carried.
/// </summary>
public sealed class IndexRulesTests
{
    private static IndexDetail Index(
        string name, string[] columns, bool unique = false, string method = "btree",
        string? predicate = null, string[]? included = null, string[]? descending = null) =>
        new(name, columns, unique, IsPrimary: false, method, Definition: "", IsPartial: predicate is not null,
            predicate, included, descending);

    private static AnalyzeIndexesResult Analyze(
        IndexDetail[] indexes, ForeignKeyDetail[]? fks = null, UniqueConstraintDetail[]? uniques = null)
    {
        var table = Table("orders", [Col("id", "int"), Col("user_id", "int"), Col("status"), Col("total", "numeric")],
            outgoing: fks) with
        {
            Indexes = indexes,
            UniqueConstraints = uniques ?? [],
        };

        return IndexAnalysisEngine.Analyze([table], null, new AnalyzeIndexesRequest(), DatabaseDialect.PostgreSql);
    }

    private static readonly ForeignKeyDetail[] UserFk = [Fk("public", "orders", "user_id", "public", "users", "id")];

    private static IEnumerable<string> Dropped(AnalyzeIndexesResult result, string ruleId) =>
        result.Findings.Where(f => f.RuleId == ruleId).Select(f => f.SqlFix!.Sql);

    // ── fk-without-index ───────────────────────────────────────────────────────

    [Fact]
    public void ForeignKey_IsCoveredByAnIndexThatLeadsWithIt()
    {
        Analyze([Index("ix", ["user_id", "status"])], UserFk)
            .Findings.Should().NotContain(f => f.RuleId == IndexRuleIds.FkWithoutIndex);
    }

    [Fact]
    public void ForeignKey_IsNotCoveredByAPartialIndex()
    {
        // The index holds only the open orders: a delete on users must still scan the rest.
        Analyze([Index("ix_open", ["user_id"], predicate: "status = 'open'")], UserFk)
            .Findings.Should().ContainSingle(f => f.RuleId == IndexRuleIds.FkWithoutIndex);
    }

    [Fact]
    public void ForeignKey_IsCoveredByAHashIndexOnExactlyItsColumns_ButNotByALongerOne()
    {
        Analyze([Index("ix_hash", ["user_id"], method: "hash")], UserFk)
            .Findings.Should().NotContain(f => f.RuleId == IndexRuleIds.FkWithoutIndex);

        Analyze([Index("ix_hash", ["user_id", "status"], method: "hash")], UserFk)
            .Findings.Should().ContainSingle(f => f.RuleId == IndexRuleIds.FkWithoutIndex);
    }

    [Fact]
    public void ForeignKey_IsNotCoveredByAGinIndex()
    {
        Analyze([Index("ix_gin", ["user_id"], method: "gin")], UserFk)
            .Findings.Should().ContainSingle(f => f.RuleId == IndexRuleIds.FkWithoutIndex);
    }

    [Fact]
    public void ForeignKey_IsCoveredByAUniqueConstraint()
    {
        // SQL Server reports a constraint's index under the constraint, not under Indexes.
        Analyze([], UserFk, [new UniqueConstraintDetail("uq", ["user_id", "status"])])
            .Findings.Should().NotContain(f => f.RuleId == IndexRuleIds.FkWithoutIndex);
    }

    // ── duplicate-index ────────────────────────────────────────────────────────

    [Fact]
    public void Duplicate_NeedsTheSameAccessMethod()
    {
        var result = Analyze([Index("ix_btree", ["status"]), Index("ix_hash", ["status"], method: "hash")]);

        result.Findings.Should().BeEmpty("a hash index beside a b-tree is a choice, not a copy");
    }

    [Fact]
    public void Duplicate_NeedsTheSameIncludedColumns()
    {
        var result = Analyze([Index("ix_plain", ["status"]), Index("ix_incl", ["status"], included: ["total"])]);

        result.Findings.Should().NotContain(f => f.RuleId == IndexRuleIds.DuplicateIndex);
        Dropped(result, IndexRuleIds.RedundantIndex).Should().ContainSingle()
            .Which.Should().Contain("ix_plain", "the index that carries less is the one to drop");
    }

    [Fact]
    public void Duplicate_IsReportedForTwoIdenticalIndexes()
    {
        var result = Analyze([Index("ix_a", ["status"]), Index("ix_b", ["status"])]);

        Dropped(result, IndexRuleIds.DuplicateIndex).Should().ContainSingle().Which.Should().Contain("ix_b");
        result.Findings.Should().NotContain(f => f.RuleId == IndexRuleIds.RedundantIndex);
    }

    // ── key order ──────────────────────────────────────────────────────────────

    [Fact]
    public void MixedOrder_IsAnotherIndex_NotADuplicateOrAPrefix()
    {
        // (status, total DESC) answers ORDER BY status, total DESC; (status, total) cannot.
        var result = Analyze(
        [
            Index("ix_plain", ["status", "total"]),
            Index("ix_mixed", ["status", "total"], descending: ["total"]),
            Index("ix_mixed_wide", ["status", "total", "user_id"], descending: ["total"]),
        ]);

        result.Findings.Should().NotContain(f => f.RuleId == IndexRuleIds.DuplicateIndex);
        Dropped(result, IndexRuleIds.RedundantIndex).Should().ContainSingle()
            .Which.Should().Contain("ix_mixed\"", "it is a prefix of the wider index that sorts the same way");
    }

    [Fact]
    public void FullyReversedOrder_IsTheSameIndexReadBackwards()
    {
        var result = Analyze(
        [
            Index("ix_asc", ["status", "total"]),
            Index("ix_desc", ["status", "total"], descending: ["status", "total"]),
            Index("ix_one_desc", ["status"], descending: ["status"]),
        ]);

        Dropped(result, IndexRuleIds.DuplicateIndex).Should().ContainSingle().Which.Should().Contain("ix_desc");
        Dropped(result, IndexRuleIds.RedundantIndex).Should().ContainSingle().Which.Should().Contain("ix_one_desc");
    }

    // ── redundant-index ────────────────────────────────────────────────────────

    [Fact]
    public void Redundant_WhenAUniqueIndexHasTheSameKey()
    {
        var result = Analyze([Index("ix_plain", ["status"]), Index("ix_unique", ["status"], unique: true)]);

        Dropped(result, IndexRuleIds.RedundantIndex).Should().ContainSingle().Which.Should().Contain("ix_plain");
    }

    [Fact]
    public void Redundant_CoveringIndexFoldsIntoALongerKeyThatHoldsItsColumns()
    {
        var result = Analyze([Index("ix_incl", ["status"], included: ["total"]), Index("ix_wide", ["status", "total"])]);

        Dropped(result, IndexRuleIds.RedundantIndex).Should().ContainSingle().Which.Should().Contain("ix_incl");
    }

    [Fact]
    public void NotRedundant_WhenTheLongerIndexLacksAnIncludedColumn()
    {
        var result = Analyze([Index("ix_incl", ["status"], included: ["total"]), Index("ix_wide", ["status", "user_id"])]);

        result.Findings.Should().BeEmpty("dropping ix_incl would lose the only index that carries total");
    }

    [Fact]
    public void NotRedundant_AgainstAHashIndex()
    {
        var result = Analyze([Index("ix_btree", ["status"]), Index("ix_hash", ["status", "total"], method: "hash")]);

        result.Findings.Should().BeEmpty("a hash index cannot be searched by a prefix of its key");
    }
}

/// <summary>What the 200-result cap keeps must never depend on the order the rules ran in.</summary>
public sealed class FindingSplitTests
{
    private static Finding Signal(Severity severity, string table, double confidence = 0.9) =>
        new("rule", severity, FindingCategory.Compliance, confidence, Evidence.Inferred, table, null, "t", "d", null, null);

    [Fact]
    public void Truncation_KeepsTheMostSevereResults_WhereverTheyCameInTheList()
    {
        var all = Enumerable.Range(0, 300).Select(i => Signal(Severity.Low, $"low_{i}"))
            .Append(Signal(Severity.Critical, "critical_last"))
            .ToList();

        var split = FindingSplit.Partition(all);

        split.Truncated.Should().BeTrue();
        split.Total.Should().Be(DiagnosticLimits.MaxFindingCount);
        split.Signals.Should().Contain(f => f.Table == "critical_last");
        split.Omitted.Should().Be(101);
        split.TruncationCaveat().Should().Contain("101 more were left out");
    }

    [Fact]
    public void Truncation_KeepsFactsBeforeSignals_AndTheOriginalOrderAmongEquals()
    {
        var facts = Enumerable.Range(0, 150).Select(i => Signal(Severity.Low, $"fact_{i}") with { Evidence = Evidence.Derived });
        var signals = Enumerable.Range(0, 150).Select(i => Signal(Severity.Critical, $"signal_{i}"));

        var split = FindingSplit.Partition(signals.Concat(facts));

        split.Findings.Should().HaveCount(150, "a measured Low outranks an inferred Critical for a place in the result");
        split.Signals.Select(f => f.Table).Should().Equal(Enumerable.Range(0, 50).Select(i => $"signal_{i}"));
    }

    [Fact]
    public void NoTruncation_LeavesTheOrderAlone()
    {
        var all = new[] { Signal(Severity.Low, "a"), Signal(Severity.Critical, "b") };

        var split = FindingSplit.Partition(all);

        split.Truncated.Should().BeFalse();
        split.Omitted.Should().Be(0);
        split.Signals.Select(f => f.Table).Should().Equal("a", "b");
    }
}
