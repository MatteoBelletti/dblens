namespace DbLens.Tests;

/// <summary>The read-only <c>dblens_reader</c> logins of the demo, as the integration fixtures use them.</summary>
internal static class TestConnections
{
    public static string PostgreSql =>
        Environment.GetEnvironmentVariable("DBLENS_TEST_PG_CONNECTION")
        ?? "Host=localhost;Port=5433;Database=dblens_demo;Username=dblens_reader;Password=readerpassword";

    public static string SqlServer =>
        Environment.GetEnvironmentVariable("DBLENS_TEST_SS_CONNECTION")
        ?? "Server=localhost,1434;Database=dblens_demo;User Id=dblens_reader;Password=ReaderPassword123!;TrustServerCertificate=true";

    public static string For(Core.Models.DatabaseDialect dialect) =>
        dialect == Core.Models.DatabaseDialect.SqlServer ? SqlServer : PostgreSql;

    public static Task<bool> IsReachableAsync(Core.Models.DatabaseDialect dialect) =>
        dialect == Core.Models.DatabaseDialect.SqlServer ? SqlServerAdmin.IsReachableAsync() : PostgresAdmin.IsReachableAsync();
}
