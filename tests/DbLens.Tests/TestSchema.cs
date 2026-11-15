using DbLens.Core.Models;

namespace DbLens.Tests;

/// <summary>
/// Builders for in-memory catalog metadata, shared by every engine unit test.
/// Import with <c>using static DbLens.Tests.TestSchema;</c>.
/// </summary>
public static class TestSchema
{
    public static ColumnDetail Col(
        string name,
        string dataType = "text",
        bool nullable = true,
        bool identity = false,
        string? fullType = null) =>
        new(
            Name: name,
            OrdinalPosition: 0,
            DataType: dataType,
            FullDataType: fullType ?? dataType,
            IsNullable: nullable,
            DefaultExpression: null,
            CharacterMaxLength: null,
            NumericPrecision: null,
            NumericScale: null,
            IsIdentity: identity,
            Comment: null);

    public static ForeignKeyDetail Fk(
        string fromSchema, string fromTable, string fromColumn,
        string toSchema, string toTable, string toColumn,
        string constraint = "fk_test") =>
        new(constraint, fromSchema, fromTable, [fromColumn], toSchema, toTable, [toColumn],
            "NO ACTION", "NO ACTION");

    /// <param name="pkColumns">Primary-key columns. Defaults to a single "id"; pass [] for none.</param>
    public static TableDetail Table(
        string name,
        IEnumerable<ColumnDetail> columns,
        string schema = "public",
        IEnumerable<ForeignKeyDetail>? outgoing = null,
        IEnumerable<ForeignKeyDetail>? incoming = null,
        IReadOnlyList<string>? pkColumns = null,
        string kind = "table")
    {
        var pk = pkColumns ?? ["id"];

        return new TableDetail(
            Schema: schema,
            Name: name,
            Kind: kind,
            Comment: null,
            Columns: [.. columns],
            PrimaryKey: pk.Count == 0 ? null : new PrimaryKeyInfo($"pk_{name}", pk),
            Indexes: [],
            ForeignKeysOutgoing: outgoing?.ToList() ?? [],
            ForeignKeysIncoming: incoming?.ToList() ?? [],
            CheckConstraints: [],
            UniqueConstraints: []);
    }
}
