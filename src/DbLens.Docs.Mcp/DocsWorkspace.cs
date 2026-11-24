using DbLens.Docs.Documents;
using DbLens.Docs.Inventory;

namespace DbLens.Docs.Mcp;

/// <summary>
/// The one folder the server works on, fixed when it starts: no tool takes a path, so a model cannot ask
/// for another folder. Reads the code and the documents afresh on every call.
/// </summary>
public sealed class DocsWorkspace
{
    public DocsWorkspace(string root)
    {
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root)) throw new ArgumentException($"{Root} is not a folder.");
        Options = DocsConfig.Load(Root);
    }

    public string Root { get; }

    public DocsOptions Options { get; }

    public ProjectInventory Inventory() => ProjectInventoryEngine.Build(SolutionReader.Read(Root));

    public IReadOnlyDictionary<string, string> Documents() => DocsFolder.Read(Root, Options);

    public void Write(string path, string text) => DocsFolder.Write(Root, Options, new Dictionary<string, string> { [path] = text });
}
