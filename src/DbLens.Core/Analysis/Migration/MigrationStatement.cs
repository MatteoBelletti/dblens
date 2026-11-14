namespace DbLens.Core.Analysis.Migration;

public enum MigrationOperationKind
{
    /// <summary>The statement shape was not recognized; no lock claim is made about it.</summary>
    Unknown,
    CreateTable,
    DropTable,
    TruncateTable,
    DropSchema,
    AddColumn,
    DropColumn,
    AlterColumnType,
    SetNotNull,
    DropNotNull,
    SetColumnDefault,
    RenameColumn,
    RenameTable,
    CreateIndex,
    DropIndex,
    AddForeignKey,
    AddCheckConstraint,
    AddPrimaryKey,
    AddUniqueConstraint,
    DropConstraint,
    ValidateConstraint,
}

/// <summary>
/// One DDL statement, classified by shape. This is a shape matcher, not a SQL parser:
/// <see cref="MigrationOperationKind.Unknown"/> means "not recognized", never "safe".
/// </summary>
public sealed record MigrationStatement(
    string RawSql,
    MigrationOperationKind Kind,
    string? Schema,
    string? Table,
    /// <summary>Index, constraint or column name the statement targets, when identifiable.</summary>
    string? ObjectName,
    /// <summary>CONCURRENTLY (PostgreSQL) or WITH (ONLINE = ON) (SQL Server).</summary>
    bool IsConcurrent,
    /// <summary>NOT VALID (PostgreSQL) — defers the validating scan.</summary>
    bool IsNotValid,
    /// <summary>The added column carries a DEFAULT.</summary>
    bool HasDefault,
    /// <summary>The added column is declared NOT NULL.</summary>
    bool IsNotNull,
    /// <summary>The DEFAULT expression is non-constant (function call), which forces a rewrite.</summary>
    bool HasVolatileDefault
);
