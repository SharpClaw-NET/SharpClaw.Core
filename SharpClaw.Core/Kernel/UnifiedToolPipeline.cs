using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

public sealed class UnifiedToolPipeline : IUnifiedToolPipeline
{
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
        _serviceProvider = serviceProvider;
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
                ExtractInput(envelope, invocation),
                ct),
            _graph.ActionSnapshot,
            cancellationToken);

        if (outcome.Kind == ActionOutcomeKind.Completed && outcome.Result is ToolInvocationOutcome completed)
            return completed;
        return outcome.Kind switch
        {
            ActionOutcomeKind.Cancelled => ToolInvocationOutcome.Rejected(
                outcome.Error?.Code ?? "TOOL_CANCELLED",
                outcome.Error?.Message ?? "The tool invocation was cancelled."),
            ActionOutcomeKind.Deferred => new ToolInvocationOutcome(
                ActionOutcomeKind.Deferred,
                ToolResult.Text("The tool invocation was deferred."),
                null,
                outcome.Continuation,
                null),
            ActionOutcomeKind.Uncertain => new ToolInvocationOutcome(
                ActionOutcomeKind.Uncertain,
                ToolResult.Error("The tool invocation has an uncertain outcome."),
                null,
                null,
                outcome.Uncertainty),
            _ => ToolInvocationOutcome.Rejected(
                outcome.Error?.Code ?? "TOOL_PIPELINE_FAILED",
                outcome.Error?.Message ?? "The tool pipeline failed.")
        };
    }

    private async ValueTask<object> ExecutePipelineAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveToolAsync(invocation, cancellationToken);
        if (resolution.Registration is null)
            return ToolInvocationOutcome.Rejected(
                "TOOL_NOT_REGISTERED",
                $"No handler is registered for tool '{resolution.Invocation.ToolName}'.");

        var holds = new List<ToolHoldRequirement>();
        var checkResult = await RunPhaseAsync(
            SharpClawActions.Tools.Check,
            resolution.Invocation,
            async (effectiveInvocation, ct) =>
            {
                foreach (var gate in _gates)
                {
                    var decision = await gate.EvaluateAsync(effectiveInvocation, ct);
                    switch (decision)
                    {
                        case ToolGateDecision.Reject reject:
                            return (object)ToolInvocationOutcome.Rejected(reject.Code, reject.Message);
                        case ToolGateDecision.Hold hold:
                            holds.Add(hold.Requirement);
                            break;
                    }
                }

                return new ToolCheckResult(effectiveInvocation, holds);
            },
            cancellationToken);
        if (checkResult is ToolInvocationOutcome rejected)
            return rejected;

        var checkedInput = checkResult as ToolCheckResult
            ?? throw new KernelActionExecutionException("The tool check action returned an invalid result.");
        var registration = await ResolveToolAsync(checkedInput.Invocation, cancellationToken);
        if (registration.Registration is null)
            return ToolInvocationOutcome.Rejected(
                "TOOL_NOT_REGISTERED",
                $"No handler is registered for tool '{registration.Invocation.ToolName}'.");

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
                            if (handlerResolution.Registration is null)
                                return (object)ToolResult.Error(
                                    $"No handler is registered for tool '{handlerResolution.Invocation.ToolName}'.");
                            var handler = KernelServiceResolution.Resolve(
                                handlerResolution.Registration.HandlerType,
                                _serviceProvider);
                            if (handler is not IToolHandler typedHandler)
                                throw new KernelActionExecutionException(
                                    $"Tool handler '{handlerResolution.Registration.HandlerType.FullName}' does not implement IToolHandler.");
                            return (object)await typedHandler.InvokeAsync(
                                effectiveHandlerInvocation,
                                handlerCancellationToken);
                        },
                        handlerCt);
                    return handlerResult as ToolResult
                        ?? ToolResult.Error("The tool handler returned no result.");
                };
                return (object)await _coordinator.CoordinateAsync(plan, terminal, ct);
            },
            cancellationToken);
        return coordinated is ToolInvocationOutcome result
            ? result
            : ToolInvocationOutcome.Rejected("TOOL_COORDINATION_FAILED", "The tool coordinator returned no result.");
    }

    private async ValueTask<ToolResolution> ResolveToolAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var result = await RunPhaseAsync(
            SharpClawActions.Tools.Resolve,
            invocation,
            (effectiveInvocation, _) => new ValueTask<object>(new ToolResolution(
                effectiveInvocation,
                _graph.Tools.FirstOrDefault(tool =>
                    string.Equals(tool.Descriptor.Name, effectiveInvocation.ToolName, StringComparison.Ordinal)))),
            cancellationToken);
        return result as ToolResolution
            ?? throw new KernelActionExecutionException("The tool resolve action returned an invalid result.");
    }

    private ValueTask<object> RunPhaseAsync(
        SharpClawActionKey key,
        ToolInvocation invocation,
        Func<ToolInvocation, CancellationToken, ValueTask<object>> terminal,
        CancellationToken cancellationToken) =>
        RunPhaseCoreAsync(key, invocation, terminal, cancellationToken);

    private async ValueTask<object> RunPhaseCoreAsync(
        SharpClawActionKey key,
        ToolInvocation invocation,
        Func<ToolInvocation, CancellationToken, ValueTask<object>> terminal,
        CancellationToken cancellationToken)
    {
        var descriptor = _graph.GetStandardAction(key);
        var outcome = await _dispatcher.RunAsync(
            descriptor,
            new KernelActionEnvelope(key, invocation),
            async (envelope, ct) => await terminal(ExtractInput(envelope, invocation), ct),
            _graph.ActionSnapshot,
            cancellationToken);
        if (outcome.Kind == ActionOutcomeKind.Completed)
            return outcome.Result!;
        return ToolInvocationOutcome.Rejected(
            outcome.Error?.Code ?? "TOOL_PHASE_FAILED",
            outcome.Error?.Message ?? $"Tool phase '{key.Value}' failed.");
    }

    private static ToolInvocation ExtractInput(
        KernelActionEnvelope envelope,
        ToolInvocation original) =>
        envelope.Payload switch
        {
            ToolInvocation typed => typed,
            KernelActionEnvelope nested when nested.Payload is ToolInvocation typed => typed,
            _ => throw new KernelActionExecutionException(
                $"Action '{envelope.Key.Value}' returned an invalid tool invocation replacement.")
        };

    private sealed record ToolResolution(
        ToolInvocation Invocation,
        KernelToolRegistration? Registration);

    private sealed record ToolCheckResult(
        ToolInvocation Invocation,
        IReadOnlyList<ToolHoldRequirement> Holds);
}

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
