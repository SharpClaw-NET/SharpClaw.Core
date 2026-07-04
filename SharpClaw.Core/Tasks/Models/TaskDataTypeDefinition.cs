namespace SharpClaw.Core.Tasks.Models;

/// <summary>
/// A custom data type defined inside a task script. Data types define the
/// shape of structured results that task operations may write to listeners.
/// </summary>
public sealed record TaskDataTypeDefinition(
    string Name,
    IReadOnlyList<TaskPropertyDefinition> Properties,
    /// <summary>
    /// When <see langword="true"/> this type is the primary output schema
    /// pushed to listeners by the module-owned output operation.
    /// </summary>
    bool IsOutputType = false);
