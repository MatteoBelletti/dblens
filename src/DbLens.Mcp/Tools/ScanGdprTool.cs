using System.ComponentModel;
using DbLens.Core.Abstractions;
using DbLens.Core.Analysis.Gdpr;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class ScanGdprTool(IServiceProvider services, IDatabaseInspectorFactory factory)
{
    private IKeyedServiceProvider KeyedServices => (IKeyedServiceProvider)services;

    [McpServerTool, Description(
        "Scans database table columns for potential GDPR/PII-relevant data using column name pattern matching. " +
        "Detects six rule types: " +
        "(1) pii-identifier — direct identifiers (name, date of birth, gender) and national IDs (SSN, codice fiscale, passport); " +
        "(2) pii-contact — contact data (email, phone, address) and online identifiers (IP, username, session_id); " +
        "(3) pii-financial — payment data (card number, IBAN, CVV, bank account); " +
        "(4) pii-special-category — Article 9 special categories (health, biometric, religion, political view, ethnicity, sexual orientation); " +
        "(5) pii-location — precise location data (latitude/longitude, geolocation, GPS); " +
        "(6) missing-gdpr-controls — tables with PII columns that lack soft-delete and/or audit timestamps. " +
        "Severity is adjusted by structural markers: Critical findings are always Critical; " +
        "High-severity findings are downgraded to Medium when the table contains an encryption/hashing marker column. " +
        "The response always contains a Disclaimer field explaining the limitations of automated PII detection. " +
        "IMPORTANT: This tool uses column NAME patterns only — it does NOT read, inspect, or sample actual row data. " +
        "Results are probabilistic and may include false positives. " +
        "Do NOT treat findings as confirmed PII without human review. " +
        "Do NOT infer, guess, or generate findings beyond what this tool returns. " +
        "Always surface the Disclaimer to the user before presenting findings.")]
    public Task<ScanGdprResult> ScanGdpr(
        [Description("Database dialect: PostgreSql or SqlServer.")]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(
            "Schema to scan. Null = all user schemas. " +
            "Use 'public' for PostgreSQL default schema, 'dbo' for SQL Server.")]
        string? schema = null,
        [Description(
            "Table names to include (without schema prefix). Null = all tables in the specified schema(s).")]
        string[]? tables = null,
        [Description(
            "Table names to exclude from the scan (without schema prefix). " +
            "Useful for excluding known-safe tables like migration history or system tables.")]
        string[]? excludeTables = null,
        [Description(
            "Minimum confidence threshold for pattern matches (0.0–1.0). " +
            "Default 0.5 balances recall vs precision. " +
            "Raise to 0.8+ to reduce false positives; lower to 0.3 to maximize recall.")]
        double minConfidence = 0.5,
        [Description(
            "Connection string to the target database. " +
            "If omitted, uses the server's configured default for the chosen dialect.")]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        var inspector = connectionString is not null
            ? factory.Create(dialect, connectionString)
            : KeyedServices.GetRequiredKeyedService<IDatabaseInspector>(dialect);

        return inspector.ScanGdprAsync(
            new ScanGdprRequest(schema, tables, excludeTables, minConfidence),
            cancellationToken);
    }
}
