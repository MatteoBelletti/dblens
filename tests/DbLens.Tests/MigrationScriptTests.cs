using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Migration;
using DbLens.Core.Models;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// How a script is cut into statements. A statement the classifier never sees is a destructive
/// change nobody is warned about, so every shape here hid one.
/// </summary>
public sealed class MigrationScriptTests
{
    private static IReadOnlyList<MigrationStatement> Classify(
        string ddl, DatabaseDialect dialect = DatabaseDialect.PostgreSql) =>
        DdlClassifier.Classify(ddl, dialect);

    private static IEnumerable<(MigrationOperationKind Kind, string? Object)> Shape(
        IEnumerable<MigrationStatement> statements) =>
        statements.Select(s => (s.Kind, s.ObjectName));

    [Fact]
    public void SqlServerScript_IsSplitOnGoAndOnStatementsWithoutSeparators()
    {
        var statements = Classify(
            """
            ALTER TABLE dbo.big ADD note nvarchar(50) NULL
            GO
            ALTER TABLE dbo.big DROP COLUMN pad
            DROP TABLE dbo.orders
            go
            TRUNCATE TABLE dbo.shipments
            """,
            DatabaseDialect.SqlServer);

        Shape(statements).Should().Equal(
            (MigrationOperationKind.AddColumn, "note"),
            (MigrationOperationKind.DropColumn, "pad"),
            (MigrationOperationKind.DropTable, "orders"),
            (MigrationOperationKind.TruncateTable, "shipments"));
    }

    [Fact]
    public void SqlServerModule_StaysOneStatement_SoItsBodyIsNotReadAsTheMigration()
    {
        var statements = Classify(
            """
            CREATE PROCEDURE dbo.purge AS
            BEGIN
                DELETE FROM dbo.audit;
                DROP TABLE #scratch
            END
            GO
            DROP TABLE dbo.orders
            """,
            DatabaseDialect.SqlServer);

        Shape(statements).Should().Equal(
            (MigrationOperationKind.Unknown, null),
            (MigrationOperationKind.DropTable, "orders"));
    }

    [Fact]
    public void AlterTableWithSeveralActions_IsOneStatementPerAction()
    {
        var statements = Classify(
            "ALTER TABLE public.big ALTER COLUMN k TYPE numeric(12, 2), ADD COLUMN z int DEFAULT 0, DROP COLUMN pad;");

        Shape(statements).Should().Equal(
            (MigrationOperationKind.AlterColumnType, "k"),
            (MigrationOperationKind.AddColumn, "z"),
            (MigrationOperationKind.DropColumn, "pad"));
        statements.Should().OnlyContain(s => s.Schema == "public" && s.Table == "big");
    }

    [Fact]
    public void SqlServerActionList_InheritsTheVerbItOmits()
    {
        var statements = Classify("ALTER TABLE t DROP COLUMN a, b", DatabaseDialect.SqlServer);

        Shape(statements).Should().Equal(
            (MigrationOperationKind.DropColumn, "a"),
            (MigrationOperationKind.DropColumn, "b"));
    }

    [Fact]
    public void DropTableList_DropsEveryTableNamed()
    {
        var statements = Classify("DROP TABLE IF EXISTS \"Mixed Case\".\"Orders\", public.users CASCADE;");

        statements.Select(s => (s.Kind, s.Schema, s.Table)).Should().Equal(
            (MigrationOperationKind.DropTable, "Mixed Case", "Orders"),
            (MigrationOperationKind.DropTable, "public", "users"));
    }

    [Fact]
    public void QuotedNamesContainingDots_KeepTheirSchemaAndTable()
    {
        var statement = Classify("ALTER TABLE \"my.schema\".\"tbl.with.dot\" DROP COLUMN email").Single();

        (statement.Schema, statement.Table).Should().Be(("my.schema", "tbl.with.dot"));
    }

    [Theory]
    [InlineData("TRUNCATE big", MigrationOperationKind.TruncateTable)]
    [InlineData("TRUNCATE TABLE ONLY public.big", MigrationOperationKind.TruncateTable)]
    [InlineData("DROP SCHEMA many CASCADE", MigrationOperationKind.DropSchema)]
    [InlineData("DROP DATABASE IF EXISTS shop", MigrationOperationKind.DropSchema)]
    public void DestructiveStatements_AreRecognized_AndReportedCritical(string sql, MigrationOperationKind expected)
    {
        var statements = Classify(sql);

        statements.Single().Kind.Should().Be(expected);

        var result = MigrationSafetyEngine.Analyze(
            statements, new Dictionary<(string, string), MigrationTableContext>(),
            new CheckMigrationSafetyRequest(sql, "public"), DatabaseDialect.PostgreSql);

        result.Findings.Should().Contain(f => f.RuleId == MigrationRuleIds.Destructive && f.Severity == Severity.Critical);
    }

    [Fact]
    public void TransactionControl_IsNeitherAStatementNorUnrecognized()
    {
        Classify("BEGIN; DROP TABLE t; COMMIT;").Should().ContainSingle()
            .Which.Kind.Should().Be(MigrationOperationKind.DropTable);

        Classify("BEGIN TRANSACTION\nGO\nDROP TABLE t\nGO\nCOMMIT TRAN", DatabaseDialect.SqlServer)
            .Should().ContainSingle();
    }

    [Theory]
    [InlineData(8, "PostgreSql")]
    [InlineData(9, "PostgreSql")]
    [InlineData(10, "PostgreSql")]
    [InlineData(8, "SqlServer")]
    [InlineData(9, "SqlServer")]
    [InlineData(10, "SqlServer")]
    public void EfCoreScript_ItsBookkeepingIsNotAStatement_AndItsDefaultDropIsOne(int major, string provider)
    {
        var dialect = Enum.Parse<DatabaseDialect>(provider);
        var script = File.ReadAllText(Path.Combine(EfScripts.Folder(provider, major), "DropUserDateOfBirth.sql"));

        List<(MigrationOperationKind, string?)> expected = dialect == DatabaseDialect.SqlServer
            ? [(MigrationOperationKind.DropConstraint, major == 8 ? "@var0" : "@var"), (MigrationOperationKind.DropColumn, "date_of_birth")]
            : [(MigrationOperationKind.DropColumn, "date_of_birth")];
        var statements = Classify(script, dialect);

        Shape(statements).Should().Equal(expected);
        statements.Should().OnlyContain(s => script.Contains(s.RawSql));
    }

    [Theory]
    [InlineData("INSERT INTO dbo.orders (id) VALUES (1)")]
    [InlineData("INSERT INTO dbo.__EFMigrationsHistory_old (id) VALUES (1)")]
    [InlineData("SELECT @n = COUNT(*) FROM dbo.orders")]
    public void StatementsOnlyLikeEfBookkeeping_StayUnrecognized(string sql) =>
        Classify(sql, DatabaseDialect.SqlServer).Should().ContainSingle().Which.Kind.Should().Be(MigrationOperationKind.Unknown);

    [Fact]
    public void FunctionBody_IsNotSplitOnItsOwnSemicolons()
    {
        var statements = Classify(
            "CREATE FUNCTION f() RETURNS trigger AS $$ BEGIN DELETE FROM big; RETURN NEW; END; $$ LANGUAGE plpgsql; DROP TABLE t;");

        Shape(statements).Should().Equal(
            (MigrationOperationKind.Unknown, null),
            (MigrationOperationKind.DropTable, "t"));
    }
}
