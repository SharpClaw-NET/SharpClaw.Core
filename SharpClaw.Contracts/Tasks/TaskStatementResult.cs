namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Outcome of executing a single task statement.  Returned by
/// <see cref="ITaskOperationInvocationExecutor"/> implementations to signal
/// whether the orchestrator should continue with the next sibling statement
/// or unwind to the task entry point.
/// </summary>
public enum TaskStatementResult
{
    /// <summary>Continue with the next sibling statement.</summary>
    Continue,

    /// <summary>
    /// Unwind out of the current statement list and any nesting up to the
    /// task entry point. Used by the intrinsic <c>return</c> statement.
    /// </summary>
    Return,
}
