namespace DbLens.Tests;

public sealed class PostgresDenormalizationVerificationTests(PostgresFixture fixture)
    : DenormalizationVerificationTestsBase(fixture), IClassFixture<PostgresFixture>;
