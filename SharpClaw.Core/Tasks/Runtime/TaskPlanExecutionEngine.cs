using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Tasks.Compilation;
using SharpClaw.Core.Tasks.Models;

namespace SharpClaw.Core.Tasks.Runtime;

/// <summary>
/// Executes compiled task plans through the canonical SharpClaw task runtime
/// semantics while delegating persistence and host services to adapters.
/// </summary>
public sealed class TaskPlanExecutionEngine
{
    private readonly IReadOnlyList<ITaskOperationExecutor> _operationExecutors;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TaskExpressionEngine _expressions;
    private readonly TaskRuntimeLifecycleEngine _runtimeLifecycle;
    private readonly ILogger<TaskPlanExecutionEngine> _logger;

    /// <summary>
    /// Creates the execution engine with module operation executors and host DI
    /// scope support.
    /// </summary>
    public TaskPlanExecutionEngine(
        IServiceScopeFactory scopeFactory,
        IEnumerable<ITaskOperationExecutor> operationExecutors,
        TaskExpressionEngine? expressions = null,
        TaskRuntimeLifecycleEngine? runtimeLifecycle = null,
        ILogger<TaskPlanExecutionEngine>? logger = null)
    {
        _scopeFactory = scopeFactory
            ?? throw new ArgumentNullException(nameof(scopeFactory));
        _operationExecutors = operationExecutors?.ToList()
            ?? throw new ArgumentNullException(nameof(operationExecutors));
        _expressions = expressions ?? new TaskExpressionEngine();
        _runtimeLifecycle = runtimeLifecycle ?? new TaskRuntimeLifecycleEngine();
        _logger = logger ?? NullLogger<TaskPlanExecutionEngine>.Instance;
    }

    /// <summary>
    /// Executes a compiled task plan to completion, cancellation, or failure.
    /// </summary>
    public async Task<TaskPlanExecutionOutcome> ExecuteAsync(
        TaskPlanExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = new TaskPlanExecutionContext(
            request.InstanceId,
            request.Plan,
            request.Runtime,
            request.CancellationToken)
        {
            Services = request.Services
        };

        SetupSharedDataStore(request, context);
        RegisterCustomToolHooks(request, context);

        var executionTiming = Stopwatch.StartNew();
        try
        {
            var initialChannelId = await request.Host.LoadInitialChannelIdAsync(
                request.InstanceId,
                request.CancellationToken);
            context.ChannelId = initialChannelId ?? Guid.Empty;

            await ExecuteStatementDefinitionsAsync(
                request.Plan.ExecutionStatements,
                context,
                request.Host,
                request.CancellationToken,
                throwOnUnsupportedInvocation: false);

            await request.Host.MarkTerminalStatusAsync(
                request.InstanceId,
                TaskInstanceStatus.Completed,
                request.CancellationToken);
            await EmitRuntimeEventPlanAsync(
                request.InstanceId,
                _runtimeLifecycle.BuildTerminalPlan(TaskInstanceStatus.Completed),
                request.Host,
                request.Runtime,
                request.CancellationToken);

            executionTiming.Stop();
            _logger.LogDebug(
                "Task instance {InstanceId} completed in {ElapsedMs}ms.",
                request.InstanceId,
                executionTiming.ElapsedMilliseconds);
            return new TaskPlanExecutionOutcome(
                TaskInstanceStatus.Completed,
                null,
                executionTiming.Elapsed);
        }
        catch (OperationCanceledException)
        {
            await request.Host.MarkTerminalStatusAsync(
                request.InstanceId,
                TaskInstanceStatus.Cancelled,
                CancellationToken.None);
            await EmitRuntimeEventPlanAsync(
                request.InstanceId,
                _runtimeLifecycle.BuildTerminalPlan(TaskInstanceStatus.Cancelled),
                request.Host,
                request.Runtime,
                CancellationToken.None);

            executionTiming.Stop();
            _logger.LogDebug(
                "Task instance {InstanceId} cancelled after {ElapsedMs}ms.",
                request.InstanceId,
                executionTiming.ElapsedMilliseconds);
            return new TaskPlanExecutionOutcome(
                TaskInstanceStatus.Cancelled,
                null,
                executionTiming.Elapsed);
        }
        catch (Exception ex)
        {
            await request.Host.MarkFailedAsync(
                request.InstanceId,
                ex.Message,
                CancellationToken.None);
            await EmitRuntimeEventPlanAsync(
                request.InstanceId,
                _runtimeLifecycle.BuildFailurePlan(ex.Message),
                request.Host,
                request.Runtime,
                CancellationToken.None);

            executionTiming.Stop();
            _logger.LogWarning(
                ex,
                "Task instance {InstanceId} failed after {ElapsedMs}ms.",
                request.InstanceId,
                executionTiming.ElapsedMilliseconds);
            return new TaskPlanExecutionOutcome(
                TaskInstanceStatus.Failed,
                ex.Message,
                executionTiming.Elapsed);
        }
        finally
        {
            TaskSharedData.Remove(request.InstanceId);
        }
    }

    private void SetupSharedDataStore(
        TaskPlanExecutionRequest request,
        TaskPlanExecutionContext context)
    {
        var store = TaskSharedData.GetOrCreate(request.InstanceId);
        store.TaskName = request.Plan.TaskName;
        store.TaskDescription = request.Plan.Description;
        store.TaskSourceText = request.Plan.Definition.SourceText;
        store.TaskParametersJson = request.Plan.ParameterValues.Count > 0
            ? JsonSerializer.Serialize(request.Plan.ParameterValues)
            : null;
        store.AllowedOutputFormat = request.Plan.AgentOutputFormat;
        store.RegisterBuiltInTools();
        store.OnAgentOutput = async data =>
            await EmitOutputAsync(context, request.Host, data, context.CancellationToken);
        store.OnSharedDataChanged = async (
            description,
            lightSnapshot,
            bigSnapshotJson) =>
        {
            await request.Host.PersistSharedDataSnapshotAsync(
                request.InstanceId,
                lightSnapshot,
                bigSnapshotJson,
                context.CancellationToken);
            await EmitRuntimeEventPlanAsync(
                request.InstanceId,
                _runtimeLifecycle.BuildSharedDataChangedPlan(description),
                request.Host,
                request.Runtime,
                context.CancellationToken);
        };
    }

    private void RegisterCustomToolHooks(
        TaskPlanExecutionRequest request,
        TaskPlanExecutionContext context)
    {
        var store = TaskSharedData.GetOrCreate(request.InstanceId);

        foreach (var hook in request.Plan.ToolCallHooks)
        {
            store.RegisterCustomToolHook(hook, async (args, hookCt) =>
            {
                foreach (var parameter in hook.Parameters)
                {
                    var value = args?.TryGetProperty(
                        parameter.Name,
                        out var property) == true
                        ? property.ValueKind == JsonValueKind.String
                            ? property.GetString()
                            : property.GetRawText()
                        : null;
                    context.Variables[parameter.Name] = value;
                }

                await ExecuteStatementDefinitionsAsync(
                    hook.Body,
                    context,
                    request.Host,
                    hookCt,
                    throwOnUnsupportedInvocation: false);

                return hook.ReturnVariable is not null
                    && context.Variables.TryGetValue(
                        hook.ReturnVariable,
                        out var result)
                    ? result?.ToString() ?? string.Empty
                    : string.Empty;
            });
        }
    }

    private async Task<TaskStatementExecutionResult> ExecuteStatementAsync(
        TaskStatementDefinition statement,
        TaskPlanExecutionContext context,
        ITaskPlanExecutionHost host,
        CancellationToken ct)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
        await context.Runtime.WaitIfPausedAsync(context.CancellationToken);

        var statementKey = statement.StatementKey ?? string.Empty;
        if (TaskLanguageStatementKeys.IsIntrinsic(statementKey))
            return await ExecuteIntrinsicLanguageStepAsync(statement, context, host);

        var executor = _operationExecutors.FirstOrDefault(e => e.CanExecute(statementKey));
        if (executor is null)
        {
            throw new InvalidOperationException(
                $"No task operation executor is registered for statement key '{statementKey}' at line {statement.Line}, column {statement.Column}. " +
                "The module or operation that owns this task statement is missing or was not loaded.");
        }

        var moduleContext = new TaskOperationContextAdapter(context, this, host);
        var operationTiming = Stopwatch.StartNew();

        if (executor is ITaskOperationInvocationExecutor invocationExecutor)
        {
            var result = await invocationExecutor.ExecuteInvocationAsync(
                statement,
                moduleContext);
            operationTiming.Stop();
            _logger.LogDebug(
                "Task instance {InstanceId} statement {StatementKey} completed in {ElapsedMs}ms. Result={Result}",
                context.InstanceId,
                SanitizeForLog(statementKey),
                operationTiming.ElapsedMilliseconds,
                result);
            return result == TaskStatementResult.Return
                ? TaskStatementExecutionResult.Return
                : TaskStatementExecutionResult.Continue;
        }

        var resolvedArguments = statement.Arguments?
            .Select(argument => _expressions.ResolveExpression(
                argument,
                context.Variables))
            .ToList();
        if (statement.TypeName is not null)
        {
            resolvedArguments ??= [];
            resolvedArguments.Insert(0, statement.TypeName);
        }

        var resolvedExpression = statement.Expression is not null
            ? _expressions.ResolveExpression(statement.Expression, context.Variables)
            : null;
        var keepGoing = await executor.ExecuteAsync(
            statementKey,
            moduleContext,
            resolvedArguments,
            resolvedExpression,
            statement.ResultVariable);

        operationTiming.Stop();
        _logger.LogDebug(
            "Task instance {InstanceId} operation statement {StatementKey} completed in {ElapsedMs}ms. Continue={Continue}",
            context.InstanceId,
            SanitizeForLog(statementKey),
            operationTiming.ElapsedMilliseconds,
            keepGoing);
        return keepGoing
            ? TaskStatementExecutionResult.Continue
            : TaskStatementExecutionResult.Return;
    }

    private async Task<TaskStatementExecutionResult> ExecuteIntrinsicLanguageStepAsync(
        TaskStatementDefinition step,
        TaskPlanExecutionContext context,
        ITaskPlanExecutionHost host)
    {
        switch (step.StatementKey)
        {
            case TaskLanguageStatementKeys.DeclareVariable:
            case TaskLanguageStatementKeys.Assign:
                context.Variables[step.VariableName ?? string.Empty] = step.Expression;
                return TaskStatementExecutionResult.Continue;

            case TaskLanguageStatementKeys.Evaluate:
                if (step.ResultVariable is not null)
                    context.Variables[step.ResultVariable] = step.Expression;
                return TaskStatementExecutionResult.Continue;

            case TaskLanguageStatementKeys.Log:
            {
                var message = step.Expression is null
                    ? string.Empty
                    : _expressions.ResolveExpression(step.Expression, context.Variables);
                await host.AppendLogAsync(
                    context.InstanceId,
                    message,
                    JobLogLevels.Info,
                    context.CancellationToken);
                return TaskStatementExecutionResult.Continue;
            }

            case TaskLanguageStatementKeys.Delay:
            {
                var resolved = step.Expression is null
                    ? null
                    : _expressions.ResolveExpression(step.Expression, context.Variables);
                if (int.TryParse(resolved, out var delayMs))
                    await DelayWithPauseAsync(delayMs, context);
                return TaskStatementExecutionResult.Continue;
            }

            case TaskLanguageStatementKeys.WaitUntilStopped:
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
                return TaskStatementExecutionResult.Continue;

            case TaskLanguageStatementKeys.Return:
                return TaskStatementExecutionResult.Return;

            case TaskLanguageStatementKeys.Conditional:
            {
                var branch = _expressions.EvaluateCondition(step.Expression, context.Variables)
                    ? step.Body
                    : step.ElseBody;
                if (branch is null)
                    return TaskStatementExecutionResult.Continue;
                return await ExecuteStatementDefinitionsAsync(
                    branch,
                    context,
                    host,
                    context.CancellationToken,
                    throwOnUnsupportedInvocation: false) == TaskStatementResult.Return
                    ? TaskStatementExecutionResult.Return
                    : TaskStatementExecutionResult.Continue;
            }

            case TaskLanguageStatementKeys.Loop:
                return await ExecuteLoopAsync(step, context, host);

            case TaskLanguageStatementKeys.EventHandler:
            {
                if (step.ModuleTriggerKey is null)
                    throw new InvalidOperationException(
                        "Event handler step requires a module trigger key.");
                context.EventHandlers.Add(new RegisteredEventHandler(
                    step.ModuleTriggerKey,
                    step.HandlerParameter,
                    step.Body ?? []));
                await host.AppendLogAsync(
                    context.InstanceId,
                    $"Registered event handler: {step.ModuleTriggerKey}",
                    JobLogLevels.Info,
                    context.CancellationToken);
                return TaskStatementExecutionResult.Continue;
            }
        }

        return TaskStatementExecutionResult.Continue;
    }

    private async Task<TaskStatementExecutionResult> ExecuteLoopAsync(
        TaskStatementDefinition step,
        TaskPlanExecutionContext context,
        ITaskPlanExecutionHost host)
    {
        if (step.VariableName is not null)
        {
            foreach (var item in EnumerateLoopValues(step, context))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                await context.Runtime.WaitIfPausedAsync(context.CancellationToken);
                context.Variables[step.VariableName] = item;
                if (step.Body is null)
                    continue;
                var result = await ExecuteStatementDefinitionsAsync(
                    step.Body,
                    context,
                    host,
                    context.CancellationToken,
                    throwOnUnsupportedInvocation: false);
                if (result == TaskStatementResult.Return)
                    return TaskStatementExecutionResult.Return;
            }

            return TaskStatementExecutionResult.Continue;
        }

        while (_expressions.EvaluateCondition(step.Expression, context.Variables))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            await context.Runtime.WaitIfPausedAsync(context.CancellationToken);
            if (step.Body is null)
                continue;
            var result = await ExecuteStatementDefinitionsAsync(
                step.Body,
                context,
                host,
                context.CancellationToken,
                throwOnUnsupportedInvocation: false);
            if (result == TaskStatementResult.Return)
                return TaskStatementExecutionResult.Return;
        }

        return TaskStatementExecutionResult.Continue;
    }

    private IEnumerable<object?> EnumerateLoopValues(
        TaskStatementDefinition step,
        TaskPlanExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(step.Expression))
            yield break;

        if (context.Variables.TryGetValue(step.Expression, out var direct))
        {
            foreach (var item in EnumerateValue(direct))
                yield return item;
            yield break;
        }

        var resolved = _expressions.ResolveExpression(
            step.Expression,
            context.Variables);
        if (string.IsNullOrWhiteSpace(resolved))
            yield break;

        if (context.Variables.TryGetValue(resolved, out var variableValue))
        {
            foreach (var item in EnumerateValue(variableValue))
                yield return item;
            yield break;
        }

        foreach (var item in EnumerateValue(resolved))
            yield return item;
    }

    private static IEnumerable<object?> EnumerateValue(object? value)
    {
        if (value is null)
            yield break;

        if (value is string text)
        {
            if (TryEnumerateJsonArray(text, out var items))
            {
                foreach (var item in items)
                    yield return item;
                yield break;
            }

            yield return text;
            yield break;
        }

        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
                yield return item;
        }
    }

    private static bool TryEnumerateJsonArray(string text, out List<object?> values)
    {
        values = [];
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                values.Add(item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.GetRawText());
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task DelayWithPauseAsync(
        int delayMs,
        TaskPlanExecutionContext context)
    {
        const int chunkMs = 250;
        var remaining = delayMs;
        while (remaining > 0)
        {
            await context.Runtime.WaitIfPausedAsync(context.CancellationToken);
            var nextDelay = Math.Min(chunkMs, remaining);
            await Task.Delay(nextDelay, context.CancellationToken);
            remaining -= nextDelay;
        }
    }

    private async Task<TaskStatementResult> ExecuteStatementDefinitionsAsync(
        IReadOnlyList<ITaskStatementInvocation> steps,
        TaskPlanExecutionContext context,
        ITaskPlanExecutionHost host,
        CancellationToken ct,
        bool throwOnUnsupportedInvocation)
    {
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (step is not TaskStatementDefinition definition)
            {
                if (throwOnUnsupportedInvocation)
                    throw new InvalidOperationException(
                        $"Unsupported step invocation type: {step.GetType().FullName}");
                continue;
            }

            var result = await ExecuteStatementAsync(
                definition,
                context,
                host,
                ct);
            if (result == TaskStatementExecutionResult.Return)
                return TaskStatementResult.Return;
        }

        return TaskStatementResult.Continue;
    }

    private async Task ExecuteScopedStatementDefinitionsAsync(
        IReadOnlyList<ITaskStatementInvocation> steps,
        TaskPlanExecutionContext context,
        ITaskPlanExecutionHost host,
        CancellationToken ct,
        bool throwOnUnsupportedInvocation)
    {
        using var scope = _scopeFactory.CreateScope();
        using var serviceScope = context.UseServices(scope.ServiceProvider);

        await ExecuteStatementDefinitionsAsync(
            steps,
            context,
            host,
            ct,
            throwOnUnsupportedInvocation);
    }

    private static async Task EmitOutputAsync(
        TaskPlanExecutionContext context,
        ITaskPlanExecutionHost host,
        string? outputJson,
        CancellationToken ct)
    {
        var sequence = context.Runtime.IncrementSequence();
        await host.PersistOutputAsync(
            context.InstanceId,
            sequence,
            outputJson,
            ct);

        await context.Runtime.WriteEventAsync(
            TaskOutputEventType.Output,
            outputJson,
            ct);
    }

    private static async Task EmitRuntimeEventPlanAsync(
        Guid instanceId,
        TaskRuntimeEventPlan plan,
        ITaskPlanExecutionHost host,
        TaskRuntimeInstance runtime,
        CancellationToken ct)
    {
        if (plan.LogMessage is not null)
            await host.AppendLogAsync(
                instanceId,
                plan.LogMessage,
                plan.LogLevel,
                ct);

        foreach (var evt in plan.OutputEvents)
            await runtime.WriteEventAsync(evt.Type, evt.Data, ct);
    }

    private static string SanitizeForLog(string value)
        => value.Replace('\r', '_').Replace('\n', '_');

    private sealed class TaskPlanExecutionContext(
        Guid instanceId,
        CompiledTaskPlan plan,
        TaskRuntimeInstance runtime,
        CancellationToken cancellationToken)
    {
        public Guid InstanceId { get; } = instanceId;
        public Guid ChannelId { get; set; }
        public CompiledTaskPlan Plan { get; } = plan;
        public TaskRuntimeInstance Runtime { get; } = runtime;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Dictionary<string, object?> Variables { get; } = new(StringComparer.Ordinal);
        public List<RegisteredEventHandler> EventHandlers { get; } = [];
        public IServiceProvider Services { get; set; } = default!;

        public IDisposable UseServices(IServiceProvider services)
        {
            var previous = Services;
            Services = services;
            return new ServiceScopeRestore(() => Services = previous);
        }
    }

    private sealed record RegisteredEventHandler(
        string ModuleTriggerKey,
        string? ParameterName,
        IReadOnlyList<ITaskStatementInvocation> Body);

    private sealed class TaskOperationContextAdapter(
        TaskPlanExecutionContext context,
        TaskPlanExecutionEngine engine,
        ITaskPlanExecutionHost host) : ITaskOperationExecutionContext
    {
        public Guid InstanceId => context.InstanceId;
        public Guid ChannelId => context.ChannelId;
        public CancellationToken CancellationToken => context.CancellationToken;
        public IServiceProvider Services => context.Services;
        public IDictionary<string, object?> Variables => context.Variables;

        public IReadOnlyList<ITaskEventHandler> EventHandlers =>
            context.EventHandlers
                .Select(handler => (ITaskEventHandler)new EventHandlerAdapter(
                    handler,
                    context,
                    engine,
                    host))
                .ToList();

        public string ResolveExpression(string expression) =>
            engine._expressions.ResolveExpression(
                expression,
                context.Variables);

        public Task AppendLogAsync(string message) =>
            host.AppendLogAsync(
                context.InstanceId,
                message,
                JobLogLevels.Info,
                context.CancellationToken);

        public Task WriteOutputAsync(string? outputJson) =>
            EmitOutputAsync(
                context,
                host,
                outputJson,
                context.CancellationToken);

        public void SetChannelId(Guid channelId) =>
            context.ChannelId = channelId;

        public Task WaitIfPausedAsync() =>
            context.Runtime.WaitIfPausedAsync(context.CancellationToken);

        public bool EvaluateCondition(string? expression) =>
            engine._expressions.EvaluateCondition(
                expression,
                context.Variables);

        public void RegisterEventHandler(
            string moduleTriggerKey,
            string? parameterName,
            IReadOnlyList<ITaskStatementInvocation> body)
        {
            context.EventHandlers.Add(new RegisteredEventHandler(
                moduleTriggerKey,
                parameterName,
                body));
        }

        public async Task<TaskStatementResult> ExecuteStatementsAsync(
            IReadOnlyList<ITaskStatementInvocation> steps,
            CancellationToken cancellationToken)
        {
            using var scope = engine._scopeFactory.CreateScope();
            using var serviceScope = context.UseServices(scope.ServiceProvider);

            return await engine.ExecuteStatementDefinitionsAsync(
                steps,
                context,
                host,
                cancellationToken,
                throwOnUnsupportedInvocation: true);
        }
    }

    private sealed class EventHandlerAdapter(
        RegisteredEventHandler handler,
        TaskPlanExecutionContext context,
        TaskPlanExecutionEngine engine,
        ITaskPlanExecutionHost host) : ITaskEventHandler
    {
        public string? ModuleTriggerKey => handler.ModuleTriggerKey;
        public string? ParameterName => handler.ParameterName;

        public async Task ExecuteBodyAsync(CancellationToken ct)
        {
            await engine.ExecuteScopedStatementDefinitionsAsync(
                handler.Body,
                context,
                host,
                ct,
                throwOnUnsupportedInvocation: false);
        }
    }

    private sealed class ServiceScopeRestore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private enum TaskStatementExecutionResult
    {
        Continue,
        Return,
    }
}

/// <summary>
/// Input required to execute one compiled task plan.
/// </summary>
public sealed record TaskPlanExecutionRequest(
    Guid InstanceId,
    CompiledTaskPlan Plan,
    TaskRuntimeInstance Runtime,
    IServiceProvider Services,
    ITaskPlanExecutionHost Host,
    CancellationToken CancellationToken);

/// <summary>
/// Result of a completed task-plan execution attempt.
/// </summary>
public sealed record TaskPlanExecutionOutcome(
    TaskInstanceStatus Status,
    string? Error,
    TimeSpan Elapsed);

/// <summary>
/// Host adapter used by the Core task execution engine for persistence and
/// runtime side effects.
/// </summary>
public interface ITaskPlanExecutionHost
{
    /// <summary>
    /// Loads the task instance's initial channel id, or null for context-only
    /// starts.
    /// </summary>
    Task<Guid?> LoadInitialChannelIdAsync(
        Guid instanceId,
        CancellationToken ct);

    /// <summary>
    /// Persists one task output payload with the supplied sequence number.
    /// </summary>
    Task PersistOutputAsync(
        Guid instanceId,
        long sequence,
        string? outputJson,
        CancellationToken ct);

    /// <summary>
    /// Persists the current shared-data snapshots for an instance.
    /// </summary>
    Task PersistSharedDataSnapshotAsync(
        Guid instanceId,
        string? lightSnapshot,
        string? bigSnapshotJson,
        CancellationToken ct);

    /// <summary>
    /// Appends a log entry for a task instance.
    /// </summary>
    Task AppendLogAsync(
        Guid instanceId,
        string message,
        string level,
        CancellationToken ct);

    /// <summary>
    /// Marks a task instance as a non-failed terminal status.
    /// </summary>
    Task MarkTerminalStatusAsync(
        Guid instanceId,
        TaskInstanceStatus status,
        CancellationToken ct);

    /// <summary>
    /// Marks a task instance as failed with the supplied error.
    /// </summary>
    Task MarkFailedAsync(
        Guid instanceId,
        string error,
        CancellationToken ct);
}
