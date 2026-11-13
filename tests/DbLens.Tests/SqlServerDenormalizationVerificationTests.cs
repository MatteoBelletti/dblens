using FluentAssertions;

namespace DbLens.Tests;

public sealed class SqlServerDenormalizationVerificationTests(SqlServerFixture fixture)
    : DenormalizationVerificationTestsBase(fixture), IClassFixture<SqlServerFixture>
{
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ReadUncommittedTradeOff_IsDeclaredInCaveats()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await VerifyAsync();

        // The dialect asymmetry must be stated, not left for someone to discover.
        result.Caveats.Should().Contain(c => c.Contains("READ UNCOMMITTED"));
        result.Caveats.Should().Contain(c => c.Contains("allow-list"));
    }
}
