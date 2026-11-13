namespace DbLens.Tests;

public sealed class SqlServerDenormalizationTests(SqlServerFixture fixture)
    : DenormalizationTestsBase(fixture), IClassFixture<SqlServerFixture>;
