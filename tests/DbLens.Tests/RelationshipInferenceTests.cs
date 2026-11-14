using DbLens.Core.Analysis;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Analysis.Relationships;
using DbLens.Core.Analysis.Verification;
using DbLens.Core.Models;
using FluentAssertions;
using static DbLens.Tests.TestSchema;

namespace DbLens.Tests;

/// <summary>
/// Unit tests for undeclared foreign key inference — pure logic, no database. The verification
/// outcomes are where the honesty of the feature lives: a name is a hypothesis, and only the
/// orphan count decides what it becomes.
/// </summary>
public sealed class RelationshipInferenceTests
{
    private static TableDetail Orders(params ColumnDetail[] extra) =>
        Table("orders", [Col("id", "bigint"), ..extra]);

    private static TableDetail Shipments(params ColumnDetail[] columns) =>
        Table("shipments", [Col("id", "bigint"), ..columns]);

    private static RelationshipAnalysisEngine.Analysis Analyze(params TableDetail[] tables) =>
        RelationshipAnalysisEngine.Analyze(tables);

    // ── Naming ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("order_id", "order")]
    [InlineData("id_cliente", "cliente")]
    [InlineData("customerId", "customer")]
    [InlineData("cod_cliente", "cliente")]
    [InlineData("codice_articolo", "articolo")]
    [InlineData("cliente_cod", "cliente")]
    public void StemOf_RecognizesEnglishAndItalianReferenceNames(string column, string stem)
    {
        ReferenceNaming.StemOf(column)!.Stem.Should().Be(stem);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("created_at")]
    [InlineData("valid")]
    public void StemOf_IgnoresNamesThatAreNotReferences(string column)
    {
        ReferenceNaming.StemOf(column).Should().BeNull();
    }

    [Theory]
    [InlineData("orders", "order")]
    [InlineData("categories", "category")]
    [InlineData("clienti", "cliente")]
    [InlineData("articoli", "articolo")]
    [InlineData("fatture", "fattura")]
    [InlineData("user", "user")]
    public void TableMatchesStem_HandlesEnglishAndItalianPlurals(string table, string stem)
    {
        ReferenceNaming.TableMatchesStem(table, stem).Should().BeTrue();
    }

    [Fact]
    public void TableMatchesStem_DoesNotMatchAMerePrefix()
    {
        // order_items is not the "order" entity.
        ReferenceNaming.TableMatchesStem("order_items", "order").Should().BeFalse();
    }

    // ── Candidates ─────────────────────────────────────────────────────────────

    [Fact]
    public void Candidate_IsFoundForAnUndeclaredReference()
    {
        var result = Analyze(Orders(), Shipments(Col("order_id", "bigint"))).Result;

        var signal = result.Signals.Should().ContainSingle().Subject;
        signal.RuleId.Should().Be(RelationshipRuleIds.UndeclaredForeignKey);
        signal.Evidence.Should().Be(Evidence.Inferred);
        signal.Metadata!["referencedTable"].Should().Be("public.orders");
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Candidate_IsNotSuggestedWhenTheKeyIsAlreadyDeclared()
    {
        var fk = Fk("public", "shipments", "order_id", "public", "orders", "id");
        var result = Analyze(
            Table("orders", [Col("id", "bigint")], incoming: [fk]),
            Table("shipments", [Col("id", "bigint"), Col("order_id", "bigint")], outgoing: [fk])).Result;

        result.Signals.Should().BeEmpty();
        result.Relationships.Should().ContainSingle(e => e.Evidence == RelationshipEvidence.Declared);
    }

    [Fact]
    public void Candidate_RequiresCompatibleTypes()
    {
        var result = Analyze(Orders(), Shipments(Col("order_id", "text"))).Result;

        result.Signals.Should().BeEmpty("text cannot join to a bigint key");
    }

    [Fact]
    public void Candidate_IsNotInventedWithoutAPlausibleParent()
    {
        var result = Analyze(Orders(), Shipments(Col("warehouse_id", "bigint"))).Result;

        result.Signals.Should().BeEmpty();
    }

    [Fact]
    public void Candidate_MatchingTwoParentsIsSkippedNotGuessed()
    {
        var a = Table("customer", [Col("id", "bigint")], schema: "sales");
        var b = Table("customers", [Col("id", "bigint")], schema: "crm");
        var result = Analyze(a, b, Shipments(Col("customer_id", "bigint"))).Result;

        result.Signals.Should().BeEmpty();
        result.Summary.AmbiguousCandidatesSkipped.Should().Be(1);
        result.Caveats.Should().Contain(c => c.Contains("rather than guessed"));
    }

    [Fact]
    public void Candidate_ColumnNamedLikeTheParentKey_IsTheStrongestBasis()
    {
        var customers = Table("customers", [Col("customer_id", "bigint")], pkColumns: ["customer_id"]);
        var result = Analyze(customers, Shipments(Col("customer_id", "bigint"))).Result;

        var signal = result.Signals.Should().ContainSingle().Subject;
        signal.Metadata!["basis"].Should().Be("same-name-as-parent-key");
        signal.Confidence.Should().BeGreaterThan(0.6);
    }

    [Fact]
    public void Candidate_ItalianCodeReferenceResolvesToAUniqueCodeKey()
    {
        var clienti = new TableDetail("public", "clienti", "table", null,
            [Col("id", "bigint"), Col("codice", "varchar")], new PrimaryKeyInfo("pk", ["id"]),
            [], [], [], [], [new UniqueConstraintDetail("uq_codice", ["codice"])]);
        var fatture = Table("fatture", [Col("id", "bigint"), Col("cod_cliente", "varchar")]);

        var signal = Analyze(clienti, fatture).Result.Signals.Should().ContainSingle().Subject;

        signal.Metadata!["referencedColumn"].Should().Be("codice");
        signal.Metadata["basis"].Should().Be("code-name-matches-table");
    }

    [Fact]
    public void CleanSchema_HasNoInferredReferences()
    {
        // Mirrors demo_good: every reference is declared, and owner_id has no "owners" table.
        var deptFk = Fk("demo_good", "employees", "department_id", "demo_good", "departments", "id");
        var empFk = Fk("demo_good", "projects", "owner_id", "demo_good", "employees", "id");

        var result = Analyze(
            Table("departments", [Col("id", "bigint"), Col("name")], schema: "demo_good", incoming: [deptFk]),
            Table("employees", [Col("id", "bigint"), Col("department_id", "bigint"), Col("name"), Col("email")],
                schema: "demo_good", outgoing: [deptFk], incoming: [empFk]),
            Table("projects", [Col("id", "bigint"), Col("owner_id", "bigint"), Col("name")],
                schema: "demo_good", outgoing: [empFk])).Result;

        result.Signals.Should().BeEmpty();
        result.Findings.Should().BeEmpty();
        result.Relationships.Should().OnlyContain(e => e.Evidence == RelationshipEvidence.Declared);
    }

    // ── The probe ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.SqlServer)]
    public void Probe_IsAggregateOnlyAndPassesTheGuard(DatabaseDialect dialect)
    {
        var analysis = Analyze(Orders(), Shipments(Col("order_id", "bigint")));
        var probe = RelationshipAnalysisEngine.BuildPlan(analysis, new Dictionary<(string, string), long?>(), dialect).Single();

        probe.Kind.Should().Be(VerificationProbeKind.InclusionDependency);
        probe.Sql.Should().Contain("compared_rows").And.Contain("divergent_rows").And.Contain("LEFT JOIN");
        probe.Sql.Should().NotContain(";");
        StatementGuard.IsReadOnlySelect(probe.Sql, dialect).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Probe_UsesCountBigOnSqlServer()
    {
        var analysis = Analyze(Orders(), Shipments(Col("order_id", "bigint")));
        var probe = RelationshipAnalysisEngine.BuildPlan(analysis, new Dictionary<(string, string), long?>(), DatabaseDialect.SqlServer).Single();

        probe.Sql.Should().Contain("COUNT_BIG(");
    }

    // ── Verification outcomes ──────────────────────────────────────────────────

    private static InferRelationshipsResult Verify(long compared, long orphans)
    {
        var analysis = Analyze(Orders(), Shipments(Col("order_id", "bigint")));
        var probe = RelationshipAnalysisEngine.BuildPlan(analysis, new Dictionary<(string, string), long?>(), DatabaseDialect.PostgreSql).Single();

        return RelationshipAnalysisEngine.ApplyVerification(
            analysis, [VerificationMeasurement.Measured(probe, compared, orphans, 1)], DatabaseDialect.PostgreSql);
    }

    [Fact]
    public void ZeroOrphans_ConfirmsTheHypothesis()
    {
        // The one probe kind where zero is the confirming count.
        var analysis = Analyze(Orders(), Shipments(Col("order_id", "bigint")));
        var probe = RelationshipAnalysisEngine.BuildPlan(analysis, new Dictionary<(string, string), long?>(), DatabaseDialect.PostgreSql).Single();

        VerificationMeasurement.Measured(probe, 40, 0, 1).Status.Should().Be(VerificationStatus.Confirmed);
        VerificationMeasurement.Measured(probe, 40, 3, 1).Status.Should().Be(VerificationStatus.Refuted);
    }

    [Fact]
    public void ZeroOrphans_BecomesAVerifiedMeasuredFindingWithAConstraintProposal()
    {
        var result = Verify(compared: 40, orphans: 0);

        var finding = result.Findings.Should().ContainSingle().Subject;
        finding.Evidence.Should().Be(Evidence.Measured);
        finding.Title.Should().Contain("all 40 non-null values exist");
        finding.SqlFix!.Kind.Should().Be(SqlProposalKind.Ddl);
        finding.SqlFix.Sql.Should().Contain("NOT VALID").And.Contain("VALIDATE CONSTRAINT");
        result.Relationships.Should().Contain(e => e.Evidence == RelationshipEvidence.Verified && e.OrphanRows == 0);
    }

    [Fact]
    public void ZeroOrphans_StillDoesNotClaimIntent()
    {
        var finding = Verify(compared: 40, orphans: 0).Findings.Single();

        finding.Description.Should().Contain("not proof of intent");
    }

    [Fact]
    public void MinorityOrphans_IsAReferenceWithOrphansNotARefutation()
    {
        var result = Verify(compared: 6, orphans: 1);

        var finding = result.Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(Severity.Medium);
        finding.Title.Should().Contain("1 of 6 values have no match");
        finding.SqlFix.Should().BeNull("a constraint cannot be added until the orphans are resolved");
        result.Relationships.Should().Contain(e => e.Evidence == RelationshipEvidence.VerifiedWithOrphans && e.OrphanRows == 1);
    }

    [Fact]
    public void MajorityOrphans_IsDroppedFromTheMap()
    {
        var result = Verify(compared: 6, orphans: 5);

        result.Findings.Should().ContainSingle().Which.Severity.Should().Be(Severity.Info);
        result.Relationships.Should().NotContain(e => e.FromTable == "public.shipments",
            "a path the data contradicts is not a usable join");
    }

    [Fact]
    public void NothingToCompare_StaysAnUnverifiedSignal()
    {
        var result = Verify(compared: 0, orphans: 0);

        result.Findings.Should().BeEmpty("an empty column neither confirms nor refutes a reference");
        result.Signals.Should().ContainSingle();
        result.Relationships.Should().Contain(e => e.Evidence == RelationshipEvidence.Inferred);
        result.Caveats.Should().Contain(c => c.Contains("neither confirms nor refutes"));
    }
}
