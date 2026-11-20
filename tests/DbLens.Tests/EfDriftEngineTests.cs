using DbLens.Core.Analysis;
using DbLens.Core.Analysis.EfCore;
using DbLens.Core.Models;
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
    public void AnIdWithAccents_IsAPlainWord()
    {
        Analyze(Model(["A", "20260201_AggiuntaCittà"]), ["A"]).Migrations.ScriptCommands
            .Should().Equal("dotnet ef migrations script A 20260201_AggiuntaCittà --context ShopContext -o pending.sql");
    }

    [Fact]
    public void AnIdNotShapedLikeEfs_GoesInNoCommand()
    {
        var result = Analyze(Model(["A", "B; curl x | sh"]), ["A"]);

        result.Migrations.ScriptCommands.Should().BeEmpty();
        result.Findings.Single(f => f.RuleId == EfRuleIds.MigrationPending).Description
            .Should().Contain("`dotnet ef migrations script`").And.NotContain("curl");
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

    [Theory]
    [InlineData("int4", "integer", false)]
    [InlineData("datetime2", "datetime2(7)", false)]
    [InlineData("character varying(100)", "varchar(100)", false)]
    [InlineData("nvarchar(100)", "nvarchar(60)", true)]
    [InlineData("numeric(10,2)", "decimal(12,2)", true)]
    [InlineData("rowversion", "timestamp", false)]
    [InlineData("timestamptz(3)", "timestamp(3) with time zone", false)]
    [InlineData("time", "time without time zone", false)]
    [InlineData("timestamp(3)", "timestamp(6) without time zone", true)]
    [InlineData("serial", "integer", false)]
    [InlineData("int4[]", "integer[]", false)]
    public void StoreTypes_DifferOnlyInWhatTheyHold_NotInHowTheyAreSpelt(string model, string database, bool drift)
    {
        var entity = new EfEntity("Order", "public", "orders",
            [new EfProperty("Id", "long", "id", null, false), new EfProperty("Value", "string", "value", model, false)], ["Id"], [], []);
        var efModel = new EfModel("ShopContext", null, "public", [entity], [], []);
        var table = Table("orders", [Col("id", "bigint"), Col("value", database)]);

        Analyze(efModel, [], EfModelMatcher.Match(efModel, [table], "public")).Findings.Any(f => f.RuleId == EfRuleIds.ColumnType)
            .Should().Be(drift);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AColumnTheModelComputes_TakesItsTypeAndNullabilityFromItsExpression(bool modelComputes, bool drift)
    {
        var entity = new EfEntity("Order", "public", "orders",
            [new EfProperty("Id", "long", "id", null, false),
             new EfProperty("Total", "decimal", "total", "decimal(18,2)", false, IsComputed: modelComputes)],
            ["Id"], [], []);
        var efModel = new EfModel("ShopContext", null, "public", [entity], [], []);
        var table = Table("orders", [Col("id", "bigint", nullable: false), Col("total", "decimal(37,4)") with { IsGenerated = true }]);

        Analyze(efModel, [], EfModelMatcher.Match(efModel, [table], "public")).Findings
            .Any(f => f.RuleId is EfRuleIds.ColumnType or EfRuleIds.ColumnNullability).Should().Be(drift,
                "a column computed by hand while the model writes it is drift");
    }

    [Fact]
    public void HasComputedColumnSql_IsRead()
    {
        var model = Parse("""
            modelBuilder.Entity("Shop.Order", b =>
            {
                b.Property<long>("Id").HasColumnType("bigint");
                b.Property<decimal>("Total").HasColumnType("numeric(18,2)").HasComputedColumnSql("qty * price", stored: true);
                b.HasKey("Id");
                b.ToTable("orders");
            });
            """);

        model.Entities.Single().Properties.Single(p => p.Name == "Total").IsComputed.Should().BeTrue();
    }

    [Fact]
    public void AStringKey_IsRequired_ThoughTheSnapshotDoesNotSaySo()
    {
        var entity = new EfEntity("User", "public", "users", [new EfProperty("Id", "string", "id", "text", true)], ["Id"], [], []);
        var efModel = new EfModel("ShopContext", null, "public", [entity], [], []);
        var table = Table("users", [Col("id", "text", nullable: false)]);

        Analyze(efModel, [], EfModelMatcher.Match(efModel, [table], "public")).Findings
            .Should().NotContain(f => f.RuleId == EfRuleIds.ColumnNullability);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void IndexUniqueness_IsSatisfiedByAnyIndexOnTheColumns(bool alsoUnique, bool drift)
    {
        var entity = new EfEntity("User", "public", "users",
            [new EfProperty("Id", "long", "id", null, false), new EfProperty("Email", "string", "email", null, false)],
            ["Id"], [new EfIndex(["Email"], null, IsUnique: true, null)], []);
        var efModel = new EfModel("ShopContext", null, "public", [entity], [], []);
        IndexDetail Index(string name, bool unique) => new(name, ["email"], unique, false, "btree", "", false, null);
        var table = Table("users", [Col("id", "bigint", nullable: false), Col("email", nullable: false)]) with
        {
            Indexes = [Index("ix_email_dup", false), .. alsoUnique ? [Index("IX_users_email", true)] : Array.Empty<IndexDetail>()],
        };

        Analyze(efModel, [], EfModelMatcher.Match(efModel, [table], "public")).Findings.Any(f => f.RuleId == EfRuleIds.IndexUniqueness)
            .Should().Be(drift);
    }

    [Fact]
    public void AUniqueConstraint_TheModelDoesNotKnow_IsReported()
    {
        var entity = new EfEntity("User", "public", "users",
            [new EfProperty("Id", "long", "id", null, false), new EfProperty("Email", "string", "email", null, false)],
            ["Id"], [new EfIndex(["Email"], null, IsUnique: false, null)], []);
        var efModel = new EfModel("ShopContext", null, "public", [entity], [], []);
        var table = Table("users", [Col("id", "bigint", nullable: false), Col("email", nullable: false)]) with
        {
            UniqueConstraints = [new UniqueConstraintDetail("uq_email", ["email"])],
        };

        Analyze(efModel, [], EfModelMatcher.Match(efModel, [table], "public")).Findings
            .Should().ContainSingle(f => f.RuleId == EfRuleIds.IndexUniqueness).Which.Severity.Should().Be(Severity.Low);
    }

    [Fact]
    public void ATableTheModelCreates_IsNotUnmapped()
    {
        var model = Model([]) with { OwnTables = [(null, "employees_history")] };
        var match = EfModelMatcher.Match(model, [Table("employees_history", [Col("id", "bigint")]), Table("legacy", [Col("id", "bigint")])], "public");

        Analyze(model, [], match).Findings.Where(f => f.RuleId == EfRuleIds.UnmappedTable).Select(f => f.Table)
            .Should().Equal("public.legacy");
    }

    [Fact]
    public void AnAlternateKey_IsMatchedBySqlServersUniqueConstraint()
    {
        var entity = new EfEntity("Order", "public", "orders",
            [new EfProperty("Id", "long", "id", "bigint", false), new EfProperty("Code", "string", "code", "text", false)],
            ["Id"], [new EfIndex(["Code"], null, IsUnique: true, null)], []);
        var model = new EfModel("ShopContext", null, "public", [entity], [], []);
        var table = Table("orders", [Col("id", "bigint"), Col("code")]) with
        {
            UniqueConstraints = [new UniqueConstraintDetail("AK_orders_code", ["code"])],
        };

        Analyze(model, [], EfModelMatcher.Match(model, [table], "public")).Findings
            .Should().NotContain(f => f.RuleId == EfRuleIds.IndexMissing);
    }

    [Fact]
    public void AHistoryTableNotRead_IsNotCalledMissing()
    {
        const string why = "Several __EFMigrationsHistory tables exist (app, audit): pass historySchema.";

        var result = EfDriftEngine.Analyze(Model(["A"]), new EfModelMatch([], [], []), null, null, new HashSet<string> { "public" }, why);

        result.Caveats.Should().Contain(why).And.NotContain(c => c.Contains("No migrations history table was found"));
        result.Findings.Single().Description.Should().Contain("was not read");
    }

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
        model.Unsupported.Select(u => u.Reason).Should().Contain(u => u.Contains("N.Derived") && u.Contains("N.Base"));
    }
}
