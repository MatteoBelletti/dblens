namespace DbLens.Core.Analysis.Gdpr;

public sealed record PiiMatch(
    string ColumnName,
    PiiCategory Category,
    string PatternName,
    double Confidence);
