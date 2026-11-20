using DbLens.Core.Models;
using static DbLens.Core.Analysis.Names;

namespace DbLens.Core.Analysis.EfCore;

/// <summary>
/// What the database holds that the EF model does not know, and the reverse. Pure. Every finding
/// is derived from the catalog and the snapshot; each names the entity and property to touch in
/// <c>Metadata["ef"]</c>. Index filters and defaults are not compared here.
/// </summary>
public static partial class EfDriftEngine
{
    /// <param name="applied">The applied migration ids; null when no history table was found.</param>
    /// <param name="historyTable">The history table read, as a catalog pair.</param>
    /// <param name="schemas">The schemas compared: a table there that no entity maps is reported.</param>
    /// <param name="historyCaveat">What was found of the history table and not read, or which one was read.</param>
    public static CompareEfModelResult Analyze(
        EfModel model,
        EfModelMatch match,
        IReadOnlyList<string>? applied,
        (string Schema, string Name)? historyTable,
        IReadOnlySet<string> schemas,
        string? historyCaveat = null)
    {
        List<Finding> findings = [];
        List<string> caveats = [.. model.Unsupported.Select(u => u.Reason), .. match.Ambiguities];

        var history = historyTable is { } h ? $"{h.Schema}.{h.Name}" : null;
        if (historyCaveat is not null) caveats.Add(historyCaveat);
        var migrations = Migrations(model, applied, history, historyCaveat is not null, findings, caveats);

        foreach (var table in match.Tables.Where(t => !t.Ambiguous))
            CompareTable(table, match, findings);

        foreach (var table in match.UnmappedTables.Where(t =>
                     schemas.Contains(t.Schema)
                     && !(historyTable is { } ht && Same(t.Schema, ht.Schema) && Same(t.Name, ht.Name))
                     && !model.OwnTables.Any(o => Same(o.Name, t.Name) && (o.Schema is null || Same(o.Schema, t.Schema)))))
            findings.Add(Unmapped(table));

        var differences = findings.Count;
        var split = FindingSplit.Partition(findings).WithMandatory(Summary(model, match, migrations, differences));

        if (split.TruncationCaveat() is { } truncation) caveats.Add(truncation);

        return new CompareEfModelResult(
            split.Findings,
            split.Signals,
            new CompareEfModelSummary(
                split.Findings.Count, split.Signals.Count, model.Entities.Count, match.Tables.Count,
                split.BySeverity(), split.ByRule(EfRuleIds.All)),
            migrations,
            caveats,
            split.Truncated);
    }

    // ── Migrations ─────────────────────────────────────────────────────────────

    private static EfMigrationState Migrations(
        EfModel model, IReadOnlyList<string>? applied, string? historyTable, bool explained, List<Finding> findings, List<string> caveats)
    {
        if (applied is null)
        {
            if (!explained)
                caveats.Add(
                "No migrations history table was found, so which migrations are applied is unknown: the database " +
                "was created another way (EnsureCreated, a script, another tool) or the history lives under another " +
                "name (pass historyTable and historySchema). The schema comparison below does not depend on it.");
            return new EfMigrationState(false, null, null, [], [], [], [], []);
        }

        var code = model.MigrationIds;
        var inDatabase = new HashSet<string>(applied, StringComparer.Ordinal);
        var others = (model.OtherContexts ?? new Dictionary<string, IReadOnlyList<string>>())
            .SelectMany(c => c.Value.Select(id => (Id: id, Context: c.Key)))
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Context, StringComparer.Ordinal);

        var lastIndex = code.Select((id, i) => (id, i)).LastOrDefault(x => inDatabase.Contains(x.id), (null!, -1)).i;
        var lastApplied = lastIndex >= 0 ? code[lastIndex] : null;
        var pending = code.Where(id => !inDatabase.Contains(id)).ToList();
        var outOfOrder = code.Take(Math.Max(lastIndex, 0)).Where(id => !inDatabase.Contains(id)).ToList();
        var notInCode = applied.Where(id => !code.Contains(id, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToList();
        var otherContexts = notInCode.Where(others.ContainsKey).ToList();
        var unknown = notInCode.Where(id => !others.ContainsKey(id)).ToList();

        if (otherContexts.Count > 0)
            caveats.Add(
                $"{Num.N0(otherContexts.Count)} applied migration(s) belong to other DbContexts of the project " +
                $"({string.Join(", ", otherContexts.Select(id => others[id]).Distinct())}), which share {historyTable}; they were set aside.");

        foreach (var id in unknown)
            findings.Add(new Finding(
                EfRuleIds.MigrationUnknown, Severity.High, FindingCategory.Structure, 1.0, Evidence.Derived,
                historyTable!, null,
                $"Migration '{id}' is applied in the database but not in the code",
                $"{historyTable} records '{id}', which no migration of {model.ContextName} declares, nor any other DbContext " +
                "found beside it. It came from another branch, or was applied by hand as a hotfix: the next migration " +
                "generated from this code does not know what it changed. Merge that migration into the code before " +
                "generating a new one. If a DbContext whose migrations live elsewhere shares this history table, its " +
                "migrations appear here too.",
                null,
                new Dictionary<string, object> { ["migration"] = id }));

        var commands = ScriptCommands(model, inDatabase);
        if (pending.Count > 0)
        {
            var outOfOrderText = outOfOrder.Count == 0
                ? ""
                : $" {string.Join(", ", outOfOrder)} {(outOfOrder.Count == 1 ? "is" : "are")} older than the last applied " +
                  $"migration, {lastApplied}: `dotnet ef database update` applies {(outOfOrder.Count == 1 ? "it" : "them")}, " +
                  $"but a script from {lastApplied} leaves {(outOfOrder.Count == 1 ? "it" : "them")} out, so each run of " +
                  "pending migrations is scripted on its own.";

            findings.Add(new Finding(
                EfRuleIds.MigrationPending, outOfOrder.Count > 0 ? Severity.High : Severity.Medium,
                FindingCategory.Structure, 1.0, Evidence.Derived,
                historyTable!, null,
                $"{Num.N0(pending.Count)} migration(s) in the code are not applied" +
                (outOfOrder.Count > 0 ? $", {Num.N0(outOfOrder.Count)} of them older than the last applied" : "") +
                $": {pending[0]}" + (pending.Count > 1 ? $" … {pending[^1]}" : ""),
                $"The database's last applied migration is {lastApplied ?? "none"}.{outOfOrderText} The differences below " +
                "that these migrations would create are expected until they run. Write their SQL with " +
                string.Join(" and ", commands.Select(c => $"`{c}`")) + ", and check it with check_migration_safety before applying it.",
                null,
                new Dictionary<string, object> { ["pending"] = pending, ["outOfOrder"] = outOfOrder, ["lastApplied"] = lastApplied ?? "" }));
        }

        return new EfMigrationState(true, historyTable, lastApplied, pending, outOfOrder, unknown, otherContexts, commands);
    }

    /// <summary>
    /// One `migrations script FROM TO` per run of consecutive pending migrations: FROM is the migration
    /// before the run (0 = the start), TO the run's last, so nothing applied is scripted again.
    /// </summary>
    private static List<string> ScriptCommands(EfModel model, HashSet<string> inDatabase)
    {
        List<string> commands = [];
        var code = model.MigrationIds;

        for (var i = 0; i < code.Count; i++)
        {
            if (inDatabase.Contains(code[i])) continue;

            var from = i == 0 ? "0" : code[i - 1];
            while (i + 1 < code.Count && !inDatabase.Contains(code[i + 1])) i++;
            commands.Add($"dotnet ef migrations script {from} {code[i]} --context {model.ContextName} -o pending-{commands.Count + 1}.sql");
        }

        if (commands.Count == 1) commands[0] = commands[0].Replace("-o pending-1.sql", "-o pending.sql", StringComparison.Ordinal);
        return commands;
    }

    // ── Tables, columns, indexes, foreign keys ─────────────────────────────────

    private static void CompareTable(EfTableMatch table, EfModelMatch match, List<Finding> findings)
    {
        var owner = table.Owner;

        if (table.Table is not { } detail)
        {
            findings.Add(new Finding(
                EfRuleIds.TableMissing, Severity.High, FindingCategory.Structure, 1.0, Evidence.Derived,
                table.Qualified, null,
                $"Table {table.Qualified} of entity {Short(owner.Name)} does not exist",
                $"The model maps {string.Join(", ", table.Entities.Select(e => Short(e.Name)))} to {table.Qualified}, which " +
                "the database does not have. A query on the entity fails at run time.",
                null,
                Ef(owner.Name)));
            return;
        }

        foreach (var column in table.Columns.Where(c => c.Column is null && !c.Ambiguous))
            findings.Add(new Finding(
                EfRuleIds.ColumnMissing, Severity.High, FindingCategory.Structure, 1.0, Evidence.Derived,
                table.Qualified, [column.Property.Column],
                $"Column {column.Property.Column} of {Short(column.Entity.Name)}.{column.Property.Name} does not exist in {table.Qualified}",
                $"The model maps {Short(column.Entity.Name)}.{column.Property.Name} to the column {column.Property.Column}, which " +
                $"{table.Qualified} does not have. Every query that loads {Short(column.Entity.Name)} fails until a migration adds " +
                "it, or the model maps the property to the column that holds it (HasColumnName).",
                null,
                Ef(column.Entity.Name, column.Property.Name)));

        // A column the model computes takes its type and nullability from its expression. TPH subtypes and
        // owned types can map one column more than once: mappings that expect the same are compared once.
        foreach (var column in table.Columns
                     .Where(c => c.Column is not null && !c.Ambiguous && !c.Property.IsComputed)
                     .DistinctBy(c => (c.Column!.Name, c.Property.ColumnType is { } t ? NormalType(t) : null, c.Property.IsNullable)))
            CompareColumn(table, column, findings);

        List<IReadOnlyList<string>> modelIndexes = [];
        foreach (var entity in table.Entities)
            foreach (var index in entity.Indexes)
            {
                if (EfModelMatcher.ColumnsOf(table, entity.Name, index.Properties) is not { } columns) continue;
                modelIndexes.Add(columns);

                if (detail.PrimaryKey is { } pk && SameColumns(pk.Columns, columns)) continue;
                if (detail.UniqueConstraints.FirstOrDefault(u => SameColumns(u.Columns, columns)) is { } constraint)
                {
                    if (!index.IsUnique) findings.Add(Uniqueness(table, entity, index, constraint.Name, columns));
                    continue;
                }
                var existing = detail.Indexes.Where(i => SameColumns(i.Columns, columns)).ToList();
                if (existing.Count > 0)
                {
                    if (!existing.Any(i => i.IsUnique == index.IsUnique))
                        findings.Add(Uniqueness(table, entity, index, existing[0].Name, columns));
                    continue;
                }

                findings.Add(new Finding(
                    EfRuleIds.IndexMissing, Severity.High, FindingCategory.Performance, 1.0, Evidence.Derived,
                    table.Qualified, columns,
                    $"Index {index.Name ?? "on (" + string.Join(", ", columns) + ")"} of {Short(entity.Name)} does not exist",
                    $"The model declares an index on {Short(entity.Name)}({string.Join(", ", index.Properties)}); " +
                    $"{table.Qualified} has none on ({string.Join(", ", columns)}). Queries the model expects to be indexed " +
                    "read the whole table.",
                    null,
                    Ef(entity.Name, fluent: $"b.HasIndex({Lambda(index.Properties)})")));
            }

        foreach (var index in detail.Indexes.Where(i => !i.IsPrimary
                     && !modelIndexes.Any(m => SameColumns(i.Columns, m))
                     && !(detail.PrimaryKey is { } pk && SameColumns(pk.Columns, i.Columns))))
        {
            var properties = index.Columns
                .Select(c => table.Columns.FirstOrDefault(m => m.Column is not null && Same(m.Column.Name, c))?.Property.Name)
                .ToList();
            var fluent = properties.All(p => p is not null)
                ? $"b.HasIndex({Lambda(properties!)}){(index.IsUnique ? ".IsUnique()" : "")}.HasDatabaseName(\"{index.Name}\")"
                : null;

            findings.Add(new Finding(
                EfRuleIds.IndexUnmodeled, Severity.Medium, FindingCategory.Structure, 1.0, Evidence.Derived,
                table.Qualified, index.Columns,
                $"Index {index.Name} on {table.Qualified} is not in the model",
                $"{index.Name} ({string.Join(", ", index.Columns)}) was added by hand. Declare it now with its database name " +
                "(Metadata.ef); declared later, the generated migration's CreateIndex fails where it already exists: remove " +
                "that call, or make it conditional with migrationBuilder.Sql.",
                null,
                Ef(owner.Name, fluent: fluent)));
        }

        foreach (var entity in table.Entities)
            foreach (var key in entity.ForeignKeys)
                CompareForeignKey(table, detail, entity, key, match, findings);
    }

    /// <summary>The column the model expects against the one the database has: store type and nullability.</summary>
    private static void CompareColumn(EfTableMatch table, EfColumnMatch column, List<Finding> findings)
    {
        var (property, actual, entity) = (column.Property, column.Column!, column.Entity);
        var where = $"{table.Qualified}.{actual.Name}";

        if (property.ColumnType is { } expected && !SameStoreType(expected, actual.FullDataType))
            findings.Add(new Finding(
                EfRuleIds.ColumnType, Severity.Medium, FindingCategory.Structure, 1.0, Evidence.Derived,
                table.Qualified, [actual.Name],
                $"Column {where} is {actual.FullDataType}; the model expects {expected}",
                $"{Short(entity.Name)}.{property.Name} is mapped to {expected}. A shorter or narrower column truncates or " +
                "rejects what the code writes; a wider one hides a migration that never ran. EF compares the model with " +
                "its snapshot, never with the database, so no migration will correct it: alter the column by hand, or " +
                "map the property to the database's type if the database is right.",
                null,
                Ef(entity.Name, property.Name, $"b.Property(e => e.{property.Name}).HasColumnType(\"{actual.FullDataType}\")")));

        // A snapshot writes no IsRequired() for a key property: a string key reads as nullable, and is not.
        var nullable = property.IsNullable && !entity.Key.Contains(property.Name);
        if (nullable != actual.IsNullable)
            findings.Add(new Finding(
                EfRuleIds.ColumnNullability, Severity.Medium, FindingCategory.Structure, 1.0, Evidence.Derived,
                table.Qualified, [actual.Name],
                $"Column {where} is {(actual.IsNullable ? "nullable" : "NOT NULL")}; the model says {(nullable ? "nullable" : "required")}",
                actual.IsNullable
                    ? $"The model treats {Short(entity.Name)}.{property.Name} as required, but the database allows NULL: a row " +
                      "written outside EF can hold one, and loading it fails or yields a default value."
                    : $"The model allows null in {Short(entity.Name)}.{property.Name}, but the column is NOT NULL: saving an " +
                      "entity without a value fails at the database.",
                null,
                Ef(entity.Name, property.Name, actual.IsNullable ? null : $"b.Property(e => e.{property.Name}).IsRequired()")));
    }

    /// <param name="existing">The index or unique constraint on those columns.</param>
    private static Finding Uniqueness(EfTableMatch table, EfEntity entity, EfIndex index, string existing, IReadOnlyList<string> columns) => new(
        EfRuleIds.IndexUniqueness, index.IsUnique ? Severity.High : Severity.Low, FindingCategory.Structure, 1.0, Evidence.Derived,
        table.Qualified, columns,
        index.IsUnique
            ? $"Index {existing} on {table.Qualified} is not unique; the model declares it unique"
            : $"{existing} on {table.Qualified} is unique; the model does not say so",
        index.IsUnique
            ? $"{Short(entity.Name)} relies on ({string.Join(", ", columns)}) being unique, but the database does not enforce " +
              "it: duplicates can already be there. EF sees the index as unique in its snapshot, so no migration will " +
              "change it: find and resolve the duplicates, then make the index unique in the database by hand."
            : $"The database enforces uniqueness the model does not know: an insert EF expects to succeed can fail. " +
              "EF compares the model with its snapshot, not the database, so no migration will change it.",
        null,
        // The model is right where it asks for uniqueness: the database is what changes.
        Ef(entity.Name, fluent: index.IsUnique ? null : $"b.HasIndex({Lambda(index.Properties)}).IsUnique()"));

    /// <summary>
    /// Store types as the provider writes them against the catalog's: case, spaces and the aliases the
    /// two spell differently ("int4" and "integer", "datetime2" and "datetime2(7)") are not differences.
    /// </summary>
    private static bool SameStoreType(string expected, string actual) => NormalType(expected) == NormalType(actual);

    private static string NormalType(string type)
    {
        var t = string.Concat(type.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)));
        if (t.EndsWith("[]", StringComparison.Ordinal)) return NormalType(t[..^2]) + "[]";
        if (t is "datetime2(7)" or "datetimeoffset(7)" or "time(7)") t = t[..^3];
        // PostgreSQL writes the precision inside the name: "timestamp(3) with time zone".
        t = TimePrecision().Replace(t, "$1$3timezone($2)");
        foreach (var (alias, name) in TypeAliases)
            if (t == alias || t.StartsWith(alias + "(", StringComparison.Ordinal)) { t = name + t[alias.Length..]; break; }
        return t;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(timestamp|time)\((\d+)\)(with|without)timezone$")]
    private static partial System.Text.RegularExpressions.Regex TimePrecision();

    private static readonly (string Alias, string Name)[] TypeAliases =
    [
        ("int4", "integer"), ("int", "integer"), ("int8", "bigint"), ("int2", "smallint"), ("bool", "boolean"),
        ("varchar", "charactervarying"), ("charactervarying", "charactervarying"), ("char", "character"),
        ("timestamptz", "timestampwithtimezone"), ("timestamp", "timestampwithouttimezone"),
        ("timetz", "timewithtimezone"), ("time", "timewithouttimezone"),
        ("serial", "integer"), ("bigserial", "bigint"), ("smallserial", "smallint"),
        ("float8", "doubleprecision"), ("float4", "real"), ("decimal", "numeric"),
        // SQL Server catalogs a rowversion column as "timestamp".
        ("rowversion", "timestampwithouttimezone"),
    ];

    private static void CompareForeignKey(
        EfTableMatch table, TableDetail detail, EfEntity entity, EfForeignKey key, EfModelMatch match, List<Finding> findings)
    {
        if (EfModelMatcher.ColumnsOf(table, entity.Name, key.Properties) is not { } columns) return;
        if (EfModelMatcher.TableOf(match, key.Principal) is not { Table: { } principalTable } principal) return;

        var principalProperties = key.PrincipalKey.Count > 0 ? key.PrincipalKey : principal.Owner.Key;
        if (EfModelMatcher.ColumnsOf(principal, key.Principal, principalProperties) is not { } principalColumns) return;

        if (detail.ForeignKeysOutgoing.Any(fk =>
                SameColumns(fk.FromColumns, columns)
                && Same(fk.ToSchema, principalTable.Schema) && Same(fk.ToTable, principalTable.Name))) return;

        findings.Add(new Finding(
            EfRuleIds.ForeignKeyMissing, Severity.Medium, FindingCategory.Structure, 1.0, Evidence.Derived,
            table.Qualified, columns,
            $"Relationship {Short(entity.Name)} → {Short(key.Principal)} has no foreign key in the database",
            $"The model relates {Short(entity.Name)}({string.Join(", ", key.Properties)}) to {Short(key.Principal)}, but " +
            $"{table.Qualified} declares no foreign key from ({string.Join(", ", columns)}) to {principal.Qualified}" +
            $"({string.Join(", ", principalColumns)}). The database does not enforce the reference, and " +
            $"{(key.OnDelete is { } d ? $"OnDelete({d})" : "the delete behaviour")} only applies to entities EF has loaded.",
            null,
            Ef(entity.Name, string.Join(", ", key.Properties))));
    }

    private static Finding Unmapped(TableDetail table) => new(
        EfRuleIds.UnmappedTable, Severity.Info, FindingCategory.Structure, 1.0, Evidence.Derived,
        $"{table.Schema}.{table.Name}", null,
        $"Table {table.Schema}.{table.Name} is not mapped by any entity",
        $"No entity of the model is stored in {table.Schema}.{table.Name}. It belongs to another context, another " +
        "application, or nothing: the model cannot say which.",
        null,
        null);

    private static Finding Summary(EfModel model, EfModelMatch match, EfMigrationState migrations, int differences) => new(
        EfRuleIds.DriftSummary, Severity.Info, FindingCategory.Structure, 1.0, Evidence.Derived,
        model.ContextName, null,
        $"{Num.N0(differences)} difference(s) between {model.ContextName} and the database",
        $"Compared {Num.N0(model.Entities.Count)} entities in {Num.N0(match.Tables.Count)} tables. " +
        (migrations.HistoryFound
            ? $"Migrations: {Num.N0(model.MigrationIds.Count)} in the code, {Num.N0(migrations.Pending.Count)} pending, " +
              $"{Num.N0(migrations.Unknown.Count)} applied but unknown to the code."
            : "The migrations history was not read: see the caveats."),
        null,
        new Dictionary<string, object> { ["efVersion"] = model.ProductVersion ?? "", ["context"] = model.ContextName });

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, object> Ef(string entity, string? property = null, string? fluent = null)
    {
        var ef = new Dictionary<string, object> { ["entity"] = entity };
        if (property is not null) ef["property"] = property;
        if (fluent is not null) ef["fluent"] = fluent;
        return new Dictionary<string, object> { ["ef"] = ef };
    }

    private static string Lambda(IReadOnlyList<string> properties) =>
        properties.Count == 1 ? $"e => e.{properties[0]}" : $"e => new {{ {string.Join(", ", properties.Select(p => "e." + p))} }}";



    private static bool SameColumns(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Zip(b).All(p => Same(p.First, p.Second));
}
