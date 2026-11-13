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

public sealed class DatabaseInspectorFactory(ILoggerFactory loggerFactory, ServerPolicy policy) : IDatabaseInspectorFactory
{
    public IDatabaseInspector Create(DatabaseDialect dialect, string connectionString) => dialect switch
    {
        DatabaseDialect.PostgreSql => new PostgresInspector(
            connectionString, loggerFactory.CreateLogger<PostgresInspector>())
        {
            AllowWriteCapableLogin = policy.AllowWriteCapableLogin,
            ReadOnlyLoginOnLocalhost = policy.ReadOnlyLoginOnLocalhost,
        },
        DatabaseDialect.SqlServer => new SqlServerInspector(
            connectionString, loggerFactory.CreateLogger<SqlServerInspector>())
        {
            AllowWriteCapableLogin = policy.AllowWriteCapableLogin,
            ReadOnlyLoginOnLocalhost = policy.ReadOnlyLoginOnLocalhost,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), $"Unsupported dialect: {dialect}"),
    };
}
