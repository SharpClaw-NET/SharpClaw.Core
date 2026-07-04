namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Read-only projection of a single parsed task statement as seen by module-side
/// invocation executors. Unlike the resolved-argument execution path
/// used by <see cref="ITaskOperationExecutor"/>, this surface preserves
/// the <em>raw</em> statement shape so Core intrinsic statements and module
/// invocation executors can drive nested execution.
/// </summary>
public interface ITaskStatementInvocation
{
    /// <summary>Stable string key identifying this parsed statement.</summary>
    string StatementKey { get; }

    /// <summary>Variable name for declarations, assignments, and foreach loops.</summary>
    string? VariableName { get; }

    /// <summary>Captured type name for declarations or descriptor-backed generic operations.</summary>
    string? TypeName { get; }

    /// <summary>Variable that stores the result of this statement or operation, if any.</summary>
    string? ResultVariable { get; }

    /// <summary>
    /// The raw, unresolved expression text from the source script.  Modules
    /// Core intrinsic statements that store expressions verbatim read this
    /// directly. Modules that consume runtime values resolve it via
    /// <see cref="ITaskOperationExecutionContext.ResolveExpression(string)"/>.
    /// </summary>
    string? RawExpression { get; }

    /// <summary>Raw, unresolved positional arguments.</summary>
    IReadOnlyList<string>? Arguments { get; }

    /// <summary>Module-owned trigger key for event-handler statements.</summary>
    string? ModuleTriggerKey { get; }

    /// <summary>Lambda parameter name for event-handler callbacks.</summary>
    string? HandlerParameter { get; }

    /// <summary>Nested body statements (then-branch, loop body, handler body).</summary>
    IReadOnlyList<ITaskStatementInvocation>? Body { get; }

    /// <summary>Nested else-body statements (conditional else-branch).</summary>
    IReadOnlyList<ITaskStatementInvocation>? ElseBody { get; }
}
