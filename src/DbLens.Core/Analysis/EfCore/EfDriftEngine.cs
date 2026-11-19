using DbLens.Core.Models;
using static DbLens.Core.Analysis.Names;

namespace DbLens.Core.Analysis.EfCore;

/// <summary>
/// What the database holds that the EF model does not know, and the reverse. Pure. Every finding
/// is derived from the catalog and the snapshot; each names the entity and property to touch in
/// <c>Metadata["ef"]</c>. Column types, lengths and nullability are not compared here.
/// </summary>
public static class EfDriftEngine
{
    /// <param name="applied">The applied migration ids; null when no history table was found.</param>
    /// <param name="historyTable">The history table read, as a catalog pair.</param>
    /// <param name="schemas">The schemas compared: a table there that no entity maps is reported.</param>
    public static CompareEfModelResult Analyze(
        EfModel model,
        EfModelMatch match,
        IReadOnlyList<string>? applied,
        (string Schema, string Name)? historyTable,
        IReadOnlySet<string> schemas)
    {
        List<Finding> findings = [];
        List<string> caveats = [.. model.Unsupported.Select(u => u.Reason), .. match.Ambiguities];

        var history = historyTable is { } h ? $"{h.Schema}.{h.Name}" : null;
        var migrations = Migrations(model, applied, history, findings, caveats);

        foreach (var table in match.Tables.Where(t => !t.Ambiguous))
            CompareTable(table, match, findings);

        foreach (var table in match.UnmappedTables.Where(t =>
                     schemas.Contains(t.Schema)
                     && !(historyTable is { } ht && Same(t.Schema, ht.Schema) && Same(t.Name, ht.Name))))
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
        EfModel model, IReadOnlyList<string>? applied, string? historyTable, List<Finding> findings, List<string> caveats)
    {
        if (applied is null)
        {
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

        List<IReadOnlyList<string>> modelIndexes = [];
        foreach (var entity in table.Entities)
            foreach (var index in entity.Indexes)
            {
                if (EfModelMatcher.ColumnsOf(table, entity.Name, index.Properties) is not { } columns) continue;
                modelIndexes.Add(columns);

                if (detail.Indexes.Any(i => SameColumns(i.Columns, columns))
                    || (detail.PrimaryKey is { } pk && SameColumns(pk.Columns, columns))) continue;

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
                $"{index.Name} ({string.Join(", ", index.Columns)}) exists in the database but not in the model: it was added " +
                "by hand. If the model later declares it, the generated migration will contain a CreateIndex that fails " +
                "where the index already exists: remove that call from the migration, or make it conditional with " +
                "migrationBuilder.Sql. Declaring it now with its database name keeps the two in step.",
                null,
                Ef(owner.Name, fluent: fluent)));
        }

        foreach (var entity in table.Entities)
            foreach (var key in entity.ForeignKeys)
                CompareForeignKey(table, detail, entity, key, match, findings);
    }

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
            : "No migrations history table was found."),
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
