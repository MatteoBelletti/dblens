using DbLens.Core.Analysis;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Models;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Unit tests for SqlIdentifier. These are the safety contract for every rule that generates
/// SQL from catalog names: an identifier is attacker-influenced whenever the attacker can
/// create a table.
/// </summary>
public sealed class SqlIdentifierTests
{
    // ── Basic quoting ──────────────────────────────────────────────────────────

    [Fact]
    public void Quote_UsesDoubleQuotesOnPostgresAndBracketsOnSqlServer()
    {
        SqlIdentifier.Quote("users", DatabaseDialect.PostgreSql).Should().Be("\"users\"");
        SqlIdentifier.Quote("users", DatabaseDialect.SqlServer).Should().Be("[users]");
    }

    [Fact]
    public void Quote_PreservesCase()
    {
        // An unquoted mixed-case identifier folds to lower case on PostgreSQL and targets
        // a different object, or none.
        SqlIdentifier.Quote("MixedCase", DatabaseDialect.PostgreSql).Should().Be("\"MixedCase\"");
    }

    [Fact]
    public void Quote_AllowsReservedWordsAsIdentifiers()
    {
        SqlIdentifier.Quote("select", DatabaseDialect.PostgreSql).Should().Be("\"select\"");
        SqlIdentifier.Quote("order", DatabaseDialect.SqlServer).Should().Be("[order]");
    }

    [Fact]
    public void Qualify_QuotesBothParts()
    {
        SqlIdentifier.Qualify("public", "users", DatabaseDialect.PostgreSql)
            .Should().Be("\"public\".\"users\"");
        SqlIdentifier.Qualify("dbo", "users", DatabaseDialect.SqlServer)
            .Should().Be("[dbo].[users]");
    }

    // ── Delimiter escaping ─────────────────────────────────────────────────────

    [Fact]
    public void Quote_DoublesAnEmbeddedClosingDelimiter()
    {
        SqlIdentifier.Quote("we\"ird", DatabaseDialect.PostgreSql).Should().Be("\"we\"\"ird\"");
        SqlIdentifier.Quote("a]b", DatabaseDialect.SqlServer).Should().Be("[a]]b]");
    }

    [Fact]
    public void Quote_LeavesTheOtherDialectsDelimiterAlone()
    {
        // A bracket is not special inside a PostgreSQL quoted identifier, and vice versa.
        SqlIdentifier.Quote("a]b", DatabaseDialect.PostgreSql).Should().Be("\"a]b\"");
        SqlIdentifier.Quote("we\"ird", DatabaseDialect.SqlServer).Should().Be("[we\"ird]");
    }

    // ── Injection payloads are inert ───────────────────────────────────────────

    [Theory]
    [InlineData("x\"; DROP TABLE y --")]
    [InlineData("a]b; DROP TABLE y")]
    [InlineData("'; DELETE FROM users; --")]
    [InlineData("tbl /* comment */ name")]
    public void Quote_RendersInjectionPayloadsAsASingleIdentifier(string hostile)
    {
        foreach (var dialect in new[] { DatabaseDialect.PostgreSql, DatabaseDialect.SqlServer })
        {
            var quoted = SqlIdentifier.Quote(hostile, dialect);
            var sql = $"SELECT COUNT(*) FROM {quoted}";

            // The guard blanks quoted regions before scanning for keywords, so nothing inside
            // the identifier can register as DROP, DELETE, or a statement separator.
            StatementGuard.IsReadOnlySelect(sql).Allowed.Should().BeTrue(
                "a hostile identifier must be inert once quoted ({0})", dialect);
        }
    }

    [Fact]
    public void Quote_HostilePayloadIsBlankedByTheGuardsStripper()
    {
        var quoted = SqlIdentifier.Quote("x\"; DROP TABLE y --", DatabaseDialect.PostgreSql);
        var stripped = StatementGuard.StripCommentsAndStrings($"SELECT COUNT(*) FROM {quoted}");

        stripped.Should().NotContain("DROP");
        stripped.Should().NotContain(";");
    }

    // ── Renderability ──────────────────────────────────────────────────────────

}
