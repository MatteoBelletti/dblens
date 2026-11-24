using DbLens.Core.Analysis.EfCore;

namespace DbLens.Docs.Inventory;

/// <summary>The data model, from the EF Core snapshots already read by <c>DbLens.EfCore</c>: nothing new is parsed here.</summary>
internal static class EntityExtractor
{
    public static (IReadOnlyList<InventoryItem> Items, IReadOnlyList<CoverageNote> Coverage) Extract(
        IReadOnlyList<EfModel> models, IReadOnlyList<SourceFile> files, IReadOnlyList<string> sources)
    {
        List<InventoryItem> items = [];
        List<CoverageNote> coverage = [];

        // The same context name in several projects (one per service is common): the snapshot's folder tells them apart.
        var read = models.Select((m, i) => (Model: m, Source: i < sources.Count ? sources[i] : null))
            .OrderBy(x => x.Model.ContextName, StringComparer.Ordinal).ThenBy(x => x.Source ?? "", StringComparer.Ordinal)
            .ToList();
        var repeated = read.GroupBy(x => x.Model.ContextName, StringComparer.Ordinal).Where(g => g.Count() > 1).ToDictionary(g => g.Key, _ => 0, StringComparer.Ordinal);

        foreach (var (model, source) in read)
        {
            var snapshot = source is not null
                ? files.FirstOrDefault(f => f.Path == source)
                : files.FirstOrDefault(f => f.Text.Contains($"class {model.ContextName}ModelSnapshot", StringComparison.Ordinal));
            var key = model.ContextName;
            if (repeated.ContainsKey(model.ContextName))
            {
                repeated[model.ContextName]++;
                var folder = snapshot is null ? "" : FolderOf(snapshot.Path);
                key = $"{model.ContextName} [{(folder.Length > 0 ? folder : $"#{repeated[model.ContextName]}")}]";
            }

            items.Add(new InventoryItem(
                $"context:{key}", ItemKind.Context, model.ContextName, ItemEvidence.Extracted,
                new SourceLocation(snapshot?.Path ?? $"({model.ContextName} snapshot)", snapshot is null ? 1 : LineOfText(snapshot.Text, $"class {model.ContextName}ModelSnapshot")),
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["defaultSchema"] = model.DefaultSchema ?? "",
                    ["efVersion"] = model.ProductVersion ?? "",
                    ["entities"] = model.Entities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["migrations"] = model.MigrationIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                [], null));
            foreach (var entity in model.Entities.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var at = new SourceLocation(snapshot?.Path ?? $"({model.ContextName} snapshot)", snapshot is null ? 1 : LineOfText(snapshot.Text, $"Entity(\"{entity.Name}\""));
                var schema = entity.Schema ?? model.DefaultSchema;
                items.Add(new InventoryItem(
                    $"entity:{key}.{entity.Name}", ItemKind.Entity, entity.Name, ItemEvidence.Extracted, at,
                    new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["context"] = model.ContextName,
                        ["table"] = schema is null ? entity.Table : $"{schema}.{entity.Table}",
                        ["key"] = string.Join(", ", entity.Key),
                        ["properties"] = entity.Properties.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["foreignKeys"] = entity.ForeignKeys.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["sharesTableWith"] = entity.SharesTableWith ?? "",
                    },
                    [], null));
            }

            foreach (var unsupported in model.Unsupported)
                coverage.Add(new CoverageNote("data model", $"{model.ContextName}: {unsupported.Reason}", unsupported.Entities.Count));
        }

        return (items, coverage);
    }

    // The folder of the project that holds the snapshot: the migrations folder is not part of its name.
    private static string FolderOf(string path)
    {
        var directory = PathText.Directory(path);
        return directory.EndsWith("/Migrations", StringComparison.Ordinal) ? directory[..^"/Migrations".Length] : directory;
    }

    private static int LineOfText(string text, string needle)
    {
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        return index < 0 ? 1 : 1 + text.AsSpan(0, index).Count('\n');
    }
}
