using DbLens.Core.Abstractions;
using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Fragmentation;
using DbLens.Core.Analysis.Indexes;
using DbLens.Core.Models;
using DbLens.Postgres;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace DbLens.Tests;

public sealed class PostgresEdgeCatalogTests(PostgresEdgeFixture fixture)
    : EdgeCatalogTestsBase(fixture), IClassFixture<PostgresEdgeFixture>
{
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task PartitionedTable_IsOneTable_WithItsPartitionsMarked()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var overview = await Inspector.GetSchemaOverviewAsync(new SchemaOverviewRequest(Schema));
        var objects = overview.Schemas.Single().Objects;

        var events = objects.Single(o => o.Name == "events");
        events.PartitionOf.Should().BeNull();
        events.ApproximateRowCount.Should().Be(4, "a partitioned table's rows are those of its partitions");
        events.IncomingForeignKeys.Should().Be(1, "the clones PostgreSQL keeps per partition are one constraint");

        objects.Where(o => o.Name.StartsWith("events_")).Should().HaveCount(2)
            .And.OnlyContain(o => o.PartitionOf == $"{Schema}.events");
        objects.Single(o => o.Name == "event_notes").OutgoingForeignKeys.Should().Be(1);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task PartitionedTable_HasDetails_AndItsForeignKeyIsReportedOnce()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var details = await Inspector.GetTableDetailsAsync(
            new TableDetailsRequest([$"{Schema}.events", $"{Schema}.event_notes"]));

        details.NotFound.Should().BeEmpty();
        details.Tables[0].Kind.Should().Be("table");
        details.Tables[0].PrimaryKey!.Columns.Should().Equal("id", "created_at");
        details.Tables[1].ForeignKeysOutgoing.Should().ContainSingle().Which.ToTable.Should().Be("events");

        var analysis = await Inspector.AnalyzeIndexesAsync(new AnalyzeIndexesRequest(Schema));

        analysis.Findings.Where(f => f.RuleId == IndexRuleIds.FkWithoutIndex && f.Table == $"{Schema}.event_notes")
            .Should().ContainSingle();
        analysis.Findings.Should().NotContain(f => f.Table.StartsWith($"{Schema}.events_"),
            "a partition repeats its parent's structure and is analyzed through it");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Fragmentation_SkipsTheStoragelessIndexOfAPartitionedTable()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Inspector.AnalyzeFragmentationAsync(new AnalyzeFragmentationRequest(Schema));

        result.StatsAvailable.Should().BeTrue();
        result.Summary.IndexesSkippedTooSmall.Should().BeGreaterThan(0);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task MaterializedView_IsFound_AndSaysWhatItIs()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var details = await Inspector.GetTableDetailsAsync(new TableDetailsRequest([$"{Schema}.mv_parent"]));

        details.Tables.Single().Kind.Should().Be("materialized view");
        details.Tables.Single().Columns.Should().ContainSingle();
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task QuotedColumnNames_MatchBetweenIndexesAndForeignKeys()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var details = await Inspector.GetTableDetailsAsync(new TableDetailsRequest([$"{Schema}.Mixed Kid"]));
        var table = details.Tables.Single();

        table.PrimaryKey!.Columns.Should().Equal("Id");
        table.Indexes.Single(i => i.Name == "ix_mixed").Columns.Should().Equal("Parent Id");

        var analysis = await Inspector.AnalyzeIndexesAsync(new AnalyzeIndexesRequest(Schema, ["Mixed Kid"]));

        analysis.Findings.Should().NotContain(f => f.RuleId == IndexRuleIds.FkWithoutIndex,
            "the index on \"Parent Id\" covers the foreign key on it");
    }
}

/// <summary>Creates and drops the edge database on the demo PostgreSQL server.</summary>
public sealed class PostgresEdgeFixture : IAsyncLifetime, IEdgeFixture
{
    private const string Database = "dblens_edge";

    private const string SetupSql = """
        CREATE EXTENSION IF NOT EXISTS pgstattuple;
        CREATE SCHEMA "edge.dot";
        CREATE SCHEMA edge;

        CREATE TABLE "edge.dot"."tbl.with.dot" (id int PRIMARY KEY, note text);
        CREATE TABLE "edge.dot".child (
            id int PRIMARY KEY,
            parent_id int REFERENCES "edge.dot"."tbl.with.dot"(id) ON DELETE CASCADE);

        CREATE TABLE edge."Twin" (id int PRIMARY KEY, label text);
        CREATE TABLE edge.twin (id int PRIMARY KEY, label text);

        CREATE TABLE edge.parent (id int PRIMARY KEY);
        CREATE TABLE edge.kid (id int PRIMARY KEY, parent_id int REFERENCES edge.parent(id));
        CREATE INDEX ix_kid_partial ON edge.kid (parent_id) WHERE parent_id > 0;
        CREATE VIEW edge.v_kid AS SELECT id, parent_id FROM edge.kid;
        CREATE MATERIALIZED VIEW edge.mv_parent AS SELECT id FROM edge.parent;

        CREATE TABLE edge."Mixed Kid" ("Id" int PRIMARY KEY, "Parent Id" int REFERENCES edge.parent(id));
        CREATE INDEX ix_mixed ON edge."Mixed Kid" ("Parent Id");

        CREATE TABLE edge.covered (id int PRIMARY KEY, a int, b int);
        CREATE INDEX ix_a ON edge.covered (a);
        CREATE INDEX ix_a_incl ON edge.covered (a) INCLUDE (b);
        CREATE INDEX ix_a_b ON edge.covered (a, b);
        CREATE INDEX ix_a_hash ON edge.covered USING hash (a);

        CREATE TABLE edge.events (id int, created_at date NOT NULL, PRIMARY KEY (id, created_at))
            PARTITION BY RANGE (created_at);
        CREATE TABLE edge.events_2024 PARTITION OF edge.events FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
        CREATE TABLE edge.events_2025 PARTITION OF edge.events FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
        INSERT INTO edge.events VALUES (1, '2024-03-01'), (2, '2024-04-01'), (3, '2025-03-01'), (4, '2025-04-01');
        CREATE TABLE edge.event_notes (
            id int PRIMARY KEY, event_id int, event_date date,
            FOREIGN KEY (event_id, event_date) REFERENCES edge.events (id, created_at) ON DELETE CASCADE);

        CREATE ROLE dblens_edge_limited LOGIN PASSWORD 'limited';
        GRANT USAGE ON SCHEMA edge TO dblens_edge_limited;
        GRANT SELECT ON ALL TABLES IN SCHEMA edge TO dblens_edge_limited;

        ANALYZE;
        """;

    private const string DropSql =
        $"DROP DATABASE IF EXISTS {Database} WITH (FORCE); DROP ROLE IF EXISTS dblens_edge_limited";

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("DBLENS_TEST_PG_ADMIN_CONNECTION")
        ?? "Host=localhost;Port=5433;Database=dblens_demo;Username=postgres;Password=devpassword";

    private static string EdgeConnectionString =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = Database, Pooling = false }.ToString();

    public bool IsReachable { get; private set; }

    public IDatabaseInspector? Inspector { get; private set; }

    public IDatabaseInspector? LimitedInspector { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            await ExecuteAsync(AdminConnectionString, "SELECT 1");
        }
        catch
        {
            return;
        }

        // From here a failure is a broken fixture, not an unreachable database: it must fail, not skip.
        foreach (var statement in DropSql.Split(';'))
            await ExecuteAsync(AdminConnectionString, statement);
        await ExecuteAsync(AdminConnectionString, $"CREATE DATABASE {Database}");
        await ExecuteAsync(EdgeConnectionString, SetupSql);

        Inspector = new PostgresInspector(EdgeConnectionString, NullLogger<PostgresInspector>.Instance);
        LimitedInspector = new PostgresInspector(
            new NpgsqlConnectionStringBuilder(EdgeConnectionString) { Username = "dblens_edge_limited", Password = "limited" }.ToString(),
            NullLogger<PostgresInspector>.Instance);
        IsReachable = true;
    }

    public async Task DisposeAsync()
    {
        if (!IsReachable) return;

        // DROP DATABASE cannot share a statement batch with anything else.
        foreach (var statement in DropSql.Split(';'))
            await ExecuteAsync(AdminConnectionString, statement);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 5 }.ToString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync();
    }
}
