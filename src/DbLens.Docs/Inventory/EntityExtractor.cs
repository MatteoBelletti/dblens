using DbLens.Core.Analysis.EfCore;

namespace DbLens.Docs.Inventory;

/// <summary>The data model, from the EF Core snapshots already read by <c>DbLens.EfCore</c>: nothing new is parsed here.</summary>
internal static class EntityExtractor
{
    public static (IReadOnlyList<InventoryItem> Items, IReadOnlyList<CoverageNote> Coverage) Extract(
        IReadOnlyList<EfModel> models, IReadOnlyList<SourceFile> files)
    {
        List<InventoryItem> items = [];
        List<CoverageNote> coverage = [];

        foreach (var model in models.OrderBy(m => m.ContextName, StringComparer.Ordinal))
        {
            var snapshot = files.FirstOrDefault(f => f.Text.Contains($"class {model.ContextName}ModelSnapshot", StringComparison.Ordinal));
            items.Add(new InventoryItem(
                $"context:{model.ContextName}", ItemKind.Context, model.ContextName, ItemEvidence.Extracted,
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
                    $"entity:{model.ContextName}.{entity.Name}", ItemKind.Entity, entity.Name, ItemEvidence.Extracted, at,
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

    private static int LineOfText(string text, string needle)
    {
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        return index < 0 ? 1 : 1 + text.AsSpan(0, index).Count('\n');
    }
}
