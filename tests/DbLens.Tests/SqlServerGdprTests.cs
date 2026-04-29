using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Gdpr;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for SqlServerInspector.ScanGdprAsync.
/// Requires the dblens-sqlserver container running with the demo schema.
/// Skipped automatically when SQL Server is not reachable.
/// </summary>
public sealed class SqlServerGdprTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;
    public SqlServerGdprTests(SqlServerFixture fixture) => _fixture = fixture;
    private bool ShouldSkip => !_fixture.IsReachable;

    [Fact, Trait("Category", "Integration")]
    public async Task ScanGdpr_Result_AlwaysContainsDisclaimer()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(Schema: "dbo"));

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ScanGdpr_UsersTable_DetectsPiiColumns()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(Schema: "dbo", Tables: ["users"]));

        var piiColumns = result.Findings
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

    [Fact, Trait("Category", "Integration")]
    public async Task ScanGdpr_Summary_ContainsAllRuleKeys()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(Schema: "dbo"));

        result.Summary.ByRule.Keys.Should().Contain(
            ["pii-identifier", "pii-contact", "pii-financial",
             "pii-special-category", "pii-location", "missing-gdpr-controls"]);

        result.Summary.BySeverity.Keys.Should().Contain(Enum.GetNames<Severity>());
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ScanGdpr_ExcludeTables_RemovesTableFromResults()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(
                Schema: "dbo",
                ExcludeTables: ["users", "addresses", "audit_logs", "payments"]));

        var tables = result.Findings
            .Select(f => f.Table.Split('.')[1])
            .Distinct()
            .ToList();

        tables.Should().NotContain("users");
        tables.Should().NotContain("addresses");
        tables.Should().NotContain("audit_logs");
    }
}
