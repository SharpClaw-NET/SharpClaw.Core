using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Jobs;

namespace SharpClaw.Core.Modules;

/// <summary>
/// Host-neutral executor for module job-pipeline tools.
/// </summary>
public sealed class ModuleJobToolExecutor(
    ModuleMetricsCollector metricsCollector,
    ILogger<ModuleJobToolExecutor> logger)
{
    private const int DefaultTimeoutSeconds = 30;

    private readonly ModuleMetricsCollector _metricsCollector = metricsCollector
        ?? throw new ArgumentNullException(nameof(metricsCollector));
    private readonly ILogger<ModuleJobToolExecutor> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Executes a module job-pipeline tool with standard SharpClaw runtime
    /// host, restricted-scope, timeout, streaming fallback, metrics, and
    /// failure-sanitization semantics.
    /// </summary>
    public async Task<ModuleJobToolExecutionResult> ExecuteAsync(
        ModuleJobToolExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Job);
        ArgumentNullException.ThrowIfNull(request.Plan);
        ArgumentNullException.ThrowIfNull(request.ModuleRegistry);
        ArgumentNullException.ThrowIfNull(request.CreateHostScope);
        ArgumentNullException.ThrowIfNull(request.BlockedServiceTypes);

        var job = request.Job;
        var plan = request.Plan;
        var module = request.ModuleRegistry.GetModule(plan.ModuleId)
            ?? throw new InvalidOperationException(
                $"Module '{plan.ModuleId}' is not loaded.");
        var prefixedToolName = $"{module.ToolPrefix}_{plan.ToolName}";

        var jobContext = new AgentJobContext(
            JobId: job.Id,
            AgentId: job.AgentId,
            ChannelId: job.ChannelId,
            ResourceId: job.ResourceId,
            ActionKey: job.ActionKey);

        var runtimeHost = request.ModuleRegistry.GetRuntimeHost(plan.ModuleId);
        if (runtimeHost is not null && !runtimeHost.TryAcquireExecution())
            throw new InvalidOperationException(
                $"Module '{plan.ModuleId}' is unloading - cannot execute tools.");

        var sw = Stopwatch.StartNew();
        try
        {
            using var scope = runtimeHost is not null
                ? runtimeHost.CreateScope()
                : request.CreateHostScope();

            var execCtx = scope.ServiceProvider.GetService<ModuleExecutionContext>();
            if (execCtx is not null)
                execCtx.ModuleId = module.Id;

            var restrictedScope = new ModuleServiceScope(
                scope.ServiceProvider,
                module.Id,
                request.BlockedServiceTypes);

            var completionBehavior = module.GetJobCompletionBehavior(
                plan.ToolName,
                plan.Parameters,
                jobContext);
            var timeoutSeconds = ResolveTimeoutSeconds(
                request.ModuleRegistry,
                plan.ModuleId,
                plan.ToolName);

            var dispatchLog = BuildDispatchResolvedLog(
                job.ActionKey,
                plan,
                timeoutSeconds);
            request.AddInfoLog?.Invoke(dispatchLog);

            _logger.LogInformation(
                "Dispatching agent job {JobId}: action {ActionKey} -> module {ModuleId}.{ToolName} with timeout {TimeoutSeconds}s.",
                job.Id,
                job.ActionKey,
                plan.ModuleId,
                plan.ToolName,
                timeoutSeconds);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                var (result, streamingFallbackUsed) = await ExecuteModuleToolAsync(
                    module,
                    plan,
                    jobContext,
                    restrictedScope,
                    request.IsStreamingNotSupportedException,
                    cts.Token);

                sw.Stop();
                _metricsCollector.RecordSuccess(prefixedToolName, sw.Elapsed);
                _logger.LogDebug(
                    "Module tool {ModuleId}.{ToolName} completed in {ElapsedMs}ms for job {JobId}. CompletionBehavior={CompletionBehavior}",
                    plan.ModuleId,
                    plan.ToolName,
                    sw.ElapsedMilliseconds,
                    job.Id,
                    completionBehavior);

                return new ModuleJobToolExecutionResult(
                    result,
                    completionBehavior,
                    timeoutSeconds,
                    sw.Elapsed,
                    streamingFallbackUsed);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                sw.Stop();
                _metricsCollector.RecordTimeout(prefixedToolName);
                _logger.LogWarning(
                    "Module tool {ModuleId}.{ToolName} timed out after {TimeoutSeconds}s for job {JobId}.",
                    plan.ModuleId,
                    plan.ToolName,
                    timeoutSeconds,
                    job.Id);
                throw new InvalidOperationException(
                    $"Module tool '{plan.ModuleId}.{plan.ToolName}' " +
                    $"exceeded timeout ({timeoutSeconds}s).");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
            {
                sw.Stop();
                _metricsCollector.RecordFailure(prefixedToolName);
                _logger.LogError(
                    ex,
                    "Module tool {ModuleId}.{ToolName} failed for job {JobId}.",
                    plan.ModuleId,
                    plan.ToolName,
                    job.Id);
                throw new InvalidOperationException(
                    $"[{ex.GetType().Name}] " +
                    ModuleToolExceptionSanitizer.Sanitize(
                        plan.ModuleId,
                        plan.ToolName,
                        ex.Message),
                    ex);
            }
        }
        finally
        {
            runtimeHost?.ReleaseExecution();
        }
    }

    /// <summary>
    /// Resolves the standard SharpClaw timeout chain: tool override,
    /// manifest default, then 30 seconds.
    /// </summary>
    public static int ResolveTimeoutSeconds(
        ModuleRegistry moduleRegistry,
        string moduleId,
        string toolName)
    {
        ArgumentNullException.ThrowIfNull(moduleRegistry);

        var manifest = moduleRegistry.GetManifest(moduleId);
        var toolTimeout = moduleRegistry.GetToolTimeout(moduleId, toolName);
        return toolTimeout ?? manifest?.ExecutionTimeoutSeconds ?? DefaultTimeoutSeconds;
    }

    /// <summary>
    /// Builds the standard persisted job-log entry for resolved module
    /// dispatch.
    /// </summary>
    public static string BuildDispatchResolvedLog(
        string? actionKey,
        ModuleToolExecutionPlan plan,
        int timeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return $"Module dispatch resolved: {actionKey ?? plan.ToolName} -> " +
            $"{plan.ModuleId}.{plan.ToolName} (timeout {timeoutSeconds}s).";
    }

    private async Task<(string Result, bool StreamingFallbackUsed)> ExecuteModuleToolAsync(
        ISharpClawCoreModule module,
        ModuleToolExecutionPlan plan,
        AgentJobContext jobContext,
        IServiceProvider scopedServices,
        Func<Exception, bool>? isStreamingNotSupportedException,
        CancellationToken ct)
    {
        var stream = module.ExecuteToolStreamingAsync(
            plan.ToolName,
            plan.Parameters,
            jobContext,
            scopedServices,
            ct);

        if (stream is not null)
        {
            var sb = new StringBuilder();
            try
            {
                await foreach (var chunk in stream.WithCancellation(ct))
                    sb.Append(chunk);

                return (sb.ToString(), StreamingFallbackUsed: false);
            }
            catch (Exception ex) when (isStreamingNotSupportedException?.Invoke(ex) == true)
            {
                _logger.LogDebug(
                    "Module tool {ModuleId}.{ToolName} is not streaming; falling back to normal execution for job {JobId}.",
                    plan.ModuleId,
                    plan.ToolName,
                    jobContext.JobId);
                var fallbackResult = await module.ExecuteToolAsync(
                    plan.ToolName,
                    plan.Parameters,
                    jobContext,
                    scopedServices,
                    ct);
                return (fallbackResult, StreamingFallbackUsed: true);
            }
        }

        var result = await module.ExecuteToolAsync(
            plan.ToolName,
            plan.Parameters,
            jobContext,
            scopedServices,
            ct);
        return (result, StreamingFallbackUsed: false);
    }
}

/// <summary>
/// Inputs required by Core to execute one module job-pipeline tool.
/// </summary>
public sealed record ModuleJobToolExecutionRequest(
    AgentJobState Job,
    ModuleToolExecutionPlan Plan,
    ModuleRegistry ModuleRegistry,
    Func<IServiceScope> CreateHostScope,
    IReadOnlyCollection<Type> BlockedServiceTypes,
    Action<string>? AddInfoLog,
    Func<Exception, bool>? IsStreamingNotSupportedException = null);

/// <summary>
/// Result of a module job-pipeline tool execution.
/// </summary>
public sealed record ModuleJobToolExecutionResult(
    string? ResultData,
    ModuleJobCompletionBehavior CompletionBehavior,
    int TimeoutSeconds,
    TimeSpan Elapsed,
    bool StreamingFallbackUsed);
