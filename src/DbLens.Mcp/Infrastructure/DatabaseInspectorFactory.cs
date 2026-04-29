using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using DbLens.Postgres;
using DbLens.SqlServer;
using Microsoft.Extensions.Logging;

namespace DbLens.Mcp.Infrastructure;

public interface IDatabaseInspectorFactory
{
    IDatabaseInspector Create(DatabaseDialect dialect, string connectionString);
}

public sealed class DatabaseInspectorFactory(ILoggerFactory loggerFactory) : IDatabaseInspectorFactory
{
    public IDatabaseInspector Create(DatabaseDialect dialect, string connectionString) => dialect switch
    {
        DatabaseDialect.PostgreSql => new PostgresInspector(
            connectionString, loggerFactory.CreateLogger<PostgresInspector>()),
        DatabaseDialect.SqlServer => new SqlServerInspector(
            connectionString, loggerFactory.CreateLogger<SqlServerInspector>()),
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), $"Unsupported dialect: {dialect}"),
    };
}
