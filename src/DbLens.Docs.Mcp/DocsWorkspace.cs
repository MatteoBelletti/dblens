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

    public ProjectInventory Inventory() => ProjectInventoryEngine.Build(SolutionReader.Read(Root), DocsConfig.LoadInventory(Root));

    public IReadOnlyDictionary<string, string> Documents() => DocsFolder.Read(Root, Options);

    private readonly object _writing = new();

    /// <summary>
    /// Reads the code and the documents, lets <paramref name="change"/> decide, and writes, with no other
    /// write in between, in this process or in another one (a scaffold from the command line): a write that
    /// started from an older text would take another's words away.
    /// </summary>
    public T Update<T>(Func<ProjectInventory, IReadOnlyDictionary<string, string>, (string Path, string Text, T Result)> change)
    {
        lock (_writing)
        {
            var inventory = Inventory();
            return DocsFolder.Locked(Root, Options, () =>
            {
                var (path, text, result) = change(inventory, Documents());
                Write(path, text);
                return result;
            });
        }
    }

    private void Write(string path, string text) => DocsFolder.Write(Root, Options, new Dictionary<string, string> { [path] = text });
}
