namespace DbLens.Core.Models;

/// <summary>
/// dblens declined to run something on purpose — a disabled capability, a write-capable login.
/// Its message is written for the caller and is safe to show.
/// </summary>
public sealed class ExecutionRefusedException(string message) : InvalidOperationException(message);
