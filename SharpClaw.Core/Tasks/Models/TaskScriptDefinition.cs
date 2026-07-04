using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Core.Tasks.Models;

/// <summary>
/// Complete parsed representation of a task script. Produced by the parser
/// and consumed by the validator and compiler.
/// </summary>
public sealed record TaskScriptDefinition
{
    /// <summary>Task name from the <c>[Task("...")]</c> attribute.</summary>
    public required string Name { get; init; }

    /// <summary>Optional description from the <c>[Description("...")]</c> attribute.</summary>
    public string? Description { get; init; }

    /// <summary>Raw source text of the .cs file.</summary>
    public required string SourceText { get; init; }

    /// <summary>Name of the main task class.</summary>
    public required string ClassName { get; init; }

    /// <summary>Name of the entry-point method, typically <c>RunAsync</c>.</summary>
    public required string EntryPointMethod { get; init; }

    /// <summary>Input parameters declared in the task class.</summary>
    public required IReadOnlyList<TaskParameterDefinition> Parameters { get; init; }

    /// <summary>Custom data types defined in the script.</summary>
    public required IReadOnlyList<TaskDataTypeDefinition> DataTypes { get; init; }

    /// <summary>
    /// The primary output type that a module-owned output operation may push
    /// to listeners. <see langword="null" /> if the task has no structured output.
    /// </summary>
    public TaskDataTypeDefinition? OutputType { get; init; }

    /// <summary>Ordered steps in the task entry-point body.</summary>
    public required IReadOnlyList<TaskStatementDefinition> Statements { get; init; }

    /// <summary>
    /// Custom tool-call hooks defined via <c>[ToolCall("name")]</c> methods in
    /// the task script. Each hook becomes a tool that agents can invoke during
    /// module-owned chat operations.
    /// </summary>
    public IReadOnlyList<TaskToolCallHook> ToolCallHooks { get; init; } = [];

    /// <summary>
    /// Agent output format annotation from <c>[AgentOutput("format")]</c> on
    /// the task class. When non-null, agents may write structured results to
    /// the task through their module-owned output path.
    /// </summary>
    public string? AgentOutputFormat { get; init; }

    /// <summary>
    /// Environment requirements declared via <c>[RequiresProvider]</c>,
    /// <c>[RequiresModule]</c>, <c>[RequiresPlatform]</c>, <c>[ModelId]</c>,
    /// etc. Populated by the parser and checked by task preflight.
    /// </summary>
    public IReadOnlyList<TaskRequirementDefinition> Requirements { get; init; } = [];

    /// <summary>
    /// Self-registration trigger bindings declared via <c>[Schedule]</c>,
    /// <c>[OnEvent]</c>, <c>[OnFileChanged]</c>, etc. Populated by the parser
    /// and persisted as JSON by the host.
    /// </summary>
    public IReadOnlyList<TaskTriggerDefinition> TriggerDefinitions { get; init; } = [];
}
