using DbLens.Core.Analysis;
using DbLens.Core.Analysis.EfCore;
using DbLens.EfCore;
using FluentAssertions;
using static DbLens.Tests.TestSchema;

namespace DbLens.Tests;

public sealed class EfDriftEngineTests
{
    private static readonly (string, string) History = ("public", "__EFMigrationsHistory");

    private static EfModel Model(string[] migrations, Dictionary<string, IReadOnlyList<string>>? others = null) =>
        new("ShopContext", "10.0.0", "public", [], migrations, [], others);

    private static CompareEfModelResult Analyze(EfModel model, string[] applied, EfModelMatch? match = null) =>
        EfDriftEngine.Analyze(model, match ?? new EfModelMatch([], [], []), applied, History, new HashSet<string> { "public" });

    // ── Migrations ─────────────────────────────────────────────────────────────

    [Fact]
    public void PendingAtTheEnd_IsOneScriptFromTheLastApplied()
    {
        var result = Analyze(Model(["A", "B", "C"]), ["A"]);

        result.Migrations.Pending.Should().Equal("B", "C");
        result.Migrations.OutOfOrder.Should().BeEmpty();
        result.Migrations.ScriptCommands.Should().Equal("dotnet ef migrations script A C --context ShopContext -o pending.sql");
        result.Findings.Single(f => f.RuleId == EfRuleIds.MigrationPending).Severity.Should().Be(Severity.Medium);
    }

    [Fact]
    public void APendingMigrationOlderThanTheLastApplied_IsFlagged_AndScriptedOnItsOwn()
    {
        // B came in with a merge after C was applied: a script from C would leave it out.
        var result = Analyze(Model(["A", "B", "C", "D"]), ["A", "C"]);

        result.Migrations.LastApplied.Should().Be("C");
        result.Migrations.OutOfOrder.Should().Equal("B");
        result.Migrations.ScriptCommands.Should().Equal(
            "dotnet ef migrations script A B --context ShopContext -o pending-1.sql",
            "dotnet ef migrations script C D --context ShopContext -o pending-2.sql");

        var finding = result.Findings.Single(f => f.RuleId == EfRuleIds.MigrationPending);
        finding.Severity.Should().Be(Severity.High);
        finding.Description.Should().Contain("B is older than the last applied migration, C");
    }

    [Fact]
    public void NothingApplied_ScriptsFromTheStart()
    {
        Analyze(Model(["A", "B"]), []).Migrations.ScriptCommands
            .Should().Equal("dotnet ef migrations script 0 B --context ShopContext -o pending.sql");
    }

    [Fact]
    public void AnotherContextsMigrations_InASharedHistoryTable_AreNotUnknown()
    {
        var model = Model(["A"], new() { ["IdentityContext"] = ["I1", "I2"] });

        var result = Analyze(model, ["A", "I1", "I2", "X"]);

        result.Migrations.Unknown.Should().Equal("X");
        result.Migrations.OtherContexts.Should().Equal("I1", "I2");
        result.Findings.Where(f => f.RuleId == EfRuleIds.MigrationUnknown).Should().ContainSingle();
        result.Caveats.Should().Contain(c => c.Contains("2 applied migration(s) belong to other DbContexts") && c.Contains("IdentityContext"));
    }

    // ── Names ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ATableMatchingSeveralByCase_IsNeitherMissingNorUnmapped()
    {
        var entity = new EfEntity("Order", "public", "ORDERS", [new EfProperty("Id", "long", "id", "bigint", false)], ["Id"], [], []);
        var model = new EfModel("ShopContext", null, "public", [entity], [], []);
        var tables = new[] { Table("orders", [Col("id", "bigint")]), Table("Orders", [Col("id", "bigint")]) };

        var result = EfDriftEngine.Analyze(model, EfModelMatcher.Match(model, tables, "public"), [], History, new HashSet<string> { "public" });

        result.Findings.Should().ContainSingle().Which.RuleId.Should().Be(EfRuleIds.DriftSummary);
        result.Caveats.Should().Contain(c => c.Contains("differ only in case") && c.Contains("nothing is reported missing"));
    }

    // ── Reader ─────────────────────────────────────────────────────────────────

    private static EfModel Parse(string body) => SnapshotReader.Parse(
        $$"""
        [DbContext(typeof(TestContext))]
        partial class TestContextModelSnapshot : ModelSnapshot
        {
            protected override void BuildModel(ModelBuilder modelBuilder) { {{body}} }
        }
        """, []);

    [Fact]
    public void TwoComplexPropertiesOfOneType_KeepTheirOwnColumns()
    {
        var model = Parse("""
            modelBuilder.Entity("N.Order", b =>
            {
                b.Property<int>("Id"); b.HasKey("Id"); b.ToTable("orders");
                b.ComplexProperty<Dictionary<string, object>>("Billing", "N.Order.Billing#Address", b1 => { b1.Property<string>("Street").IsRequired(); });
                b.ComplexProperty<Dictionary<string, object>>("Shipping", "N.Order.Shipping#Address", b1 => { b1.Property<string>("Street").IsRequired().HasColumnName("ship_street"); });
            });
            """);

        model.Entities.Single().Properties.Select(p => (p.Name, p.Column)).Should().Equal(
            ("Id", "Id"), ("Billing.Street", "Billing_Street"), ("Shipping.Street", "ship_street"));
    }

    [Fact]
    public void ASubtypeWhoseRootIsNotCompared_IsNamed_NotOrphaned()
    {
        var model = Parse("""
            modelBuilder.Entity("N.Base", b => { b.Property<int>("Id"); b.ToTable((string)null); b.ToView("base_view"); });
            modelBuilder.Entity("N.Derived", b => { b.HasBaseType("N.Base"); b.Property<string>("Extra"); });
            """);

        model.Entities.Should().BeEmpty();
        model.Unsupported.Should().Contain(u => u.Contains("N.Derived") && u.Contains("N.Base"));
    }
}
