namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Public projection of a running task instance's execution context. Passed to
/// <see cref="ITaskOperationExecutor" /> implementations so modules can
/// read and write task variables, enumerate event handlers, and execute statement
/// bodies without taking a dependency on the internal orchestrator or
/// Infrastructure.Tasks.
/// </summary>
public interface ITaskOperationExecutionContext
{
    /// <summary>The running task instance ID.</summary>
    Guid InstanceId { get; }

    /// <summary>The channel this task instance is executing against.</summary>
    Guid ChannelId { get; }

    /// <summary>Active cancellation token for the task instance.</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Scoped service provider for the running task instance. Modules may
    /// resolve services from this provider when executing operations. The scope is
    /// owned by the orchestrator and is valid for the duration of operation
    /// execution.
    /// </summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Task-script variables. Modules may read and write entries to propagate
    /// results.
    /// </summary>
    IDictionary<string, object?> Variables { get; }

    /// <summary>Registered event handlers in this task instance.</summary>
    IReadOnlyList<ITaskEventHandler> EventHandlers { get; }

    /// <summary>
    /// Resolve an expression string, such as a variable reference or literal,
    /// to its current value within this context.
    /// </summary>
    string ResolveExpression(string expression);

    /// <summary>
    /// Append a log entry to this task instance's log.
    /// </summary>
    Task AppendLogAsync(string message);

    /// <summary>
    /// Push an output payload to the task instance's output stream. Used by
    /// whichever module-owned operation is responsible for task output.
    /// </summary>
    Task WriteOutputAsync(string? outputJson);

    /// <summary>
    /// Update the channel currently associated with this running task
    /// instance. Used by a module executor that provisions a channel while
    /// the task is running; subsequent module-owned operations then resolve to the
    /// newly-created channel.
    /// </summary>
    void SetChannelId(Guid channelId);

    /// <summary>
    /// Recursively execute a nested statement list. Returns
    /// <see cref="TaskStatementResult.Return" /> if any nested statement requested an
    /// early return so the caller can unwind.
    /// </summary>
    Task<TaskStatementResult> ExecuteStatementsAsync(
        IReadOnlyList<ITaskStatementInvocation> statements,
        CancellationToken cancellationToken);

    /// <summary>
    /// Evaluate a boolean expression against the current variable scope.
    /// </summary>
    bool EvaluateCondition(string? expression);

    /// <summary>
    /// Register an event handler for a module-owned trigger key.
    /// </summary>
    void RegisterEventHandler(
        string moduleTriggerKey,
        string? parameterName,
        IReadOnlyList<ITaskStatementInvocation> body);

    /// <summary>
    /// Block until the task instance has been resumed.
    /// </summary>
    Task WaitIfPausedAsync();
}
