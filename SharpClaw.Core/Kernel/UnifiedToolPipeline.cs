using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

public sealed class UnifiedToolPipeline : IUnifiedToolPipeline
{
    private static readonly SharpClawActionKey Parse = new("tool.call.parse");
    private static readonly SharpClawActionKey InputTransform = new("tool.call.input.transform");
    private static readonly SharpClawActionKey Defer = new("tool.call.defer");
    private static readonly SharpClawActionKey ResultTransform = new("tool.result.transform");
    private static readonly SharpClawActionKey ResultReturn = new("tool.result.return");
    private static readonly SharpClawActionKey Failure = new("tool.call.fail");
    private static readonly SharpClawActionKey Cancellation = new("tool.call.cancel");
    private readonly KernelGraph _graph;
    private readonly KernelActionDispatcher _dispatcher;
    private readonly IReadOnlyList<IToolInvocationGate> _gates;
    private readonly IToolExecutionCoordinator _coordinator;
    private readonly IServiceProvider? _serviceProvider;

    public UnifiedToolPipeline(
        KernelGraph graph,
        KernelActionDispatcher dispatcher,
        IEnumerable<IToolInvocationGate>? gates = null,
        IToolExecutionCoordinator? coordinator = null,
        IServiceProvider? serviceProvider = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _gates = gates?.ToArray() ?? [];
        _coordinator = coordinator ?? new ImmediateToolExecutionCoordinator();
        _serviceProvider = serviceProvider ?? graph.Modules.Services;
    }

    public IReadOnlyList<ToolDescriptor> Tools =>
        _graph.Tools.Select(tool => tool.Descriptor).ToArray();

    public async ValueTask<ToolInvocationOutcome> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var descriptor = _graph.GetStandardAction(SharpClawActions.Tools.Invoke);
        var outcome = await _dispatcher.RunAsync(
            descriptor,
            new KernelActionEnvelope(SharpClawActions.Tools.Invoke, invocation),
            async (envelope, ct) => await ExecutePipelineAsync(
                ExtractInput<ToolInvocation>(envelope),
                ct),
            _graph.ActionSnapshot,
            cancellationToken);

        var result = outcome.Kind == ActionOutcomeKind.Completed && outcome.Result is ToolInvocationOutcome completed
            ? completed
            : outcome.Kind switch
            {
                ActionOutcomeKind.Cancelled => new ToolInvocationOutcome(
                    ActionOutcomeKind.Cancelled,
                    Error: outcome.Error ?? new ExecutionError(
                        "TOOL_CANCELLED",
                        "The tool invocation was cancelled.")),
                ActionOutcomeKind.Deferred => new ToolInvocationOutcome(
                    ActionOutcomeKind.Deferred,
                    Continuation: outcome.Continuation),
                ActionOutcomeKind.Uncertain => new ToolInvocationOutcome(
                    ActionOutcomeKind.Uncertain,
                    Uncertainty: outcome.Uncertainty),
                _ => new ToolInvocationOutcome(
                    ActionOutcomeKind.Failed,
                    Error: outcome.Error ?? new ExecutionError(
                        "TOOL_PIPELINE_FAILED",
                        "The tool pipeline failed."))
            };
        if (result.Kind == ActionOutcomeKind.Cancelled)
            await TryDispatchTerminalAsync(Cancellation, result);
        else if (result.Kind == ActionOutcomeKind.Failed)
            await TryDispatchTerminalAsync(Failure, result);
        return result;
    }

    private async ValueTask<object> ExecutePipelineAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        invocation = (ToolInvocation)await RunPhaseAsync(
            Parse,
            invocation,
            static (value, _) => ValueTask.FromResult<object>(value),
            cancellationToken);
        invocation = (ToolInvocation)await RunPhaseAsync(
            InputTransform,
            invocation,
            static (value, _) => ValueTask.FromResult<object>(value),
            cancellationToken);
        var resolution = await ResolveToolAsync(invocation, cancellationToken);
        if (resolution.SelectedToolName is null)
            return ToolInvocationOutcome.Rejected(
                "TOOL_NOT_REGISTERED",
                $"No handler is registered for tool '{resolution.Invocation.ToolName}'.");

        var checkResult = await RunPhaseAsync(
            SharpClawActions.Tools.Check,
            resolution.Invocation,
            async (effectiveInvocation, ct) =>
            {
                var holds = new List<ToolHoldRequirement>();
                foreach (var gate in _gates)
                {
                    var decision = await gate.EvaluateAsync(effectiveInvocation, ct);
                    switch (decision)
                    {
                        case ToolGateDecision.Reject reject:
                            return new KernelToolCheckResult(
                                effectiveInvocation,
                                holds.ToArray(),
                                new ExecutionError(reject.Code, reject.Message));
                        case ToolGateDecision.Hold hold:
                            holds.Add(hold.Requirement);
                            break;
                    }
                }

                return new KernelToolCheckResult(effectiveInvocation, holds.ToArray(), null);
            },
            cancellationToken);
        var checkedInput = checkResult as KernelToolCheckResult
            ?? throw new KernelActionExecutionException("The tool check action returned an invalid result.");
        if (checkedInput.Rejection is { } rejection)
            return ToolInvocationOutcome.Rejected(rejection.Code, rejection.Message);
        var registration = await ResolveToolAsync(checkedInput.Invocation, cancellationToken);
        if (registration.SelectedToolName is null)
            return ToolInvocationOutcome.Rejected(
                "TOOL_NOT_REGISTERED",
                $"No handler is registered for tool '{registration.Invocation.ToolName}'.");

        if (checkedInput.Holds.Count > 0)
        {
            var deferred = await RunPhaseAsync(
                Defer,
                checkedInput.Invocation,
                static (value, _) => ValueTask.FromResult<object>(value),
                cancellationToken);
            checkedInput = checkedInput with { Invocation = (ToolInvocation)deferred };
        }

        var coordinated = await RunPhaseAsync(
            SharpClawActions.Tools.Coordinate,
            checkedInput.Invocation,
            async (effectiveInvocation, ct) =>
            {
                var plan = new ToolExecutionPlan(effectiveInvocation, checkedInput.Holds);
                ToolExecutionDelegate terminal = async (handlerInvocation, handlerCt) =>
                {
                    var handlerResult = await RunPhaseAsync(
                        SharpClawActions.Tools.InvokeHandler,
                        handlerInvocation,
                        async (effectiveHandlerInvocation, handlerCancellationToken) =>
                        {
                            var handlerResolution = await ResolveToolAsync(
                                effectiveHandlerInvocation,
                                handlerCancellationToken);
                            if (handlerResolution.SelectedToolName is null)
                                return (object)ToolResult.Error(
                                    $"No handler is registered for tool '{handlerResolution.Invocation.ToolName}'.");
                            var handlerRegistration = _graph.Tools.Single(tool =>
                                string.Equals(
                                    tool.Descriptor.Name,
                                    handlerResolution.SelectedToolName,
                                    StringComparison.Ordinal));
                            var handler = KernelServiceResolution.Resolve(
                                handlerRegistration.HandlerType,
                                _serviceProvider);
                            if (handler is not IToolHandler typedHandler)
                                throw new KernelActionExecutionException(
                                    $"Tool handler '{handlerRegistration.HandlerType.FullName}' does not implement IToolHandler.");
                            return (object)await typedHandler.InvokeAsync(
                                effectiveHandlerInvocation,
                                handlerCancellationToken);
                        },
                        handlerCt);
                    var transformed = await RunPhaseAsync(
                        ResultTransform,
                        new KernelToolResultStage(
                            handlerInvocation,
                            handlerResult as ToolResult
                            ?? ToolResult.Error("The tool handler returned no result.")),
                        static (stage, _) => ValueTask.FromResult<object>(stage.Result),
                        handlerCt);
                    var returned = await RunPhaseAsync(
                        ResultReturn,
                        new KernelToolResultStage(
                            handlerInvocation,
                            transformed as ToolResult
                            ?? ToolResult.Error("The tool result transform returned no result.")),
                        static (stage, _) => ValueTask.FromResult<object>(stage.Result),
                        handlerCt);
                    return returned as ToolResult
                        ?? ToolResult.Error("The tool handler returned no result.");
                };
                return (object)await _coordinator.CoordinateAsync(plan, terminal, ct);
            },
            cancellationToken);
        return coordinated is ToolInvocationOutcome result
            ? result
            : ToolInvocationOutcome.Rejected("TOOL_COORDINATION_FAILED", "The tool coordinator returned no result.");
    }

    private async ValueTask<KernelToolResolution> ResolveToolAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var result = await RunPhaseAsync(
            SharpClawActions.Tools.Resolve,
            invocation,
            (effectiveInvocation, _) => new ValueTask<object>(new KernelToolResolution(
                effectiveInvocation,
                _graph.Tools.FirstOrDefault(tool =>
                    string.Equals(tool.Descriptor.Name, effectiveInvocation.ToolName, StringComparison.Ordinal))
                    ?.Descriptor.Name)),
            cancellationToken);
        return result as KernelToolResolution
            ?? throw new KernelActionExecutionException("The tool resolve action returned an invalid result.");
    }

    private ValueTask<object> RunPhaseAsync<TInput>(
        SharpClawActionKey key,
        TInput input,
        Func<TInput, CancellationToken, ValueTask<object>> terminal,
        CancellationToken cancellationToken) =>
        RunPhaseCoreAsync(key, input, terminal, cancellationToken);

    private async ValueTask<object> RunPhaseCoreAsync<TInput>(
        SharpClawActionKey key,
        TInput input,
        Func<TInput, CancellationToken, ValueTask<object>> terminal,
        CancellationToken cancellationToken)
    {
        var descriptor = _graph.GetStandardAction(key);
        return await _dispatcher.RunRequiredAsync(
            descriptor,
            new KernelActionEnvelope(key, input),
            async (envelope, ct) => await terminal(ExtractInput<TInput>(envelope), ct),
            _graph.ActionSnapshot,
            cancellationToken);
    }

    private async ValueTask TryDispatchTerminalAsync(
        SharpClawActionKey key,
        ToolInvocationOutcome outcome)
    {
        try
        {
            var descriptor = _graph.GetStandardAction(key);
            await _dispatcher.RunAsync(
                descriptor,
                new KernelActionEnvelope(key, outcome),
                static (_, _) => ValueTask.FromResult<object>(true),
                _graph.ActionSnapshot,
                CancellationToken.None);
        }
        catch
        {
        }
    }

    private static TInput ExtractInput<TInput>(KernelActionEnvelope envelope) =>
        envelope.Payload switch
        {
            TInput typed => typed,
            KernelActionEnvelope nested when nested.Payload is TInput typed => typed,
            _ => throw new KernelActionExecutionException(
                $"Action '{envelope.Key.Value}' returned an invalid tool input replacement.")
        };

}

public sealed record KernelToolResolution(
    ToolInvocation Invocation,
    string? SelectedToolName);

public sealed record KernelToolCheckResult(
    ToolInvocation Invocation,
    IReadOnlyList<ToolHoldRequirement> Holds,
    ExecutionError? Rejection);

public sealed record KernelToolResultStage(ToolInvocation Invocation, ToolResult Result);

public sealed class ImmediateToolExecutionCoordinator : IToolExecutionCoordinator
{
    public ValueTask<ToolInvocationOutcome> CoordinateAsync(
        ToolExecutionPlan plan,
        ToolExecutionDelegate terminal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(terminal);
        if (plan.Holds.Count > 0)
        {
            var requirement = plan.Holds[0];
            return ValueTask.FromResult(ToolInvocationOutcome.Rejected(
                requirement.Code,
                $"The tool invocation requires approval: {requirement.Description}."));
        }

        return CompleteAsync();

        async ValueTask<ToolInvocationOutcome> CompleteAsync() =>
            ToolInvocationOutcome.Completed(await terminal(plan.Invocation, cancellationToken));
    }
}
