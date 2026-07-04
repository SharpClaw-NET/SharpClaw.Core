namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Optional companion interface to <see cref="ITaskOperationExecutor"/>
/// for executors that need raw statement access (nested bodies, unresolved
/// expressions, event-handler registration).  When the orchestrator is
/// dispatching a parsed statement whose <c>StatementKey</c> is claimed by an executor
/// implementing this interface, it bypasses the resolved-argument path
/// and calls <see cref="ExecuteInvocationAsync"/> directly.
/// </summary>
public interface ITaskOperationInvocationExecutor : ITaskOperationExecutor
{
    /// <summary>
    /// Execute a statement with full access to its raw shape.  Return
    /// <see cref="TaskStatementResult.Return"/> to unwind to the task entry point.
    /// </summary>
    Task<TaskStatementResult> ExecuteInvocationAsync(
        ITaskStatementInvocation statement,
        ITaskOperationExecutionContext context);
}
