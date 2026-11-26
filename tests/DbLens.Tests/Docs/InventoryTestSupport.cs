using DbLens.Core.Analysis.EfCore;
using DbLens.Docs.Inventory;

namespace DbLens.Tests.Docs;

internal static class InventoryTestSupport
{
    public static SourceFile File(string path, string text) => new(path, text);

    public static ProjectInventory Build(params SourceFile[] files) => ProjectInventoryEngine.Build(files, []);

    public static ProjectInventory Build(IReadOnlyList<EfModel> models, params SourceFile[] files) => ProjectInventoryEngine.Build(files, models);

    public static IEnumerable<InventoryItem> Of(this ProjectInventory inventory, ItemKind kind) => inventory.Items.Where(i => i.Kind == kind);

    public static InventoryItem Item(this ProjectInventory inventory, string id) =>
        inventory.Items.Single(i => i.Id == id);

    /// <summary>A csproj with the properties a test cares about.</summary>
    public static SourceFile Project(string path, string sdk = "Microsoft.NET.Sdk", string body = "<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>") =>
        File(path, $"<Project Sdk=\"{sdk}\">{body}</Project>");

    /// <summary>A csproj that references other projects, by relative path.</summary>
    public static SourceFile Csproj(string path, string sdk, params string[] references) =>
        File(path, $"<Project Sdk=\"{sdk}\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>"
                   + string.Concat(references.Select(r => $"<ProjectReference Include=\"{r}\" />")) + "</ItemGroup></Project>");

    public static SourceFile Web(string path, params string[] references) => Csproj(path, "Microsoft.NET.Sdk.Web", references);

    public static SourceFile Lib(string path, params string[] references) => Csproj(path, "Microsoft.NET.Sdk", references);

    /// <summary>Source wrapped as a file with a usings block, so each test shows only its own lines.</summary>
    public static SourceFile Code(string path, string body) => File(path, body);
}
