using DbLens.Core.Analysis;
using DbLens.Core.Analysis.EfCore;
using DbLens.Core.Analysis.Hygiene;
using DbLens.Core.Analysis.Verification;
using DbLens.Core.Models;
using FluentAssertions;
using static DbLens.Tests.TestSchema;

namespace DbLens.Tests;

public sealed class HygieneEngineTests
{
    private static ColumnDetail Ts(string name, bool nullable = false, string type = "timestamp with time zone") =>
        Col(name, type, nullable: nullable);

    private static HygieneEngine.Plan Analyze(
        TableDetail table, long rows = 5_000, ColumnStatistics[]? stats = null, long? updates = 10,
        bool verify = false, DatabaseDialect dialect = DatabaseDialect.PostgreSql) =>
        HygieneEngine.Analyze(
            new HygieneEngine.Input(
                [table],
                new Dictionary<(string, string), long?> { [(table.Schema, table.Name)] = rows },
                stats ?? [],
                updates is { } u ? new Dictionary<(string, string), long> { [(table.Schema, table.Name)] = u } : null,
                [],
                dialect),
            new AnalyzeTableHygieneRequest(VerifySignals: verify));

    private static IEnumerable<string> Rules(HygieneEngine.Plan plan) =>
        plan.Findings.Select(f => f.RuleId).Concat(plan.Checks.Select(c => c.Finding.RuleId));

    private static ColumnStatistics Null(string column) => new("public", "t", column, AllNull: true, SingleValue: false);

    [Fact]
    public void ColumnsInUseByConstruction_AreNeverCandidates()
    {
        var table = Table("t", [Col("id", "int"), Col("ref_id", "int", nullable: true), Col("indexed", nullable: true),
                Col("calc", nullable: true) with { IsGenerated = true }, Col("free", nullable: true), Ts("created_at"), Ts("updated_at")],
                outgoing: [Fk("public", "t", "ref_id", "public", "other", "id")]) with
            { Indexes = [new IndexDetail("ix", ["indexed"], false, false, "btree", "", false, null)] };

        var plan = Analyze(table, stats: [Null("id"), Null("ref_id"), Null("indexed"), Null("calc"), Null("free")]);

        plan.Checks.Should().ContainSingle(c => c.Finding.RuleId == HygieneRuleIds.ColumnAlwaysNull)
            .Which.Finding.Columns.Should().Equal("free");
    }

    [Fact]
    public void AuditColumns_AreNotConstants()
    {
        var table = Table("t", [Col("id", "int"), Ts("created_at"), Ts("updated_at", nullable: true)]);

        Analyze(table, stats: [new ColumnStatistics("public", "t", "updated_at", false, SingleValue: true)])
            .Checks.Should().NotContain(c => c.Finding.RuleId == HygieneRuleIds.ColumnSingleValue);
    }

    [Fact]
    public void UnknownUpdates_LeaveMissingUpdatedLow_AndCheckNoMaintenance()
    {
        var withoutUpdated = Analyze(Table("t", [Col("id", "int"), Ts("created_at")]), updates: null);
        withoutUpdated.Findings.Single(f => f.RuleId == HygieneRuleIds.MissingUpdated).Severity.Should().Be(Severity.Low);

        Analyze(Table("t", [Col("id", "int"), Ts("created_at"), Ts("updated_at")]), updates: null)
            .Checks.Should().NotContain(c => c.Finding.RuleId == HygieneRuleIds.UpdatedNotMaintained);
    }

    [Fact]
    public void AnInsertOnlyTable_NeedsNoUpdateDate()
    {
        Rules(Analyze(Table("t", [Col("id", "int"), Ts("created_at")]), updates: 0))
            .Should().NotContain(HygieneRuleIds.MissingUpdated);
        Analyze(Table("t", [Col("id", "int"), Ts("created_at")]), updates: 5)
            .Findings.Single(f => f.RuleId == HygieneRuleIds.MissingUpdated).Severity.Should().Be(Severity.Medium);
    }

    [Fact]
    public void ALookupTable_IsNotAskedForAuditDates()
    {
        Rules(Analyze(Table("t", [Col("id", "int"), Col("name")]), rows: 40))
            .Should().NotContain([HygieneRuleIds.MissingCreated, HygieneRuleIds.MissingUpdated]);
    }

    [Fact]
    public void AuditNames_AreReadInSnakeCase_AndMustBeDates()
    {
        var plan = Analyze(Table("t", [Col("id", "int"), Ts("CreatedAt"), Col("updated_at", "text")]));

        Rules(plan).Should().NotContain(HygieneRuleIds.MissingCreated, "CreatedAt is created_at")
            .And.Contain(HygieneRuleIds.MissingUpdated, "a text column is not a date");
    }

    [Fact]
    public void TwoCreationColumns_SkipTheAbandonedCheck_AndSaySo()
    {
        var plan = Analyze(Table("t", [Col("id", "int"), Ts("created_at"), Ts("inserted_at"), Col("note", nullable: true)]), verify: true);

        plan.Checks.Should().NotContain(c => c.Finding.RuleId == HygieneRuleIds.ColumnAbandoned);
        plan.Caveats.Should().ContainSingle(c => c.Contains("2 creation columns (created_at, inserted_at)"));
    }

    [Fact]
    public void AbandonedAndOutOfOrderChecks_ExistOnlyWhenVerifying()
    {
        var table = Table("t", [Col("id", "int"), Ts("created_at"), Ts("updated_at"), Col("note", nullable: true)]);

        Rules(Analyze(table)).Should().NotContain([HygieneRuleIds.ColumnAbandoned, HygieneRuleIds.UpdateBeforeCreate]);
        Analyze(table, verify: true).Checks.Where(c => !c.IsSignal).Select(c => c.Finding.RuleId)
            .Should().BeEquivalentTo([HygieneRuleIds.ColumnAbandoned, HygieneRuleIds.UpdateBeforeCreate]);
    }

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql, "timestamp without time zone", true)]
    [InlineData(DatabaseDialect.PostgreSql, "timestamp with time zone", false)]
    [InlineData(DatabaseDialect.SqlServer, "datetime", true)]
    [InlineData(DatabaseDialect.SqlServer, "datetimeoffset", false)]
    public void ATimestampWithoutTimeZone_IsReported(DatabaseDialect dialect, string type, bool reported) =>
        Rules(Analyze(Table("t", [Col("id", "int"), Ts("created_at", type: type)]), dialect: dialect))
            .Contains(HygieneRuleIds.WithoutTimeZone).Should().Be(reported);

    [Fact]
    public void ALeftBehindName_OrComment_IsASignal()
    {
        var plan = Analyze(Table("t", [Col("id", "int"), Col("notes_old"), Col("legacy_code"), Col("temperature"),
            Col("old_value"), Col("water_temp"), Col("flag") with { Comment = "DEPRECATED since v3" }]), rows: 10);

        plan.Findings.Where(f => f.RuleId == HygieneRuleIds.ColumnLegacyName).SelectMany(f => f.Columns!)
            .Should().BeEquivalentTo(["notes_old", "legacy_code", "flag"], "old_value is an audit log's, water_temp a temperature");
    }

    [Fact]
    public void Measurements_PromoteOrRefute_AndNeverInventAFinding()
    {
        var table = Table("t", [Col("id", "int"), Ts("created_at"), Ts("updated_at"), Col("fax", nullable: true)]);
        var plan = Analyze(table, stats: [Null("fax")], verify: true);
        var alwaysNull = plan.Checks.Single(c => c.Finding.RuleId == HygieneRuleIds.ColumnAlwaysNull);
        var beforeCreate = plan.Checks.Single(c => c.Finding.RuleId == HygieneRuleIds.UpdateBeforeCreate);

        HygieneEngine.Apply(alwaysNull, VerificationMeasurement.Measured(alwaysNull.Probe, 3_000, 0, 1))!
            .Title.Should().Be("public.t.fax is NULL in all 3,000 rows");
        HygieneEngine.Apply(alwaysNull, VerificationMeasurement.Measured(alwaysNull.Probe, 3_000, 2, 1))!
            .Severity.Should().Be(Severity.Info, "a refuted signal is a fact at Info");
        HygieneEngine.Apply(beforeCreate, VerificationMeasurement.Measured(beforeCreate.Probe, 3_000, 0, 1))
            .Should().BeNull("a check without a signal is reported only when it finds something");
    }

    [Fact]
    public void TooFewRows_AreInconclusive_NotRefuted()
    {
        var table = Table("t", [Col("id", "int"), Col("channel", nullable: true)]);
        var check = Analyze(table, stats: [new ColumnStatistics("public", "t", "channel", false, SingleValue: true)]).Checks.Single();

        var measured = VerificationMeasurement.Measured(check.Probe, 40, 1, 1);

        measured.Status.Should().Be(VerificationStatus.Inconclusive);
        ((VerificationOutcome)HygieneEngine.Apply(check, measured)!.Metadata!["verification"]).NotVerifiedReason
            .Should().Be("TooFewRowsCompared");
    }

    [Fact]
    public void ARowversion_IsNotAnUpdateDate()
    {
        var table = Table("t", [Col("id", "int"), Ts("created_at", type: "datetime2"), Col("last_modified", "timestamp")]);

        Rules(Analyze(table, dialect: DatabaseDialect.SqlServer)).Should().Contain(HygieneRuleIds.MissingUpdated,
            "SQL Server's timestamp is rowversion, a counter");
    }

    [Fact]
    public void ColumnsThatDifferOnlyInCase_AreTwoColumns()
    {
        var table = Table("t", [Col("id", "int"), Col("Twin", nullable: true), Col("twin", nullable: true)]);

        Analyze(table, stats: [Null("Twin"), Null("twin")]).Checks.Select(c => c.Finding.Columns!.Single())
            .Should().Equal("Twin", "twin");
    }

    [Fact]
    public void ATableTheCountersHoldNothingFor_IsNotKnownToBeInsertOnly()
    {
        var table = Table("t", [Col("id", "int"), Ts("created_at")]);
        var plan = HygieneEngine.Analyze(
            new HygieneEngine.Input([table], new Dictionary<(string, string), long?> { [("public", "t")] = 5_000 }, [],
                new Dictionary<(string, string), long>(), [], DatabaseDialect.PostgreSql),
            new AnalyzeTableHygieneRequest());

        plan.Findings.Single(f => f.RuleId == HygieneRuleIds.MissingUpdated).Severity.Should().Be(Severity.Low);
    }

    private static EfModel Model(string context, params EfProperty[] properties) =>
        new(context, "10.0.0", null, [new EfEntity("App.T", null, "t", properties, ["Id"], [], [])], [], []);

    private static EfProperty Prop(string name, string column) => new(name, "long", column, "bigint", false);

    private static IEnumerable<string?> NotInModel(EfUnsupported[] unsupported, params EfModel[] models)
    {
        var table = Table("t", [Col("id", "int"), Col("a"), Col("b"), Col("extra")]);
        var plan = HygieneEngine.Analyze(
            new HygieneEngine.Input([table], new Dictionary<(string, string), long?> { [("public", "t")] = 10 }, [], null,
                [.. models.Select(m => EfModelMatcher.Match(m, [table], "public"))], DatabaseDialect.PostgreSql, unsupported),
            new AnalyzeTableHygieneRequest());
        return plan.Findings.Where(f => f.RuleId == HygieneRuleIds.ColumnNotInModel).Select(f => f.Columns!.Single());
    }

    [Fact]
    public void AColumnNotInTheModel_IsOneNoModelMaps_InATableEveryModelReadsWhole()
    {
        var first = Model("FirstContext", Prop("Id", "id"), Prop("A", "a"));
        var second = Model("SecondContext", Prop("Id", "id"), Prop("B", "b"));

        NotInModel([], first, second).Should().Equal("extra");
        NotInModel([new EfUnsupported("App.Address, owned by App.T, is stored as JSON.", ["App.Address", "App.T"])], first, second)
            .Should().BeEmpty("a JSON column holds what no property names");
    }
}
