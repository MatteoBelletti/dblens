namespace DbLens.Tests;

public sealed class PostgresDenormalizationTests(PostgresFixture fixture)
    : DenormalizationTestsBase(fixture), IClassFixture<PostgresFixture>;
