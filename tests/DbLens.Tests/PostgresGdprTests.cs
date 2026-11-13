using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Gdpr;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for PostgresInspector.ScanGdprAsync.
/// Requires the dblens-postgres container running with the demo schema.
/// Skipped automatically when PostgreSQL is not reachable.
/// </summary>
public sealed class PostgresGdprTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    public PostgresGdprTests(PostgresFixture fixture) => _fixture = fixture;
    private bool ShouldSkip => !_fixture.IsReachable;

    // --- result always contains Disclaimer ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task ScanGdpr_Result_AlwaysContainsDisclaimer()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(Schema: "public"));

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
        result.Disclaimer.Should().Contain("automated");
    }

    // --- PII detection on the demo schema ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task ScanGdpr_UsersTable_DetectsPiiColumns()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(Schema: "public", Tables: ["users"]));

        var piiColumns = result.Signals
            .Where(f => f.RuleId != "missing-gdpr-controls")
            .SelectMany(f => f.Columns ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        piiColumns.Should().Contain("email",         "users.email is PII (contact)");
        piiColumns.Should().Contain("first_name",    "users.first_name is PII (direct identifier)");
        piiColumns.Should().Contain("last_name",     "users.last_name is PII (direct identifier)");
        piiColumns.Should().Contain("phone",         "users.phone is PII (contact)");
        piiColumns.Should().Contain("date_of_birth", "users.date_of_birth is PII (direct identifier)");
        piiColumns.Should().Contain("fiscal_code",   "users.fiscal_code is PII (national id)");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task ScanGdpr_UsersTable_FiscalCode_IsNationalId()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(Schema: "public", Tables: ["users"]));

        var fiscalCodeFinding = result.Signals.FirstOrDefault(
            f => f.Columns?.Contains("fiscal_code") == true);

        fiscalCodeFinding.Should().NotBeNull("fiscal_code should be detected");
        fiscalCodeFinding!.RuleId.Should().Be("pii-identifier");
        fiscalCodeFinding.Severity.Should().Be(Severity.Critical,
            "national IDs are always Critical");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task ScanGdpr_AuditLogs_IpAddress_DetectedAsOnlineId()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(Schema: "public", Tables: ["audit_logs"]));

        var ipFinding = result.Signals.FirstOrDefault(
            f => f.Columns?.Contains("ip_address") == true);

        ipFinding.Should().NotBeNull("audit_logs.ip_address should be detected as PII");
        ipFinding!.RuleId.Should().Be("pii-contact");
    }

    // --- gap analysis ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task ScanGdpr_UsersTable_MissingGdprControls_SoftDeleteAbsent()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(Schema: "public", Tables: ["users"]));

        var gapFinding = result.Signals.FirstOrDefault(f => f.RuleId == "missing-gdpr-controls");

        gapFinding.Should().NotBeNull(
            "users table has PII but no soft-delete column");
        gapFinding!.Table.Should().Contain("users");
    }

    // --- excludeTables ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task ScanGdpr_ExcludeTables_RemovesTableFromResults()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(
                Schema: "public",
                ExcludeTables: ["users", "addresses", "audit_logs", "payments"]));

        var tables = result.Signals
            .Select(f => f.Table.Split('.')[1])
            .Distinct()
            .ToList();

        tables.Should().NotContain("users");
        tables.Should().NotContain("addresses");
        tables.Should().NotContain("audit_logs");
    }

    // --- minConfidence ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task ScanGdpr_HighMinConfidence_ProducesFewerFindings()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var lowConf = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(Schema: "public", MinConfidence: 0.5));
        var highConf = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(Schema: "public", MinConfidence: 0.95));

        highConf.Signals.Count(f => f.RuleId != "missing-gdpr-controls")
            .Should().BeLessThanOrEqualTo(
                lowConf.Signals.Count(f => f.RuleId != "missing-gdpr-controls"),
                "higher confidence threshold should suppress low-confidence matches");
    }

    // --- summary always-keys ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task ScanGdpr_Summary_ContainsAllRuleKeys()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(Schema: "public"));

        result.Summary.ByRule.Keys.Should().Contain(
            ["pii-identifier", "pii-contact", "pii-financial",
             "pii-special-category", "pii-location", "missing-gdpr-controls"],
            "all 6 rule IDs must be present in the summary even when count is 0");

        result.Summary.BySeverity.Keys.Should().Contain(
            Enum.GetNames<Severity>(),
            "all severity values must be present in the summary");
    }

    // --- FindingCategory is always Compliance ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task ScanGdpr_AllFindings_HaveComplianceCategory()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.ScanGdprAsync(
            new ScanGdprRequest(Schema: "public"));

        result.Signals.Should().AllSatisfy(f =>
            f.Category.Should().Be(FindingCategory.Compliance));
    }
}
