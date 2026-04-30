using System.Text.Json;
using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Indexes;
using DbLens.Core.Analysis.Indexes.Rules;
using DbLens.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DbLens.SqlServer;

public sealed partial class SqlServerInspector
{
    private const string IndexUsageStatsSql = """
        SELECT
            sch.name        AS schema_name,
            t.name          AS table_name,
            i.name          AS index_name,
            ISNULL(u.user_seeks,   0)
            + ISNULL(u.user_scans,   0)
            + ISNULL(u.user_lookups, 0) AS total_scans,
            CAST(DATEDIFF(SECOND, si.sqlserver_start_time, GETUTCDATE()) AS FLOAT) / 86400.0
                            AS uptime_days
        FROM sys.indexes i
        JOIN sys.tables t   ON t.object_id  = i.object_id
        JOIN sys.schemas sch ON sch.schema_id = t.schema_id
        CROSS JOIN sys.dm_os_sys_info si
        LEFT JOIN sys.dm_db_index_usage_stats u
            ON  u.object_id  = i.object_id
            AND u.index_id   = i.index_id
            AND u.database_id = DB_ID()
        WHERE i.type > 0
        AND EXISTS (
            SELECT 1 FROM OPENJSON(@pairs)
            WITH (s NVARCHAR(128) '$.s', n NVARCHAR(128) '$.n') AS p
            WHERE p.s = sch.name AND p.n = t.name
        )
        """;

    public async Task<AnalyzeIndexesResult> AnalyzeIndexesAsync(
        AnalyzeIndexesRequest request,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Starting index analysis (schema={Schema}, tables={Tables}, includeUnused={IncludeUnused})",
            request.Schema ?? "*", request.Tables is null ? "*" : string.Join(",", request.Tables),
            request.IncludeUnusedIndexes);

        try
        {
            var tableScope = await ResolveTableScopeAsync(request, ct);
            if (tableScope.Count == 0)
                return EmptyAnalysisResult();

            var tableNames = tableScope.Select(t => $"{t.Schema}.{t.Name}").ToArray();
            var details = await GetTableDetailsAsync(new TableDetailsRequest(tableNames), ct);

            var findings = new List<Finding>();
            findings.AddRange(FkWithoutIndexRule.Apply(details.Tables, Dialect));
            findings.AddRange(DuplicateIndexRule.Apply(details.Tables, Dialect));
            findings.AddRange(RedundantIndexRule.Apply(details.Tables, Dialect));

            var caveats = new List<string>();

            if (request.IncludeUnusedIndexes)
            {
                var stats = await QueryIndexUsageStatsAsync(tableScope, ct);

                if (stats.UptimeDays < request.UnusedIndexMinDays)
                {
                    caveats.Add(
                        $"Insufficient DB uptime for the 'unused-index' rule " +
                        $"({stats.UptimeDays:F0} day(s), minimum required: {request.UnusedIndexMinDays}). " +
                        "The rule was skipped.");
                }
                else
                {
                    var unusedFindings = UnusedIndexRule.Apply(details.Tables, stats, Dialect).ToList();
                    findings.AddRange(unusedFindings);
                    if (unusedFindings.Count > 0)
                        caveats.Add(
                            $"Database uptime: {stats.UptimeDays:F0} day(s). " +
                            "The 'unused-index' rule counts scans accumulated since the last DB restart. " +
                            "Verify manually before dropping any index.");
                }
            }

            var truncated = false;
            if (findings.Count > DiagnosticLimits.MaxFindingCount)
            {
                findings = findings.Take(DiagnosticLimits.MaxFindingCount).ToList();
                truncated = true;
                caveats.Add(
                    $"Results truncated to {DiagnosticLimits.MaxFindingCount} findings. " +
                    "Use the schema/tables filters for a more targeted analysis.");
            }

            _logger.LogInformation(
                "Index analysis completed: {FindingCount} findings, {Caveats} caveats",
                findings.Count, caveats.Count);

            return new AnalyzeIndexesResult(
                Findings: findings,
                Summary: BuildAnalysisSummary(findings),
                Caveats: caveats,
                Truncated: truncated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex)
        {
            throw new DatabaseInspectionException(
                $"SQL Server error during index analysis: {ex.Message}", ex);
        }
    }

    private async Task<List<(string Schema, string Name)>> ResolveTableScopeAsync(
        AnalyzeIndexesRequest request,
        CancellationToken ct)
    {
        var overview = await GetSchemaOverviewAsync(
            new SchemaOverviewRequest(
                SchemaFilter: request.Schema,
                IncludeViews: false,
                IncludeSystemSchemas: false),
            ct);

        IEnumerable<(string Schema, string Name)> scope = overview.Schemas
            .SelectMany(s => s.Objects
                .Where(o => o.Kind == DatabaseObjectKind.Table)
                .Select(o => (Schema: s.Name, Name: o.Name)));

        if (request.Tables is { Length: > 0 })
        {
            var filter = new HashSet<string>(request.Tables, StringComparer.OrdinalIgnoreCase);
            scope = scope.Where(t => filter.Contains(t.Name));
        }

        return scope.ToList();
    }

    private async Task<IndexUsageStats> QueryIndexUsageStatsAsync(
        List<(string Schema, string Name)> tables,
        CancellationToken ct)
    {
        var pairs = JsonSerializer.Serialize(
            tables.Select(t => new { s = t.Schema, n = t.Name }));

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IndexUsageStatsSql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        cmd.Parameters.Add(new SqlParameter("pairs", System.Data.SqlDbType.NVarChar, -1) { Value = pairs });

        var scanCounts = new Dictionary<(string, string, string), long>();
        double uptimeDays = 0;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            uptimeDays = r.GetDouble(r.GetOrdinal("uptime_days"));
            scanCounts[(
                r.GetString(r.GetOrdinal("schema_name")),
                r.GetString(r.GetOrdinal("table_name")),
                r.GetString(r.GetOrdinal("index_name"))
            )] = r.GetInt64(r.GetOrdinal("total_scans"));
        }

        return new IndexUsageStats(scanCounts, uptimeDays);
    }

    private static AnalyzeIndexesResult EmptyAnalysisResult() =>
        new(
            Findings: [],
            Summary: BuildAnalysisSummary([]),
            Caveats: [],
            Truncated: false);

    private static AnalyzeIndexesSummary BuildAnalysisSummary(IReadOnlyList<Finding> findings)
    {
        var bySeverity = Enum.GetValues<Severity>()
            .ToDictionary(s => s.ToString(), s => findings.Count(f => f.Severity == s));

        var byRule = IndexRuleIds.All
            .ToDictionary(r => r, r => findings.Count(f => f.RuleId == r));

        return new AnalyzeIndexesSummary(
            TotalFindings: findings.Count,
            BySeverity: bySeverity,
            ByRule: byRule);
    }
}
