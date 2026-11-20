using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Migration;
using DbLens.Core.Models;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Unit tests for DdlClassifier and MigrationSafetyEngine — pure in-memory logic
/// with no database access.
/// </summary>
public sealed class MigrationSafetyTests
{
    // ── Builders ───────────────────────────────────────────────────────────────

    private static MigrationTableContext Ctx(
        string name, long? rows = 5_000_000, long sizeBytes = 1_000_000_000, long incomingFks = 0,
        string schema = "public") =>
        new(schema, name, rows, sizeBytes, incomingFks);

    private static Dictionary<(string, string), MigrationTableContext> Tables(
        params MigrationTableContext[] contexts) =>
        contexts.ToDictionary(c => (c.Schema, c.Name));

    private static CheckMigrationSafetyResult Analyze(
        string ddl,
        Dictionary<(string, string), MigrationTableContext>? tables = null,
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        long largeThreshold = 1_000_000)
    {
        var statements = DdlClassifier.Classify(ddl, dialect);
        return MigrationSafetyEngine.Analyze(
            statements,
            tables ?? Tables(Ctx("users")),
            new CheckMigrationSafetyRequest(ddl, "public", largeThreshold),
            dialect);
    }

    private static IEnumerable<Finding> OfRule(CheckMigrationSafetyResult r, string ruleId) =>
        r.Findings.Where(f => f.RuleId == ruleId);

    // ── Classification ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ALTER TABLE users ADD COLUMN nickname TEXT", MigrationOperationKind.AddColumn)]
    [InlineData("ALTER TABLE users DROP COLUMN nickname", MigrationOperationKind.DropColumn)]
    [InlineData("ALTER TABLE users ALTER COLUMN age TYPE BIGINT", MigrationOperationKind.AlterColumnType)]
    [InlineData("ALTER TABLE users ALTER COLUMN age SET NOT NULL", MigrationOperationKind.SetNotNull)]
    [InlineData("ALTER TABLE users ALTER COLUMN age DROP NOT NULL", MigrationOperationKind.DropNotNull)]
    [InlineData("CREATE INDEX idx_users_email ON users(email)", MigrationOperationKind.CreateIndex)]
    [InlineData("DROP INDEX idx_users_email", MigrationOperationKind.DropIndex)]
    [InlineData("DROP TABLE users", MigrationOperationKind.DropTable)]
    [InlineData("CREATE TABLE t (id INT)", MigrationOperationKind.CreateTable)]
    [InlineData("ALTER TABLE users RENAME COLUMN a TO b", MigrationOperationKind.RenameColumn)]
    [InlineData("ALTER TABLE orders ADD CONSTRAINT fk_u FOREIGN KEY (user_id) REFERENCES users(id)",
        MigrationOperationKind.AddForeignKey)]
    [InlineData("ALTER TABLE orders ADD CONSTRAINT ck_total CHECK (total >= 0)",
        MigrationOperationKind.AddCheckConstraint)]
    [InlineData("ALTER TABLE orders DROP CONSTRAINT fk_u", MigrationOperationKind.DropConstraint)]
    public void Classifier_RecognizesStatementShape(string sql, MigrationOperationKind expected)
    {
        DdlClassifier.ClassifyOne(sql).Kind.Should().Be(expected);
    }

    [Fact]
    public void QualifiedTableNotInTheCatalog_IsNotMatchedToOneOfTheSameNameElsewhere()
    {
        var result = Analyze("CREATE INDEX ix ON shop.payments (order_id)", Tables(Ctx("payments", rows: 35)));

        var finding = OfRule(result, MigrationRuleIds.IndexNotConcurrent).Should().ContainSingle().Subject;
        finding.Table.Should().Be("shop.payments");
        finding.Title.Should().NotContain("35");
    }

    [Fact]
    public void Classifier_ExtractsSchemaAndTable()
    {
        var s = DdlClassifier.ClassifyOne("ALTER TABLE app.users ADD COLUMN x TEXT");

        s.Schema.Should().Be("app");
        s.Table.Should().Be("users");
        s.ObjectName.Should().Be("x");
    }

    [Fact]
    public void Classifier_HandlesQuotedAndBracketedIdentifiers()
    {
        DdlClassifier.ClassifyOne("ALTER TABLE [dbo].[users] DROP COLUMN [nickname]")
            .Should().Match<MigrationStatement>(s =>
                s.Schema == "dbo" && s.Table == "users" && s.ObjectName == "nickname");
    }

    [Fact]
    public void Classifier_DetectsConcurrentIndexBuild()
    {
        DdlClassifier.ClassifyOne("CREATE INDEX CONCURRENTLY idx ON users(email)")
            .IsConcurrent.Should().BeTrue();

        DdlClassifier.ClassifyOne("CREATE INDEX idx ON users(email) WITH (ONLINE = ON)")
            .IsConcurrent.Should().BeTrue();

        DdlClassifier.ClassifyOne("CREATE INDEX idx ON users(email)")
            .IsConcurrent.Should().BeFalse();
    }

    [Fact]
    public void Classifier_DetectsNotValidConstraint()
    {
        DdlClassifier.ClassifyOne(
                "ALTER TABLE orders ADD CONSTRAINT fk FOREIGN KEY (u) REFERENCES users(id) NOT VALID")
            .IsNotValid.Should().BeTrue();
    }

    [Fact]
    public void Classifier_DistinguishesConstantFromVolatileDefault()
    {
        DdlClassifier.ClassifyOne("ALTER TABLE users ADD COLUMN a INT DEFAULT 0")
            .HasVolatileDefault.Should().BeFalse();

        DdlClassifier.ClassifyOne("ALTER TABLE users ADD COLUMN a TIMESTAMPTZ DEFAULT NOW()")
            .HasVolatileDefault.Should().BeTrue();
    }

    [Fact]
    public void Classifier_SplitsMultiStatementScript()
    {
        var statements = DdlClassifier.Classify(
            "ALTER TABLE users ADD COLUMN a INT; CREATE INDEX idx ON users(a); DROP TABLE old_t;", DatabaseDialect.PostgreSql);

        statements.Should().HaveCount(3);
        statements[0].Kind.Should().Be(MigrationOperationKind.AddColumn);
        statements[1].Kind.Should().Be(MigrationOperationKind.CreateIndex);
        statements[2].Kind.Should().Be(MigrationOperationKind.DropTable);
    }

    [Fact]
    public void Classifier_IgnoresComments()
    {
        var statements = DdlClassifier.Classify(
            "-- add a column\nALTER TABLE users ADD COLUMN a INT; /* then index */ CREATE INDEX i ON users(a);",
            DatabaseDialect.PostgreSql);

        statements.Should().HaveCount(2);
    }

    [Fact]
    public void Classifier_ReturnsUnknownForUnrecognizedShape()
    {
        DdlClassifier.ClassifyOne("CREATE TRIGGER trg BEFORE INSERT ON users EXECUTE FUNCTION f()")
            .Kind.Should().Be(MigrationOperationKind.Unknown);
    }

    // ── Severity scales with table size ────────────────────────────────────────

    [Fact]
    public void TableRewrite_IsCriticalOnLargeTableAndModerateOnSmall()
    {
        const string ddl = "ALTER TABLE users ALTER COLUMN age TYPE BIGINT";

        var large = Analyze(ddl, Tables(Ctx("users", rows: 50_000_000)));
        var small = Analyze(ddl, Tables(Ctx("users", rows: 100)));

        OfRule(large, MigrationRuleIds.TableRewrite).Single().Severity.Should().Be(Severity.Critical);
        OfRule(small, MigrationRuleIds.TableRewrite).Single().Severity.Should().Be(Severity.Medium);
    }

    [Fact]
    public void IndexBuild_IsHigherSeverityOnLargeTable()
    {
        const string ddl = "CREATE INDEX idx_users_email ON users(email)";

        var large = Analyze(ddl, Tables(Ctx("users", rows: 50_000_000)));
        var small = Analyze(ddl, Tables(Ctx("users", rows: 50)));

        OfRule(large, MigrationRuleIds.IndexNotConcurrent).Single().Severity.Should().Be(Severity.High);
        OfRule(small, MigrationRuleIds.IndexNotConcurrent).Single().Severity.Should().Be(Severity.Medium);
    }

    [Fact]
    public void Finding_IncludesRowCountInTitle()
    {
        var result = Analyze(
            "ALTER TABLE users ALTER COLUMN age TYPE BIGINT",
            Tables(Ctx("users", rows: 5_000_000)));

        OfRule(result, MigrationRuleIds.TableRewrite).Single()
            .Title.Should().Contain("5,000,000");
    }

    // ── Individual rules ───────────────────────────────────────────────────────

    [Fact]
    public void ConcurrentIndexBuild_ProducesNoFinding()
    {
        var result = Analyze("CREATE INDEX CONCURRENTLY idx ON users(email)");

        OfRule(result, MigrationRuleIds.IndexNotConcurrent).Should().BeEmpty();
    }

    [Fact]
    public void NotValidConstraint_ProducesNoScanFinding()
    {
        var withValidation = Analyze(
            "ALTER TABLE users ADD CONSTRAINT ck CHECK (age >= 0)");
        var withoutValidation = Analyze(
            "ALTER TABLE users ADD CONSTRAINT ck CHECK (age >= 0) NOT VALID");

        OfRule(withValidation, MigrationRuleIds.UnvalidatedConstraint).Should().ContainSingle();
        OfRule(withoutValidation, MigrationRuleIds.UnvalidatedConstraint).Should().BeEmpty();
    }

    [Fact]
    public void AddColumn_WithConstantDefault_IsNotReportedAsRewrite()
    {
        var result = Analyze("ALTER TABLE users ADD COLUMN a INT DEFAULT 0");

        OfRule(result, MigrationRuleIds.TableRewrite).Should().BeEmpty();
    }

    [Fact]
    public void AddColumn_WithVolatileDefault_IsReportedAsRewrite()
    {
        var result = Analyze("ALTER TABLE users ADD COLUMN a TIMESTAMPTZ DEFAULT NOW()");

        OfRule(result, MigrationRuleIds.TableRewrite).Should().ContainSingle();
    }

    [Fact]
    public void AddColumn_NotNullWithoutDefault_IsReportedAsBlocking()
    {
        var result = Analyze("ALTER TABLE users ADD COLUMN a INT NOT NULL");

        var finding = OfRule(result, MigrationRuleIds.BlockingLock).Should().ContainSingle().Subject;
        finding.Severity.Should().Be(Severity.High);
        finding.Description.Should().Contain("backfill");
    }

    [Fact]
    public void DropTable_IsAlwaysCritical()
    {
        var result = Analyze("DROP TABLE users", Tables(Ctx("users", rows: 3)));

        var finding = OfRule(result, MigrationRuleIds.Destructive).Should().ContainSingle().Subject;
        finding.Severity.Should().Be(Severity.Critical);
        finding.Confidence.Should().Be(1.0);
    }

    [Fact]
    public void DropTable_MentionsIncomingForeignKeys()
    {
        var result = Analyze("DROP TABLE users", Tables(Ctx("users", incomingFks: 4)));

        OfRule(result, MigrationRuleIds.Destructive).Single()
            .Description.Should().Contain("4 foreign key");
    }

    [Fact]
    public void Rename_IsFlaggedAsBackwardIncompatible()
    {
        var result = Analyze("ALTER TABLE users RENAME COLUMN email TO email_address");

        OfRule(result, MigrationRuleIds.BlockingLock).Single()
            .Description.Should().Contain("expand-and-contract");
    }

    [Fact]
    public void SafeStatements_ProduceOnlyTheSummaryFinding()
    {
        var result = Analyze("ALTER TABLE users ALTER COLUMN age DROP NOT NULL");

        result.Findings.Should().ContainSingle()
            .Which.RuleId.Should().Be(MigrationRuleIds.Summary);
    }

    // ── Dialect-specific fixes ─────────────────────────────────────────────────

    [Fact]
    public void SqlFix_UsesConcurrentlyOnPostgresAndOnlineOnSqlServer()
    {
        const string ddl = "CREATE INDEX idx ON users(email)";

        var pg = Analyze(ddl, dialect: DatabaseDialect.PostgreSql);
        var ss = Analyze(ddl, dialect: DatabaseDialect.SqlServer);

        OfRule(pg, MigrationRuleIds.IndexNotConcurrent).Single().SqlFix!.Sql.Should().Contain("CONCURRENTLY");
        OfRule(ss, MigrationRuleIds.IndexNotConcurrent).Single().SqlFix!.Sql.Should().Contain("ONLINE = ON");
    }

    [Fact]
    public void SqlFix_ForeignKeyUsesNotValidOnPostgres()
    {
        var result = Analyze(
            "ALTER TABLE users ADD CONSTRAINT fk FOREIGN KEY (a) REFERENCES t(id)",
            dialect: DatabaseDialect.PostgreSql);

        OfRule(result, MigrationRuleIds.UnvalidatedConstraint).Single()
            .SqlFix!.Sql.Should().Contain("NOT VALID").And.Contain("VALIDATE CONSTRAINT");
    }

    // ── Summary and caveats ────────────────────────────────────────────────────

    [Fact]
    public void Summary_AlwaysContainsEveryRuleAndSeverityKey()
    {
        var result = Analyze("ALTER TABLE users ADD COLUMN a INT");

        foreach (var rule in MigrationRuleIds.All)
            result.Summary.ByRule.Should().ContainKey(rule);

        foreach (var severity in Enum.GetValues<Severity>())
            result.Summary.BySeverity.Should().ContainKey(severity.ToString());
    }

    [Fact]
    public void SummaryFinding_IsAlwaysPresentExactlyOnce()
    {
        var result = Analyze("DROP TABLE users; DROP TABLE orders;",
            Tables(Ctx("users"), Ctx("orders")));

        OfRule(result, MigrationRuleIds.Summary).Should().ContainSingle()
            .Which.Severity.Should().Be(Severity.Info);
    }

    [Fact]
    public void UnrecognizedStatements_AreCountedAndCaveated()
    {
        var result = Analyze("CREATE TRIGGER trg BEFORE INSERT ON users EXECUTE FUNCTION f()");

        result.Summary.StatementsUnrecognized.Should().Be(1);
        result.Caveats.Should().Contain(c => c.Contains("unrecognized does not mean safe"));
    }

    [Fact]
    public void UnknownTable_ProducesCaveatRatherThanSizeClaim()
    {
        var result = Analyze(
            "ALTER TABLE brand_new ALTER COLUMN a TYPE BIGINT",
            Tables(Ctx("users")));

        result.Caveats.Should().Contain(c => c.Contains("brand_new"));
        OfRule(result, MigrationRuleIds.TableRewrite).Single()
            .Description.Should().Contain("not found in the catalog");
    }

    [Fact]
    public void Empty_ContainsOnlySummaryFinding()
    {
        var result = MigrationSafetyEngine.Empty();

        result.Findings.Should().ContainSingle()
            .Which.RuleId.Should().Be(MigrationRuleIds.Summary);
        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Disclaimer_StatesThatUnknownIsNotSafe()
    {
        var result = Analyze("ALTER TABLE users ADD COLUMN a INT");

        result.Disclaimer.Should().Contain("not the same as safe");
    }
}
