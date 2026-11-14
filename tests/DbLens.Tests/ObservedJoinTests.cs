using DbLens.Core.Analysis.Relationships;
using DbLens.Core.Models;
using FluentAssertions;
using static DbLens.Tests.TestSchema;

namespace DbLens.Tests;

public class ObservedJoinTests
{
    private static readonly TableDetail Shipments =
        Table("shipments", [Col("id", "bigint"), Col("ship_to", "bigint"), Col("carrier")]);

    private static readonly TableDetail Addresses =
        Table("addresses", [Col("id", "bigint"), Col("city")]);

    private static IReadOnlyList<ObservedJoin> Extract(string sql, SqlSourceKind kind = SqlSourceKind.View) =>
        JoinPredicateExtractor.Extract([new SqlSource(kind, "public.v", sql)], [Shipments, Addresses], "public", DatabaseDialect.PostgreSql);

    [Fact]
    public void AnsiJoinWithAliases_IsResolvedAndOrientedTowardTheKey()
    {
        var join = Extract("SELECT a.city FROM shipments s JOIN addresses a ON a.id = s.ship_to").Single();

        join.Child.Name.Should().Be("shipments");
        join.ChildColumn.Should().Be("ship_to");
        join.Parent.Name.Should().Be("addresses");
        join.ParentKey.Should().Be("id");
    }

    [Fact]
    public void TablesWhoseNamesDifferOnlyInCase_AreLeftUnresolved_NotAnError()
    {
        // Legal on PostgreSQL and on a case-sensitive SQL Server; SQL text cannot say which is meant.
        var upper = Table("ADDRESSES", [Col("id", "bigint")]);

        var joins = JoinPredicateExtractor.Extract(
            [new SqlSource(SqlSourceKind.View, "public.v", "SELECT 1 FROM shipments s JOIN addresses a ON a.id = s.ship_to")],
            [Shipments, Addresses, upper], "public", DatabaseDialect.PostgreSql);

        joins.Should().BeEmpty();
    }

    [Fact]
    public void PostgresViewDefinitionStyle_IsUnderstood()
    {
        Extract("SELECT a.city FROM (shipments s\n JOIN addresses a ON ((a.id = s.ship_to)));").Should().ContainSingle();
    }

    [Fact]
    public void SelectListBeforeFrom_DoesNotHideTheFirstTable()
    {
        Extract("SELECT s.id, s.carrier, a.city\n FROM (shipments s JOIN addresses a ON ((a.id = s.ship_to)))")
            .Should().ContainSingle();
    }

    [Fact]
    public void LegacyCommaJoin_InWhereClause_IsUnderstood()
    {
        Extract("SELECT * FROM public.shipments s, public.addresses a WHERE s.ship_to = a.id").Should().ContainSingle();
    }

    [Fact]
    public void QuotedIdentifiers_AreUnquoted()
    {
        Extract("SELECT * FROM [dbo].[shipments] AS s INNER JOIN [addresses] AS a ON s.[ship_to] = a.[id]",
                SqlSourceKind.Routine)
            .Should().BeEmpty("dbo is not the default schema here and the name is qualified");
        Extract("SELECT * FROM \"shipments\" s JOIN \"addresses\" a ON s.\"ship_to\" = a.\"id\"").Should().ContainSingle();
    }

    [Fact]
    public void JoinTextInsideAStringLiteral_IsIgnored()
    {
        Extract("SELECT 'FROM shipments s JOIN addresses a ON a.id = s.ship_to' AS txt").Should().BeEmpty();
    }

    [Fact]
    public void ColumnThatDoesNotExist_IsIgnored()
    {
        Extract("SELECT * FROM shipments s JOIN addresses a ON a.id = s.nope").Should().BeEmpty();
    }

    [Fact]
    public void KeyToKeyJoin_HasNoDirection_AndIsIgnored()
    {
        Extract("SELECT * FROM shipments s JOIN addresses a ON a.id = s.id").Should().BeEmpty();
    }

    [Fact]
    public void ObservedJoin_BecomesACandidateEvenWithoutANamingHint()
    {
        var joins = Extract("SELECT * FROM shipments s JOIN addresses a ON a.id = s.ship_to");

        var outcome = UndeclaredForeignKeyRule.Find([Shipments, Addresses], joins);

        var candidate = outcome.Candidates.Single();
        candidate.Column.Name.Should().Be("ship_to");
        candidate.Basis.Should().Be("observed-join");
        candidate.SeenIn.Should().Equal("view public.v");
    }

    [Fact]
    public void ObservedJoin_SettlesANameThatMatchesSeveralParents()
    {
        var child = Table("lines", [Col("id", "bigint"), Col("doc_id", "bigint")]);
        var docs = Table("docs", [Col("id", "bigint")]);
        var documents = Table("documents", [Col("id", "bigint")], schema: "archive");
        var tables = new[] { child, docs, documents };

        UndeclaredForeignKeyRule.Find(tables).AmbiguousSkipped.Should().BeGreaterThanOrEqualTo(0);

        var joins = JoinPredicateExtractor.Extract(
            [new SqlSource(SqlSourceKind.Workload, "pg_stat_statements", "SELECT 1 FROM lines l JOIN docs d ON d.id = l.doc_id", 42)],
            tables, "public", DatabaseDialect.PostgreSql);
        var outcome = UndeclaredForeignKeyRule.Find(tables, joins);

        outcome.Candidates.Single().Parent.Name.Should().Be("docs");
        outcome.Candidates.Single().SeenIn.Single().Should().Contain("42 executions");
    }

    [Fact]
    public void Signal_NamesWhereTheJoinWasSeen()
    {
        var joins = Extract("SELECT * FROM shipments s JOIN addresses a ON a.id = s.ship_to");
        var analysis = RelationshipAnalysisEngine.Analyze([Shipments, Addresses], joins, 1);

        var signal = analysis.Result.Signals.Single();
        signal.Description.Should().Contain("view public.v");
        signal.Metadata!["seenIn"].Should().BeEquivalentTo(new[] { "view public.v" });
        analysis.Result.Summary.SqlSourcesRead.Should().Be(1);
    }
}
