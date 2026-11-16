using DbLens.Core.Abstractions;
using DbLens.Core.Analysis;
using DbLens.Core.Analysis.EfCore;
using DbLens.EfCore;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Two databases built from the SQL `dotnet ef` wrote for the demo model (EF Core 10): a clean one
/// with every migration applied, and a drifted one with the first migration only and a deliberate
/// difference of each kind made by hand.
/// </summary>
public interface IEfFixture
{
    bool IsReachable { get; }

    /// <summary>Init applied, AddPayments pending, then: users.phone dropped, the order_items → products
    /// foreign key dropped, ix_orders_created_manual and shop.legacy_notes added, and a history row
    /// for a hotfix migration the code does not have.</summary>
    IDatabaseInspector? Drifted { get; }

    /// <summary>Both migrations applied, nothing else.</summary>
    IDatabaseInspector? Clean { get; }

    /// <summary>The provider's folder under Fixtures/EfSnapshots/ef10.</summary>
    string Provider { get; }
}

public abstract class EfDriftTestsBase(IEfFixture fixture)
{
    public const string Hotfix = "20991231000000_Hotfix";

    protected IEfFixture Fixture { get; } = fixture;

    protected bool ShouldSkip => !Fixture.IsReachable;

    protected EfModel Model => SnapshotReader.Read(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "EfSnapshots", "ef10", Fixture.Provider));

    protected Task<CompareEfModelResult> CompareDrifted() => Fixture.Drifted!.CompareEfModelAsync(new CompareEfModelRequest(Model));

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Drifted_ReportsEachDeliberateDifference_AndNothingElse()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await CompareDrifted();

        result.Signals.Should().BeEmpty();
        result.Findings.Select(f => (f.RuleId, f.Table, Columns: string.Join(",", f.Columns ?? [])))
            .Should().BeEquivalentTo<(string, string, string)>(
            [
                (EfRuleIds.MigrationUnknown, HistoryTable(result), ""),
                (EfRuleIds.MigrationPending, HistoryTable(result), ""),
                (EfRuleIds.TableMissing, "shop.payments", ""),
                (EfRuleIds.IndexMissing, "shop.products", "sku"),
                (EfRuleIds.ColumnMissing, "shop.users", "phone"),
                (EfRuleIds.ForeignKeyMissing, "shop.order_items", "product_id"),
                (EfRuleIds.IndexUnmodeled, "shop.orders", "created_at"),
                (EfRuleIds.UnmappedTable, "shop.legacy_notes", ""),
                (EfRuleIds.DriftSummary, Fixture.Provider == "PostgreSql" ? "ShopPostgresContext" : "ShopSqlServerContext", ""),
            ]);
        result.Summary.ByRule.Keys.Should().BeEquivalentTo(EfRuleIds.All);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Drifted_SaysWhereTheDatabaseStandsInTheMigrations()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var migrations = (await CompareDrifted()).Migrations;
        var model = Model;

        migrations.HistoryFound.Should().BeTrue();
        migrations.LastApplied.Should().Be(model.MigrationIds[0]);
        migrations.Pending.Should().Equal(model.MigrationIds[1]);
        migrations.Unknown.Should().Equal(Hotfix);
        migrations.OutOfOrder.Should().BeEmpty();
        migrations.ScriptCommands.Should().Equal(
            $"dotnet ef migrations script {model.MigrationIds[0]} {model.MigrationIds[1]} --context {model.ContextName} -o pending.sql");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Drifted_NamesTheEntityAndTheFluentCallToChange()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await CompareDrifted();

        Ef(result, EfRuleIds.IndexUnmodeled)["fluent"]
            .Should().Be("b.HasIndex(e => e.CreatedAt).HasDatabaseName(\"ix_orders_created_manual\")");
        Ef(result, EfRuleIds.ColumnMissing).Should().Contain("entity", "DemoShop.EfCore.User").And.Contain("property", "Phone");
        Ef(result, EfRuleIds.ForeignKeyMissing).Should().Contain("entity", "DemoShop.EfCore.OrderItem");
        result.Caveats.Should().Contain(c => c.Contains("OrderSummary") && c.Contains("view"));
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Clean_ReportsOnlyTheSummary()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Fixture.Clean!.CompareEfModelAsync(new CompareEfModelRequest(Model));

        result.Findings.Should().ContainSingle().Which.RuleId.Should().Be(EfRuleIds.DriftSummary);
        result.Migrations.Pending.Should().BeEmpty();
        result.Migrations.Unknown.Should().BeEmpty();
        result.Migrations.ScriptCommands.Should().BeEmpty();
    }

    private static string HistoryTable(CompareEfModelResult result) => result.Migrations.HistoryTable!;

    private static IReadOnlyDictionary<string, object> Ef(CompareEfModelResult result, string ruleId) =>
        (IReadOnlyDictionary<string, object>)result.Findings.Single(f => f.RuleId == ruleId).Metadata!["ef"];
}
