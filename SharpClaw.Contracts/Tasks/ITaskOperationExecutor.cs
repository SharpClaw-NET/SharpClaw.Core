namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Allows a module to execute task operations that it owns. Core executes
/// intrinsic C# task-language statements directly and routes module-owned
/// operation keys to the executor that claims them via <see cref="CanExecute"/>.
/// </summary>
public interface ITaskOperationExecutor
{
    /// <summary>The module ID this executor belongs to.</summary>
    string ModuleId { get; }

    /// <summary>
    /// Returns <c>true</c> if this executor handles the given module operation key.
    /// </summary>
    bool CanExecute(string operationKey);

    /// <summary>
    /// Execute the operation. Returns <c>true</c> to continue task execution;
    /// <c>false</c> signals an early return from the task body.
    /// Throw to propagate an operation execution error.
    /// </summary>
    /// <param name="operationKey">The module-owned operation key being executed.</param>
    /// <param name="context">Execution context — variables, event handlers, logging.</param>
    /// <param name="arguments">Positional operation arguments already resolved to string values.</param>
    /// <param name="expression">Operation expression string, if any (already resolved).</param>
    /// <param name="resultVariable">Variable name to store the operation output, or <c>null</c>.</param>
    Task<bool> ExecuteAsync(
        string operationKey,
        ITaskOperationExecutionContext context,
        IReadOnlyList<string>? arguments,
        string? expression,
        string? resultVariable);
}
