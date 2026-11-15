using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Gdpr;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for SqlServerInspector.ScanGdprAsync.
/// Requires the dblens-sqlserver container running with the demo schema.
/// Skipped automatically when SQL Server is not reachable.
/// </summary>
public sealed class SqlServerGdprTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture = fixture;

    private bool ShouldSkip => !_fixture.IsReachable;

    [SkippableFact, Trait("Category", "Integration")]
    public async Task ScanGdpr_Result_AlwaysContainsDisclaimer()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(Schema: "dbo"));

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task ScanGdpr_UsersTable_DetectsPiiColumns()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(Schema: "dbo", Tables: ["users"]));

        var piiColumns = result.Signals
            .Where(f => f.RuleId != "missing-gdpr-controls")
            .SelectMany(f => f.Columns ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        piiColumns.Should().Contain("email",         "users.email is PII");
        piiColumns.Should().Contain("first_name",    "users.first_name is PII");
        piiColumns.Should().Contain("last_name",     "users.last_name is PII");
        piiColumns.Should().Contain("phone",         "users.phone is PII");
        piiColumns.Should().Contain("date_of_birth", "users.date_of_birth is PII");
        piiColumns.Should().Contain("fiscal_code",   "users.fiscal_code is PII");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task ScanGdpr_Summary_ContainsAllRuleKeys()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(Schema: "dbo"));

        result.Summary.ByRule.Keys.Should().Contain(
            ["pii-identifier", "pii-contact", "pii-financial",
             "pii-special-category", "pii-location", "missing-gdpr-controls"]);

        result.Summary.BySeverity.Keys.Should().Contain(Enum.GetNames<Severity>());
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task ScanGdpr_ExcludeTables_RemovesTableFromResults()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(
                Schema: "dbo",
                ExcludeTables: ["users", "addresses", "audit_logs", "payments"]));

        var tables = result.Signals
            .Select(f => f.Table.Split('.')[1])
            .Distinct()
            .ToList();

        tables.Should().NotContain("users");
        tables.Should().NotContain("addresses");
        tables.Should().NotContain("audit_logs");
    }
}
