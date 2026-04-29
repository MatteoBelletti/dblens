using DbLens.Core.Analysis.Gdpr;
using FluentAssertions;

namespace DbLens.Tests;

public sealed class PiiClassifierTests
{
    // --- exact matches ---

    [Theory]
    [InlineData("email",            "text",          PiiCategory.Contact)]
    [InlineData("e_mail",           "varchar",       PiiCategory.Contact)]
    [InlineData("email_address",    "nvarchar",      PiiCategory.Contact)]
    [InlineData("first_name",       "text",          PiiCategory.DirectIdentifier)]
    [InlineData("firstname",        "varchar",       PiiCategory.DirectIdentifier)]
    [InlineData("last_name",        "text",          PiiCategory.DirectIdentifier)]
    [InlineData("surname",          "text",          PiiCategory.DirectIdentifier)]
    [InlineData("date_of_birth",    "date",          PiiCategory.DirectIdentifier)]
    [InlineData("dob",              "date",          PiiCategory.DirectIdentifier)]
    [InlineData("gender",           "text",          PiiCategory.DirectIdentifier)]
    [InlineData("phone_number",     "varchar",       PiiCategory.Contact)]
    [InlineData("mobile",           "varchar",       PiiCategory.Contact)]
    [InlineData("address",          "text",          PiiCategory.Contact)]
    [InlineData("ssn",              "varchar",       PiiCategory.NationalId)]
    [InlineData("passport",         "varchar",       PiiCategory.NationalId)]
    [InlineData("credit_card",      "varchar",       PiiCategory.Financial)]
    [InlineData("iban",             "varchar",       PiiCategory.Financial)]
    [InlineData("cvv",              "varchar",       PiiCategory.Financial)]
    [InlineData("diagnosis",        "text",          PiiCategory.SpecialCategory)]
    [InlineData("religion",         "text",          PiiCategory.SpecialCategory)]
    [InlineData("ip_address",       "varchar",       PiiCategory.OnlineId)]
    [InlineData("session_id",       "varchar",       PiiCategory.OnlineId)]
    [InlineData("latitude",         "float",         PiiCategory.Location)]
    [InlineData("longitude",        "double",        PiiCategory.Location)]
    public void Classify_KnownPiiColumn_ReturnsExpectedCategory(
        string column, string sqlType, PiiCategory expectedCategory)
    {
        var match = PiiClassifier.Classify(column, sqlType);
        match.Should().NotBeNull($"'{column}' should be detected as PII");
        match!.Category.Should().Be(expectedCategory);
    }

    // --- Italian patterns ---

    [Theory]
    [InlineData("codice_fiscale",  "varchar", PiiCategory.NationalId)]
    [InlineData("codicefiscale",   "varchar", PiiCategory.NationalId)]
    [InlineData("partita_iva",     "varchar", PiiCategory.NationalId)]
    [InlineData("cognome",         "text",    PiiCategory.DirectIdentifier)]
    [InlineData("nome",            "text",    PiiCategory.DirectIdentifier)]
    [InlineData("data_nascita",    "date",    PiiCategory.DirectIdentifier)]
    [InlineData("telefono",        "varchar", PiiCategory.Contact)]
    [InlineData("diagnosi",        "text",    PiiCategory.SpecialCategory)]
    [InlineData("latitudine",      "decimal", PiiCategory.Location)]
    public void Classify_ItalianPiiColumn_ReturnsExpectedCategory(
        string column, string sqlType, PiiCategory expectedCategory)
    {
        var match = PiiClassifier.Classify(column, sqlType);
        match.Should().NotBeNull($"Italian column '{column}' should be detected as PII");
        match!.Category.Should().Be(expectedCategory);
    }

    // --- non-PII columns ---

    [Theory]
    [InlineData("id",         "int")]
    [InlineData("created_at", "timestamp")]
    [InlineData("updated_at", "timestamp")]
    [InlineData("status",     "varchar")]
    [InlineData("amount",     "decimal")]
    [InlineData("quantity",   "int")]
    [InlineData("price",      "decimal")]
    [InlineData("notes",      "text")]
    [InlineData("description","text")]
    public void Classify_NonPiiColumn_ReturnsNull(string column, string sqlType)
    {
        var match = PiiClassifier.Classify(column, sqlType);
        match.Should().BeNull($"'{column}' should not be detected as PII");
    }

    // --- minConfidence filter ---

    [Fact]
    public void Classify_AgeLowConfidence_AboveDefaultThreshold_ReturnsMatch()
    {
        // "age" has confidence 0.65, default minConfidence = 0.5
        var match = PiiClassifier.Classify("age", "int", minConfidence: 0.5);
        match.Should().NotBeNull();
    }

    [Fact]
    public void Classify_AgeLowConfidence_BelowHighThreshold_ReturnsNull()
    {
        // "age" has confidence 0.65, raising threshold above that filters it out
        var match = PiiClassifier.Classify("age", "int", minConfidence: 0.70);
        match.Should().BeNull();
    }

    // --- type filtering ---

    [Fact]
    public void Classify_LatitudeWithTextType_ReturnsNull()
    {
        // latitude pattern restricts to numeric types
        var match = PiiClassifier.Classify("latitude", "text");
        match.Should().BeNull("latitude with text type should not match — likely a label, not a coordinate");
    }

    [Fact]
    public void Classify_LatitudeWithFloatType_ReturnsMatch()
    {
        var match = PiiClassifier.Classify("latitude", "float");
        match.Should().NotBeNull();
        match!.Category.Should().Be(PiiCategory.Location);
    }

    // --- case insensitivity ---

    [Fact]
    public void Classify_UpperCaseColumnName_IsMatchedCaseInsensitively()
    {
        var match = PiiClassifier.Classify("EMAIL_ADDRESS", "varchar");
        match.Should().NotBeNull();
        match!.Category.Should().Be(PiiCategory.Contact);
    }

    // --- confidence is propagated ---

    [Fact]
    public void Classify_HighConfidencePattern_ReturnsExpectedConfidence()
    {
        var match = PiiClassifier.Classify("iban", "varchar");
        match.Should().NotBeNull();
        match!.Confidence.Should().BeGreaterThan(0.9);
    }
}
