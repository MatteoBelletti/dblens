using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Indexes;
using DbLens.Core.Analysis.Indexes.Rules;
using DbLens.Core.Models;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    private const string IndexUsageStatsSql = """
        SELECT
            s.schemaname,
            s.relname          AS table_name,
            s.indexrelname     AS index_name,
            s.idx_scan,
            EXTRACT(EPOCH FROM (NOW() - pg_postmaster_start_time())) / 86400.0 AS uptime_days
        FROM pg_stat_user_indexes s
        WHERE (s.schemaname, s.relname) IN (
            SELECT * FROM unnest(@schemas::text[], @names::text[])
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
                        $"Uptime DB insufficiente per la regola 'unused-index' " +
                        $"({stats.UptimeDays:F0} giorni, minimo richiesto: {request.UnusedIndexMinDays} giorni). " +
                        "La regola è stata saltata.");
                }
                else
                {
                    var unusedFindings = UnusedIndexRule.Apply(details.Tables, stats, Dialect).ToList();
                    findings.AddRange(unusedFindings);
                    if (unusedFindings.Count > 0)
                        caveats.Add(
                            $"Database uptime: {stats.UptimeDays:F0} giorni. " +
                            "La regola 'unused-index' considera scan accumulati dall'ultimo restart del DB. " +
                            "Verifica manualmente prima di droppare un indice.");
                }
            }

            const int maxFindings = 200;
            var truncated = false;
            if (findings.Count > maxFindings)
            {
                findings = findings.Take(maxFindings).ToList();
                truncated = true;
                caveats.Add(
                    $"Risultati troncati a {maxFindings} finding. " +
                    "Usa i filtri schema/tables per un'analisi più granulare.");
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
        catch (NpgsqlException ex)
        {
            throw new DatabaseInspectionException(
                $"PostgreSQL error during index analysis: {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            throw new DatabaseInspectionException(
                $"Timeout during index analysis: {ex.Message}", ex);
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
        var schemas = tables.Select(t => t.Schema).ToArray();
        var names = tables.Select(t => t.Name).ToArray();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IndexUsageStatsSql;
        cmd.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas });
        cmd.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names });

        var scanCounts = new Dictionary<(string, string, string), long>();
        double uptimeDays = 0;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            uptimeDays = r.GetDouble(r.GetOrdinal("uptime_days"));
            scanCounts[(
                r.GetString(r.GetOrdinal("schemaname")),
                r.GetString(r.GetOrdinal("table_name")),
                r.GetString(r.GetOrdinal("index_name"))
            )] = r.GetInt64(r.GetOrdinal("idx_scan"));
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

        var byRule = new[] { "fk-without-index", "duplicate-index", "redundant-index", "unused-index" }
            .ToDictionary(r => r, r => findings.Count(f => f.RuleId == r));

        return new AnalyzeIndexesSummary(
            TotalFindings: findings.Count,
            BySeverity: bySeverity,
            ByRule: byRule);
    }
}
