using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Indexes;

/// <summary>
/// The index DDL every rule proposes. Identifiers are quoted, so a proposal copied by a user
/// targets the object it names even when that name is mixed-case or a reserved word.
/// </summary>
public static class IndexSql
{
    /// <summary>
    /// CONCURRENTLY on PostgreSQL avoids the ACCESS EXCLUSIVE lock a plain DROP INDEX takes.
    /// </summary>
    public static string DropIndex(string schema, string table, string index, DatabaseDialect dialect) =>
        dialect == DatabaseDialect.PostgreSql
            ? $"DROP INDEX CONCURRENTLY {SqlIdentifier.Qualify(schema, index, dialect)};"
            : $"DROP INDEX {SqlIdentifier.Quote(index, dialect)} ON {SqlIdentifier.Qualify(schema, table, dialect)};";

    /// <summary>
    /// Built without blocking writes — CONCURRENTLY on PostgreSQL, ONLINE = ON on SQL Server — so a
    /// proposal passes dblens's own check_migration_safety. ONLINE needs Enterprise or Developer
    /// edition; on Standard the option has to be dropped and the build scheduled.
    /// </summary>
    public static string CreateIndex(
        string schema, string table, IReadOnlyList<string> columns, DatabaseDialect dialect)
    {
        var name = SqlIdentifier.Quote($"idx_{table}_{string.Join("_", columns)}", dialect);
        var target = SqlIdentifier.Qualify(schema, table, dialect);
        var columnList = string.Join(", ", columns.Select(c => SqlIdentifier.Quote(c, dialect)));

        return dialect == DatabaseDialect.PostgreSql
            ? $"CREATE INDEX CONCURRENTLY {name} ON {target} ({columnList});"
            : $"CREATE INDEX {name} ON {target} ({columnList}) WITH (ONLINE = ON);";
    }

    /// <summary>
    /// REINDEX CONCURRENTLY on PostgreSQL; on SQL Server a rebuild when heavily fragmented and a
    /// reorganize — online and interruptible — otherwise.
    /// </summary>
    public static string Defragment(string schema, string table, string index, bool rebuild, DatabaseDialect dialect)
    {
        if (dialect == DatabaseDialect.PostgreSql)
            return $"REINDEX INDEX CONCURRENTLY {SqlIdentifier.Qualify(schema, index, dialect)};";

        var target = $"{SqlIdentifier.Quote(index, dialect)} ON {SqlIdentifier.Qualify(schema, table, dialect)}";
        return rebuild
            ? $"ALTER INDEX {target} REBUILD WITH (ONLINE = ON);"
            : $"ALTER INDEX {target} REORGANIZE;";
    }
}
