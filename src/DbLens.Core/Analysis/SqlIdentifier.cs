using DbLens.Core.Models;

namespace DbLens.Core.Analysis;

/// <summary>
/// Quotes catalog names for generated SQL. An unquoted name is a correctness bug (a mixed-case
/// PostgreSQL identifier folds to lower case) and an injection vector (whoever can create a table
/// chooses its name). Quoted, a name is also inert to <see cref="QueryPlan.StatementGuard"/>,
/// which never reads inside quoted identifiers.
/// </summary>
public static class SqlIdentifier
{
    /// <summary>Doubles the closing delimiter, so the name cannot end the quoted region early.</summary>
    public static string Quote(string identifier, DatabaseDialect dialect) =>
        dialect == DatabaseDialect.PostgreSql
            ? $"\"{identifier.Replace("\"", "\"\"")}\""
            : $"[{identifier.Replace("]", "]]")}]";

    public static string Qualify(string schema, string name, DatabaseDialect dialect) =>
        $"{Quote(schema, dialect)}.{Quote(name, dialect)}";
}
