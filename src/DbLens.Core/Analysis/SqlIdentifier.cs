using DbLens.Core.Models;

namespace DbLens.Core.Analysis;

/// <summary>
/// Renders catalog identifiers into SQL text safely.
///
/// Every rule that emits SQL builds it from schema, table and column names read out of the
/// catalog. Interpolating those unquoted is both a correctness bug (a mixed-case PostgreSQL
/// identifier folds to lower case and targets the wrong object, or nothing) and an injection
/// vector, since an identifier is attacker-influenced whenever the attacker can create a table.
///
/// Quoting also composes with <see cref="QueryPlan.StatementGuard"/>: that guard blanks the
/// contents of quoted identifiers before scanning for forbidden keywords, so a column literally
/// named "delete" or "into" cannot trip the allow-list, and a hostile identifier cannot smuggle
/// in a keyword or a statement separator. Quoting is what makes the guard's allow-list usable on
/// generated SQL rather than merely conservative.
/// </summary>
public static class SqlIdentifier
{
    /// <summary>
    /// Quotes one identifier for the dialect, doubling the closing delimiter so it cannot
    /// terminate the quoted region early.
    /// </summary>
    public static string Quote(string identifier, DatabaseDialect dialect)
    {
        return dialect == DatabaseDialect.PostgreSql
            ? $"\"{identifier.Replace("\"", "\"\"")}\""
            : $"[{identifier.Replace("]", "]]")}]";
    }

    /// <summary>Renders <c>schema.name</c> with both parts quoted.</summary>
    public static string Qualify(string schema, string name, DatabaseDialect dialect) =>
        $"{Quote(schema, dialect)}.{Quote(name, dialect)}";


}
