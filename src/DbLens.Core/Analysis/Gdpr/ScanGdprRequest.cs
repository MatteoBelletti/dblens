namespace DbLens.Core.Analysis.Gdpr;

public sealed record ScanGdprRequest(
    string? Schema = null,
    string[]? Tables = null,
    string[]? ExcludeTables = null,
    double MinConfidence = 0.5);
