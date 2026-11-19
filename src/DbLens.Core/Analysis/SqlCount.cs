using DbLens.Core.Models;

namespace DbLens.Core.Analysis;

/// <summary>A row count in generated SQL: SQL Server's COUNT is int, so its probes use COUNT_BIG.</summary>
public static class SqlCount
{
    public static string Of(DatabaseDialect dialect, string expression = "*") =>
        $"{(dialect == DatabaseDialect.SqlServer ? "COUNT_BIG" : "COUNT")}({expression})";
}
