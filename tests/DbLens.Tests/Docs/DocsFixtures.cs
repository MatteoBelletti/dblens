using DbLens.Docs.Inventory;

namespace DbLens.Tests.Docs;

/// <summary>The solutions the inventory tests read: a trimmed dotnet/eShop checkout, and the repository's own DemoShop sample.</summary>
internal static class DocsFixtures
{
    /// <summary>The checked-in inventories, in the source tree (a reference is written there, not in the test output).</summary>
    public static string ReferenceFolder => Path.Combine(RepositoryRoot, "tests", "DbLens.Tests", "Docs", "Reference");

    public static string EShopFolder => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Docs", "eShop");

    public static SolutionRead EShop() => SolutionReader.Read(EShopFolder);

    /// <summary>samples/DemoShop.EfCore, found from the test output up to the repository root.</summary>
    public static string DemoShopFolder => Path.Combine(RepositoryRoot, "samples", "DemoShop.EfCore");

    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "dblens.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("dblens.slnx not found above the test output.");
        }
    }

    public static SolutionRead DemoShop() => SolutionReader.Read(DemoShopFolder);
}
