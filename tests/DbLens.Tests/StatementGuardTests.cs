using DbLens.Core.Analysis.QueryPlan;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Unit tests for StatementGuard — the allow-list that decides whether a statement may be
/// executed by EXPLAIN ANALYZE. Every rejection case here is a case where executing the
/// statement could modify data, so these tests are the security contract of explain_query.
/// </summary>
public sealed class StatementGuardTests
{
    private static bool Allows(string sql) => StatementGuard.IsReadOnlySelect(sql).Allowed;

    // ── Accepted: genuinely read-only ──────────────────────────────────────────

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("select * from users")]
    [InlineData("SELECT id, email FROM users WHERE id = 1")]
    [InlineData("SELECT * FROM users;")]
    [InlineData("   \n  SELECT * FROM users  \n ")]
    [InlineData("WITH recent AS (SELECT * FROM orders) SELECT * FROM recent")]
    [InlineData("WITH RECURSIVE t(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM t WHERE n < 10) SELECT * FROM t")]
    [InlineData("SELECT u.id FROM users u JOIN orders o ON o.user_id = u.id GROUP BY u.id HAVING COUNT(*) > 5")]
    public void Allows_ReadOnlySelectStatements(string sql)
    {
        Allows(sql).Should().BeTrue();
    }

    [Fact]
    public void Allows_SelectWithKeywordLikeIdentifiers()
    {
        // Column names that merely contain a forbidden word as a substring must not be rejected.
        Allows("SELECT created_at, updated_at, begin_date FROM audit_logs").Should().BeTrue();
    }

    [Fact]
    public void Allows_SelectWithForbiddenWordInsideStringLiteral()
    {
        // The literal is stripped before keyword scanning, so its contents cannot trigger a rejection.
        Allows("SELECT * FROM orders WHERE status = 'delete me'").Should().BeTrue();
    }

    // ── Rejected: writes data ──────────────────────────────────────────────────

    [Theory]
    [InlineData("INSERT INTO users (email) VALUES ('x')")]
    [InlineData("UPDATE users SET email = 'x'")]
    [InlineData("DELETE FROM users")]
    [InlineData("TRUNCATE TABLE users")]
    [InlineData("DROP TABLE users")]
    [InlineData("CREATE TABLE t (id INT)")]
    [InlineData("ALTER TABLE users ADD COLUMN x INT")]
    [InlineData("MERGE INTO users USING staging ON users.id = staging.id WHEN MATCHED THEN DELETE")]
    [InlineData("GRANT SELECT ON users TO public")]
    public void Rejects_DataModifyingStatements(string sql)
    {
        Allows(sql).Should().BeFalse();
    }

    // ── Rejected: bypass attempts ──────────────────────────────────────────────

    [Fact]
    public void Rejects_DataModifyingCteThatStartsWithWith()
    {
        // PostgreSQL executes this and deletes rows, despite the statement starting with WITH.
        var sql = "WITH gone AS (DELETE FROM users RETURNING *) SELECT * FROM gone";

        Allows(sql).Should().BeFalse();
    }

    [Fact]
    public void Rejects_SecondStatementAfterSemicolon()
    {
        Allows("SELECT 1; DROP TABLE users").Should().BeFalse();
    }

    [Fact]
    public void Rejects_WriteHiddenAfterLineComment()
    {
        // The newline ends the comment, so the DELETE is a real second statement.
        Allows("SELECT 1 -- harmless\n; DELETE FROM users").Should().BeFalse();
    }

    [Fact]
    public void Rejects_WriteHiddenInsideBlockCommentedLeadingSelect()
    {
        // Once the block comment is stripped the statement genuinely starts with DELETE.
        Allows("/* SELECT */ DELETE FROM users").Should().BeFalse();
    }

    [Fact]
    public void Rejects_StatementThatIsOnlyAComment()
    {
        Allows("-- SELECT 1").Should().BeFalse();
        Allows("/* nothing here */").Should().BeFalse();
    }

    [Fact]
    public void Rejects_SelectForUpdateBecauseItTakesRowLocks()
    {
        Allows("SELECT * FROM users FOR UPDATE").Should().BeFalse();
    }

    [Fact]
    public void Rejects_SelectIntoWhichCreatesATable()
    {
        Allows("SELECT * INTO backup_users FROM users").Should().BeFalse();
    }

    [Fact]
    public void Rejects_TransactionControlKeywords()
    {
        Allows("SELECT 1 COMMIT").Should().BeFalse();
        Allows("BEGIN SELECT 1").Should().BeFalse();
    }

    [Fact]
    public void Rejects_ProcedureExecution()
    {
        Allows("EXEC sp_executesql N'DELETE FROM users'").Should().BeFalse();
        Allows("CALL do_something()").Should().BeFalse();
    }

    [Fact]
    public void Rejects_EmptyAndWhitespaceInput()
    {
        Allows("").Should().BeFalse();
        Allows("   ").Should().BeFalse();
        StatementGuard.IsReadOnlySelect(null).Allowed.Should().BeFalse();
    }

    // ── Rejection reasons are explanatory ──────────────────────────────────────

    [Fact]
    public void RejectionReason_NamesTheOffendingKeyword()
    {
        var result = StatementGuard.IsReadOnlySelect("WITH gone AS (DELETE FROM users RETURNING *) SELECT * FROM gone");

        result.RejectionReason.Should().Contain("DELETE");
    }

    [Fact]
    public void RejectionReason_ExplainsHowToProceedWithoutExecuting()
    {
        var result = StatementGuard.IsReadOnlySelect("DELETE FROM users");

        result.RejectionReason.Should().Contain("without analyze");
    }

    [Fact]
    public void RejectionReason_IsPresentForEveryRejection()
    {
        var result = StatementGuard.IsReadOnlySelect("SELECT 1; SELECT 2");

        result.Allowed.Should().BeFalse();
        result.RejectionReason.Should().NotBeNullOrWhiteSpace();
    }

    // ── Literal stripping ──────────────────────────────────────────────────────

    [Fact]
    public void StripsQuotedIdentifiersAndLiterals()
    {
        var stripped = StatementGuard.StripCommentsAndStrings(
            "SELECT \"drop\", '[delete]' FROM [update]");

        stripped.Should().NotContain("drop");
        stripped.Should().NotContain("delete");
        stripped.Should().NotContain("update");
    }

    [Fact]
    public void HandlesEscapedQuotesInsideLiterals()
    {
        // The doubled quote does not terminate the literal, so DELETE stays inside it.
        Allows("SELECT * FROM t WHERE name = 'it''s a DELETE'").Should().BeTrue();
    }
}
