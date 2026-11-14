using DbLens.Core.Analysis;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Models;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Unit tests for StatementGuard — the allow-list that decides whether a statement may be
/// executed by EXPLAIN ANALYZE. Every rejection case here is a case where executing the
/// statement could modify data, so these tests are the security contract of explain_query.
/// </summary>
public sealed class StatementGuardTests
{
    private static bool Allows(string sql) =>
        StatementGuard.IsReadOnlySelect(sql, DatabaseDialect.PostgreSql).Allowed
        && StatementGuard.IsReadOnlySelect(sql, DatabaseDialect.SqlServer).Allowed;

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
        StatementGuard.IsReadOnlySelect(null, DatabaseDialect.PostgreSql).Allowed.Should().BeFalse();
    }

    // ── Rejection reasons are explanatory ──────────────────────────────────────

    [Fact]
    public void RejectionReason_NamesTheOffendingKeyword()
    {
        var result = StatementGuard.IsReadOnlySelect("WITH gone AS (DELETE FROM users RETURNING *) SELECT * FROM gone", DatabaseDialect.PostgreSql);

        result.RejectionReason.Should().Contain("DELETE");
    }

    [Fact]
    public void RejectionReason_ExplainsHowToProceedWithoutExecuting()
    {
        var result = StatementGuard.IsReadOnlySelect("DELETE FROM users", DatabaseDialect.PostgreSql);

        result.RejectionReason.Should().Contain("without analyze");
    }

    [Fact]
    public void RejectionReason_IsPresentForEveryRejection()
    {
        var result = StatementGuard.IsReadOnlySelect("SELECT 1; SELECT 2", DatabaseDialect.PostgreSql);

        result.Allowed.Should().BeFalse();
        result.RejectionReason.Should().NotBeNullOrWhiteSpace();
    }

    // ── Literal stripping ──────────────────────────────────────────────────────

    [Fact]
    public void StripsQuotedIdentifiersAndLiterals()
    {
        var stripped = SqlText.StripCommentsAndStrings(
            "SELECT \"drop\", '[delete]' FROM [update]", DatabaseDialect.SqlServer);

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

/// <summary>
/// The guard is a filter, not a proof: these are the side effects a pure SELECT can still reach
/// through functions, remote execution and locking clauses.
/// </summary>
public sealed class StatementGuardSideEffectTests
{
    private static bool Allows(string sql) =>
        StatementGuard.IsReadOnlySelect(sql, DatabaseDialect.PostgreSql).Allowed
        && StatementGuard.IsReadOnlySelect(sql, DatabaseDialect.SqlServer).Allowed;

    [Theory]
    [InlineData("SELECT pg_terminate_backend(123)")]
    [InlineData("SELECT pg_cancel_backend(pid) FROM pg_stat_activity")]
    [InlineData("SELECT PG_TERMINATE_BACKEND (1)")]
    [InlineData("SELECT \"pg_terminate_backend\"(1)")]
    [InlineData("SELECT pg_catalog.pg_reload_conf()")]
    [InlineData("SELECT pg_read_file('/etc/passwd')")]
    [InlineData("SELECT lo_import('/etc/passwd')")]
    [InlineData("SELECT nextval('orders_id_seq')")]
    [InlineData("SELECT set_config('statement_timeout', '0', false)")]
    [InlineData("SELECT pg_advisory_lock(1)")]
    [InlineData("SELECT pg_sleep(600)")]
    [InlineData("SELECT query_to_xml('select 1', true, true, '')")]
    [InlineData("SELECT * FROM dblink('host=x', 'DELETE FROM t') AS t(a int)")]
    [InlineData("SELECT * FROM OPENQUERY(remote, 'DELETE FROM dbo.users')")]
    [InlineData("SELECT * FROM [OPENQUERY](remote, 'x')")]
    [InlineData("SELECT * FROM srv.db.dbo.users")]
    [InlineData("SELECT * FROM [srv].[db].[dbo].[users]")]
    [InlineData("SELECT * FROM users FOR UPDATE")]
    [InlineData("SELECT * FROM users FOR NO KEY UPDATE")]
    [InlineData("SELECT * FROM users FOR SHARE")]
    [InlineData("SELECT * FROM users WITH (UPDLOCK)")]
    [InlineData("SELECT * FROM users WITH (TABLOCKX, HOLDLOCK)")]
    public void Rejects_SideEffectsReachableFromASelect(string sql)
    {
        Allows(sql).Should().BeFalse();
    }

    [Theory]
    [InlineData("SELECT pg_stat_reset()")]
    [InlineData("SELECT pg_stat_statements_reset()")]
    [InlineData("SELECT pg_ls_waldir()")]
    [InlineData("SELECT * FROM pg_logical_slot_get_changes('s', NULL, NULL)")]
    [InlineData("SELECT brin_summarize_new_values('idx')")]
    [InlineData("SELECT NEXT VALUE FOR dbo.order_numbers")]
    public void Rejects_StateChangingFunctionsAndSequences(string sql)
    {
        Allows(sql).Should().BeFalse();
    }

    [Theory]
    [InlineData("SELECT COUNT(*), COALESCE(SUM(total), 0) FROM orders")]
    [InlineData("SELECT * FROM dbo.users")]
    [InlineData("SELECT * FROM dblens_demo.dbo.users")]
    [InlineData("SELECT 'pg_terminate_backend(1)' AS harmless_text")]
    [InlineData("SELECT u.id FROM users u WHERE u.note = 'for update'")]
    [InlineData("SELECT o.total * 1.22 FROM orders o")]
    public void Allows_LookalikesThatHaveNoSideEffect(string sql)
    {
        Allows(sql).Should().BeTrue();
    }
}

/// <summary>
/// The guard must end every string, comment and identifier exactly where the engine does. Each
/// rejected case here hid a second, writing statement from a lexer that did not.
/// </summary>
public sealed class StatementGuardLexingTests
{
    private static bool Allows(string sql, DatabaseDialect dialect) =>
        StatementGuard.IsReadOnlySelect(sql, dialect).Allowed;

    [Theory]
    // E'' strings: the backslash escapes the quote, so the string ends at the second one.
    [InlineData(@"SELECT E'\'' ; COMMIT; DELETE FROM t; --'")]
    // '[' is a subscript on PostgreSQL, not the start of a quoted identifier.
    [InlineData("SELECT ARRAY[']', 'b']; COMMIT; DELETE FROM t; SELECT 'x'")]
    // A comment opener inside a string is text.
    [InlineData("SELECT '/*' ; COMMIT; DELETE FROM t; --*/'")]
    [InlineData("SELECT '--' ; DELETE FROM t")]
    // Dollar quoting: the apostrophes inside are not string delimiters.
    [InlineData("SELECT $$ it's $$ ; COMMIT; DELETE FROM t; SELECT $$ ' $$")]
    [InlineData("SELECT $tag$ ' $tag$ ; DELETE FROM t; SELECT $tag$ ' $tag$")]
    // Block comments nest, so the first */ does not end the comment.
    [InlineData("SELECT 1 /* /* */ ' */ ; DELETE FROM t --'")]
    public void Rejects_StatementsHiddenFromANaiveLexer_OnPostgres(string sql)
    {
        Allows(sql, DatabaseDialect.PostgreSql).Should().BeFalse();
    }

    [Theory]
    [InlineData("SELECT '/*' ; COMMIT; DELETE FROM t; --*/'")]
    // SQL Server has no backslash escapes and no dollar quoting: these strings end early.
    [InlineData(@"SELECT 'a\' ; DELETE FROM t; --'")]
    [InlineData("SELECT $$ ; DELETE FROM t; SELECT $$")]
    [InlineData("SELECT [a]]b] ; DELETE FROM t")]
    [InlineData("SELECT 1 /* /* */ ' */ ; DELETE FROM t --'")]
    public void Rejects_StatementsHiddenFromANaiveLexer_OnSqlServer(string sql)
    {
        Allows(sql, DatabaseDialect.SqlServer).Should().BeFalse();
    }

    [Theory]
    [InlineData("SELECT 1 DENY SELECT ON t TO public")]
    [InlineData("SELECT 1 DISABLE TRIGGER ALL ON t")]
    [InlineData("SELECT 1 RECONFIGURE")]
    [InlineData("SELECT 1 WRITETEXT t.c @p 'x'")]
    public void Rejects_ASecondStatementWithoutSeparator_OnSqlServer(string sql)
    {
        Allows(sql, DatabaseDialect.SqlServer).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"SELECT U&""pg\005Fsleep""(600)")]
    [InlineData(@"SELECT x.u&""pg\005Fterminate\005Fbackend""(1) FROM t x")]
    public void Rejects_UnicodeEscapedIdentifiers_OnPostgres(string sql)
    {
        Allows(sql, DatabaseDialect.PostgreSql).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"SELECT $q$it's; fine$q$, E'tab\t\'q', 'a''b', (ARRAY[1,2,3])[2] /* a /* nested */ ; */ -- ; tail")]
    [InlineData(@"SELECT '$$' AS a, ""weird;name"" FROM t WHERE note LIKE '%\_%'")]
    [InlineData("SELECT price::numeric(10,2), $1 AS param FROM t")]
    [InlineData("SELECT menu & \"flags\" FROM t")]
    public void Allows_LegitimatePostgresLexicalForms(string sql)
    {
        Allows(sql, DatabaseDialect.PostgreSql).Should().BeTrue();
    }

    [Theory]
    [InlineData("SELECT [delete], [a]]; drop] FROM [dbo].[update] WHERE n = N'x; y' -- ; tail")]
    [InlineData("SELECT TOP 5 [trigger] FROM dbo.jobs WITH (NOLOCK) OPTION (USE HINT ('DISABLE_OPTIMIZER_ROWGOAL'))")]
    public void Allows_LegitimateSqlServerLexicalForms(string sql)
    {
        Allows(sql, DatabaseDialect.SqlServer).Should().BeTrue();
    }

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.SqlServer)]
    public void SingleStatement_AcceptsAnyOneStatementAndRejectsASecond(DatabaseDialect dialect)
    {
        StatementGuard.IsSingleStatement("DELETE FROM users WHERE note = 'a;b'; -- done;", dialect).Allowed.Should().BeTrue();
        StatementGuard.IsSingleStatement("SELECT 1; DELETE FROM users", dialect).Allowed.Should().BeFalse();
        StatementGuard.IsSingleStatement("SELECT '/*' ; DELETE FROM users; --*/'", dialect).Allowed.Should().BeFalse();
        StatementGuard.IsSingleStatement("  -- nothing", dialect).Allowed.Should().BeFalse();
    }

    [Fact]
    public void RejectedKeyword_SaysHowToUseItAsAName()
    {
        var result = StatementGuard.IsReadOnlySelect("SELECT comment FROM reviews", DatabaseDialect.PostgreSql);

        result.Allowed.Should().BeFalse();
        result.RejectionReason.Should().Contain("quote it");
        StatementGuard.IsReadOnlySelect("SELECT \"comment\" FROM reviews", DatabaseDialect.PostgreSql).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Segments_ReportQuotedIdentifiersUnescaped()
    {
        SqlText.Segments("SELECT \"a\"\"b\", [c]]d]", DatabaseDialect.SqlServer)
            .Where(s => s.Kind == SqlSegmentKind.QuotedIdentifier)
            .Select(s => s.Text)
            .Should().Equal("a\"b", "c]d");
    }

    [Fact]
    public void SplitStatements_KeepsSemicolonsInsideDollarQuotedBodies()
    {
        var statements = SqlText.SplitStatements(
            "CREATE FUNCTION f() RETURNS int AS $$ BEGIN RETURN 1; END; $$ LANGUAGE plpgsql; DROP TABLE t;",
            DatabaseDialect.PostgreSql);

        statements.Should().HaveCount(2);
        statements[1].Should().Be("DROP TABLE t");
    }
}
