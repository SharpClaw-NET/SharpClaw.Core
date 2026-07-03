namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Read-only projection of a single task step as seen by module-side
/// invocation executors. Unlike the resolved-argument execution path
/// used by <see cref="ITaskStepExecutorExtension"/>, this surface preserves
/// the <em>raw</em> step shape so Core intrinsic statements and module
/// invocation executors can drive nested execution.
/// </summary>
public interface ITaskStepInvocation
{
    /// <summary>Stable string key identifying this step's operation.</summary>
    string StepKey { get; }

    /// <summary>Variable name for declare/assign and foreach-loop steps.</summary>
    string? VariableName { get; }

    /// <summary>Type name for declare-variable and parse-response steps.</summary>
    string? TypeName { get; }

    /// <summary>Variable that stores the result of this step, if any.</summary>
    string? ResultVariable { get; }

    /// <summary>
    /// The raw, unresolved expression text from the source script.  Modules
    /// Core intrinsic statements that store expressions verbatim read this
    /// directly. Modules that consume runtime values resolve it via
    /// <see cref="ITaskStepExecutionContext.ResolveExpression(string)"/>.
    /// </summary>
    string? RawExpression { get; }

    /// <summary>Raw, unresolved positional arguments.</summary>
    IReadOnlyList<string>? Arguments { get; }

    /// <summary>Module-owned trigger key for event-handler steps.</summary>
    string? ModuleTriggerKey { get; }

    /// <summary>Lambda parameter name for event-handler callbacks.</summary>
    string? HandlerParameter { get; }

    /// <summary>Nested body steps (then-branch, loop body, handler body).</summary>
    IReadOnlyList<ITaskStepInvocation>? Body { get; }

    /// <summary>Nested else-body steps (conditional else-branch).</summary>
    IReadOnlyList<ITaskStepInvocation>? ElseBody { get; }
}
