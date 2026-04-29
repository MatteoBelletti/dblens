using System.ComponentModel;
using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class GetTableDetailsTool(IServiceProvider services, IDatabaseInspectorFactory factory)
{
    private IKeyedServiceProvider KeyedServices => (IKeyedServiceProvider)services;

    [McpServerTool, Description(
        "Returns detailed schema information for one or more specific tables or views: " +
        "columns with types and nullability, all indexes with their definitions, " +
        "foreign keys (outgoing and incoming), check constraints, and unique constraints. " +
        "Use this after get_schema to inspect specific tables. " +
        "Pass multiple table names in a single call to avoid round trips. " +
        "IMPORTANT: this tool returns only structural metadata. " +
        "The following data is NOT included — do NOT infer, guess, or generate it: " +
        "(1) view SQL bodies (SELECT definitions) — for views only column metadata is returned; " +
        "(2) actual row data or sample values; " +
        "(3) structure of tables listed in the NotFound field — those objects do not exist in the database.")]
    public Task<TableDetailsResult> GetTableDetails(
        [Description(
            "Table names to inspect. Format: \"schema.name\" (e.g. \"public.users\") or just \"name\" " +
            "(uses default schema). Pass multiple tables to retrieve all in a single call. Maximum 20 tables.")]
        string[] tables,
        [Description("Database dialect: PostgreSql or SqlServer.")]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(
            "Default schema when table names have no schema prefix. " +
            "Defaults to \"public\" for PostgreSQL and \"dbo\" for SQL Server when omitted.")]
        string? defaultSchema = null,
        [Description(
            "Connection string to the target database. " +
            "If omitted, uses the server's configured default for the chosen dialect.")]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        if (tables is null || tables.Length == 0)
            throw new ArgumentException("tables must not be empty.");

        if (tables.Length > 20)
            throw new ArgumentException($"tables list must not exceed 20 elements (got {tables.Length}).");

        var empty = Array.FindIndex(tables, t => string.IsNullOrWhiteSpace(t));
        if (empty >= 0)
            throw new ArgumentException($"tables[{empty}] is empty or whitespace.");

        var inspector = connectionString is not null
            ? factory.Create(dialect, connectionString)
            : KeyedServices.GetRequiredKeyedService<IDatabaseInspector>(dialect);

        return inspector.GetTableDetailsAsync(new TableDetailsRequest(tables, defaultSchema), cancellationToken);
    }
}
