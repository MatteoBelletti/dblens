namespace DbLens.Core.Analysis;

public static class DiagnosticLimits
{
    /// <summary>Maximum number of findings returned by any diagnostic tool before truncation.</summary>
    public const int MaxFindingCount = 200;

    /// <summary>Maximum number of tables per get_table_details call.</summary>
    public const int MaxTablesPerRequest = 20;
}
