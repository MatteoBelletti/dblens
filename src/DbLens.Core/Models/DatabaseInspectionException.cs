namespace DbLens.Core.Models;

/// <summary>Thrown when a database inspection operation fails due to a database or connectivity error.</summary>
public sealed class DatabaseInspectionException : Exception
{
    public DatabaseInspectionException(string message) : base(message) { }
    public DatabaseInspectionException(string message, Exception inner) : base(message, inner) { }
}
