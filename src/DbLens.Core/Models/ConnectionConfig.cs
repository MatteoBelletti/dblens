namespace DbLens.Core.Models;

/// <summary>Holds connection settings for a single database target.</summary>
public sealed class ConnectionConfig
{
    /// <summary>ADO.NET connection string.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Database dialect this config targets.</summary>
    public required DatabaseDialect Dialect { get; init; }
}
