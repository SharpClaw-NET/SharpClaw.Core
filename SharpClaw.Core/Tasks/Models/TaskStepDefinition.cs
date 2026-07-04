using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Core.Tasks.Models;

/// <summary>
/// A single step in a task script body. The <see cref="StepKey" /> discriminator
/// determines which properties are relevant. Steps form a tree: event handlers,
/// conditionals, and loops contain nested body steps.
/// </summary>
public sealed record TaskStepDefinition : ITaskStepInvocation
{
    /// <summary>
    /// Stable wire-format string key identifying this step's operation.
    /// Intrinsic language keys are exposed by <see cref="TaskLanguageStepKeys" />.
    /// Module operations use keys provided by the descriptor or executor owned
    /// by that module.
    /// </summary>
    public required string StepKey { get; init; }

    /// <summary>Source line number (1-based) for diagnostics.</summary>
    public required int Line { get; init; }

    /// <summary>Source column (0-based) for diagnostics.</summary>
    public required int Column { get; init; }

    /// <summary>
    /// Variable name for <c>core.declare_variable</c> and <c>core.assign</c> steps.
    /// </summary>
    public string? VariableName { get; init; }

    /// <summary>
    /// Type name for intrinsic declarations and descriptor-backed module calls
    /// that capture a generic type argument.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// Variable that stores the result of this step. Used by intrinsic
    /// expression steps and descriptor-backed module operations that produce
    /// a value.
    /// </summary>
    public string? ResultVariable { get; init; }

    /// <summary>
    /// Expression text whose interpretation depends on <see cref="StepKey" />.
    /// Intrinsic examples include declaration initializers, assignment values,
    /// conditional predicates, loop predicates, delay durations, log messages,
    /// and evaluated expressions. Module operations define their own expression
    /// meaning in their descriptor and executor.
    /// </summary>
    public string? Expression { get; init; }

    /// <summary>
    /// Positional arguments passed to descriptor-backed module operations.
    /// </summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    /// <summary>
    /// Module-owned trigger key for <c>core.event_handler</c> steps. Identifies
    /// which module trigger the handler is bound to.
    /// </summary>
    public string? ModuleTriggerKey { get; init; }

    /// <summary>
    /// Lambda parameter name for event-handler callbacks.
    /// </summary>
    public string? HandlerParameter { get; init; }

    /// <summary>
    /// Nested steps: event-handler body, conditional then-branch, or loop body.
    /// </summary>
    public IReadOnlyList<TaskStepDefinition>? Body { get; init; }

    /// <summary>Else branch for <c>core.conditional</c> steps.</summary>
    public IReadOnlyList<TaskStepDefinition>? ElseBody { get; init; }

    string? ITaskStepInvocation.RawExpression => Expression;
    IReadOnlyList<ITaskStepInvocation>? ITaskStepInvocation.Body => Body;
    IReadOnlyList<ITaskStepInvocation>? ITaskStepInvocation.ElseBody => ElseBody;
}
