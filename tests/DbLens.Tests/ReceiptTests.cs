using DbLens.Core.Analysis.Receipts;
using FluentAssertions;

namespace DbLens.Tests;

public class ResultValueTests
{
    [Fact]
    public void ValuesJsonCannotCarry_BecomeText()
    {
        ResultValue.From(double.NaN).Should().Be("NaN");
        ResultValue.From(double.PositiveInfinity).Should().Be("Infinity");
        ResultValue.From(new[] { 1, 2 }).Should().Be("[1, 2]");
        ResultValue.From(1.5).Should().Be(1.5);
    }

    [Fact]
    public void Tolerance_IsReportedAsThePercentageItIs()
    {
        var a = new QueryResult(["t"], [[100.0m]], false, 0);
        var b = new QueryResult(["t"], [[100.05m]], false, 0);

        ResultComparer.Compare(a, b, 0.001).Explanation.Should().Contain("0.1 %");
    }
}

public class ResultComparerTests
{
    private static QueryResult Result(string[] columns, params object?[][] rows) =>
        new(columns, [.. rows.Select(r => (IReadOnlyList<object?>)r)], false, 0);

    [Fact]
    public void SameRowsInAnotherOrder_Agree()
    {
        var a = Result(["k", "n"], ["a", 1L], ["b", 2L]);
        var b = Result(["key", "total"], ["b", 2], ["a", 1]);

        ResultComparer.Compare(a, b, 0).Status.Should().Be(CrossCheckStatus.Agree);
    }

    [Fact]
    public void DecimalScale_DoesNotMatter()
    {
        ResultComparer.Compare(Result(["t"], [120.50m]), Result(["t"], [120.5m]), 0)
            .Status.Should().Be(CrossCheckStatus.Agree);
    }

    [Fact]
    public void DifferentValue_Disagrees_AndNamesBothFigures()
    {
        var (status, explanation) = ResultComparer.Compare(Result(["n"], [40L]), Result(["n"], [20L]), 0);

        status.Should().Be(CrossCheckStatus.Disagree);
        explanation.Should().Contain("40").And.Contain("20");
    }

    [Fact]
    public void Tolerance_AbsorbsRoundingOnly()
    {
        ResultComparer.Compare(Result(["n"], [100.0]), Result(["n"], [100.05]), 0.001)
            .Status.Should().Be(CrossCheckStatus.Agree);
        ResultComparer.Compare(Result(["n"], [100.0]), Result(["n"], [101.0]), 0.001)
            .Status.Should().Be(CrossCheckStatus.Disagree);
    }

    [Fact]
    public void DifferentShapes_AreNotComparable()
    {
        ResultComparer.Compare(Result(["a"], [1]), Result(["a", "b"], [1, 2]), 0)
            .Status.Should().Be(CrossCheckStatus.NotComparable);
    }

    [Fact]
    public void TruncatedResult_IsNeverReportedAsAgreeing()
    {
        var truncated = new QueryResult(["n"], [[1]], Truncated: true, 0);

        ResultComparer.Compare(truncated, truncated, 0).Status.Should().Be(CrossCheckStatus.NotComparable);
    }

    [Fact]
    public void NullsMatchOnlyNulls()
    {
        ResultComparer.Compare(Result(["n"], [(object?)null]), Result(["n"], [(object?)null]), 0).Status.Should().Be(CrossCheckStatus.Agree);
        ResultComparer.Compare(Result(["n"], [(object?)null]), Result(["n"], [0]), 0).Status.Should().Be(CrossCheckStatus.Disagree);
    }

    [Fact]
    public void Values_AreMadeJsonSafeWithoutChangingThem()
    {
        ResultValue.From(DBNull.Value).Should().BeNull();
        ResultValue.From(new byte[3]).Should().Be("<3 bytes>");
        ResultValue.From(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)).Should().Be("2026-01-02T03:04:05.0000000Z");
        ResultValue.From(12.5m).Should().Be(12.5m);
    }
}
