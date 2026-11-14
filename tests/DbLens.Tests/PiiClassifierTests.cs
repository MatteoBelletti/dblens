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

    // --- naming styles ---

    [Theory]
    [InlineData("First Name",    "text",    PiiCategory.DirectIdentifier)]
    [InlineData("firstName",     "text",    PiiCategory.DirectIdentifier)]
    [InlineData("DateOfBirth",   "date",    PiiCategory.DirectIdentifier)]
    [InlineData("E-Mail",        "varchar", PiiCategory.Contact)]
    [InlineData("IPAddress",     "varchar", PiiCategory.OnlineId)]
    [InlineData("phone1",        "varchar", PiiCategory.Contact)]
    [InlineData("PHONE_2",       "varchar", PiiCategory.Contact)]
    [InlineData("email",         "citext",  PiiCategory.Contact)]
    public void Classify_ReadsTheNameWhateverItsStyle(string column, string sqlType, PiiCategory expected)
    {
        var match = PiiClassifier.Classify(column, sqlType);

        match.Should().NotBeNull();
        match!.Category.Should().Be(expected);
        match.Qualified.Should().BeFalse();
    }

    // --- qualified names ---

    [Theory]
    [InlineData("customer_email",         "text",    PiiCategory.Contact)]
    [InlineData("DeliveryAddress2",       "text",    PiiCategory.Contact)]
    [InlineData("address_line1",          "text",    PiiCategory.Contact)]
    [InlineData("user_ssn",               "varchar", PiiCategory.NationalId)]
    [InlineData("card_number_last4",      "varchar", PiiCategory.Financial)]
    [InlineData("email_cliente",          "text",    PiiCategory.Contact)]
    [InlineData("telefono_casa",          "varchar", PiiCategory.Contact)]
    [InlineData("indirizzo_spedizione",   "text",    PiiCategory.Contact)]
    [InlineData("codice_fiscale_cliente", "varchar", PiiCategory.NationalId)]
    public void Classify_FindsAnUnambiguousPatternInsideALongerName_AtLowerConfidence(
        string column, string sqlType, PiiCategory expected)
    {
        var match = PiiClassifier.Classify(column, sqlType);

        match.Should().NotBeNull();
        match!.Category.Should().Be(expected);
        match.Qualified.Should().BeTrue();
        match.Confidence.Should().BeLessThan(0.9);
    }

    [Theory]
    [InlineData("email_verified",   "text")]     // about the value, not the value
    [InlineData("email_sent_at",    "text")]
    [InlineData("is_email",         "text")]
    [InlineData("address_id",       "int")]
    [InlineData("zip_file",         "text")]
    [InlineData("email_opt_in",     "boolean")]  // the pattern needs a text type
    [InlineData("via_api",          "text")]     // "via", "pin", "name" are too common to trust in a compound
    [InlineData("pin_code",         "text")]
    [InlineData("health_check_url", "text")]
    [InlineData("product_name",     "text")]
    [InlineData("nome_prodotto",    "text")]
    [InlineData("last_login",       "timestamp")]
    [InlineData("long_description", "text")]
    public void Classify_DoesNotReadAmbiguousOrDerivedCompounds(string column, string sqlType)
    {
        PiiClassifier.Classify(column, sqlType).Should().BeNull();
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
