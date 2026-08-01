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
            async (_, ct) => await ExecutePipelineAsync(invocation, ct),
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
        var registration = await ResolveToolAsync(invocation, cancellationToken);
        if (registration is null)
            return ToolInvocationOutcome.Rejected(
                "TOOL_NOT_REGISTERED",
                $"No handler is registered for tool '{invocation.ToolName}'.");

        var holds = new List<ToolHoldRequirement>();
        var checkResult = await RunPhaseAsync(
            SharpClawActions.Tools.Check,
            invocation,
            async (_, ct) =>
            {
                foreach (var gate in _gates)
                {
                    var decision = await gate.EvaluateAsync(invocation, ct);
                    switch (decision)
                    {
                        case ToolGateDecision.Reject reject:
                            return (object)ToolInvocationOutcome.Rejected(reject.Code, reject.Message);
                        case ToolGateDecision.Hold hold:
                            holds.Add(hold.Requirement);
                            break;
                    }
                }

                return (object)null!;
            },
            cancellationToken);
        if (checkResult is ToolInvocationOutcome rejected)
            return rejected;

        var plan = new ToolExecutionPlan(invocation, holds);
        ToolExecutionDelegate terminal = async (_, ct) =>
        {
            var handlerResult = await RunPhaseAsync(
                SharpClawActions.Tools.InvokeHandler,
                invocation,
                async (_, handlerCancellationToken) =>
                {
                    var handler = KernelServiceResolution.Resolve(registration.HandlerType, _serviceProvider);
                    if (handler is not IToolHandler typedHandler)
                        throw new KernelActionExecutionException(
                            $"Tool handler '{registration.HandlerType.FullName}' does not implement IToolHandler.");
                    return (object)await typedHandler.InvokeAsync(invocation, handlerCancellationToken);
                },
                ct);
            return handlerResult as ToolResult
                ?? ToolResult.Error("The tool handler returned no result.");
        };

        var coordinated = await RunPhaseAsync(
            SharpClawActions.Tools.Coordinate,
            invocation,
            async (_, ct) => (object)await _coordinator.CoordinateAsync(plan, terminal, ct),
            cancellationToken);
        return coordinated is ToolInvocationOutcome result
            ? result
            : ToolInvocationOutcome.Rejected("TOOL_COORDINATION_FAILED", "The tool coordinator returned no result.");
    }

    private async ValueTask<KernelToolRegistration?> ResolveToolAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var result = await RunPhaseAsync(
            SharpClawActions.Tools.Resolve,
            invocation,
            (_, _) => new ValueTask<object>(_graph.Tools.FirstOrDefault(tool =>
                string.Equals(tool.Descriptor.Name, invocation.ToolName, StringComparison.Ordinal))!),
            cancellationToken);
        return result as KernelToolRegistration;
    }

    private ValueTask<object> RunPhaseAsync(
        SharpClawActionKey key,
        ToolInvocation invocation,
        Func<KernelActionEnvelope, CancellationToken, ValueTask<object>> terminal,
        CancellationToken cancellationToken) =>
        RunPhaseCoreAsync(key, invocation, terminal, cancellationToken);

    private async ValueTask<object> RunPhaseCoreAsync(
        SharpClawActionKey key,
        ToolInvocation invocation,
        Func<KernelActionEnvelope, CancellationToken, ValueTask<object>> terminal,
        CancellationToken cancellationToken)
    {
        var descriptor = _graph.GetStandardAction(key);
        var outcome = await _dispatcher.RunAsync(
            descriptor,
            new KernelActionEnvelope(key, invocation),
            terminal,
            _graph.ActionSnapshot,
            cancellationToken);
        if (outcome.Kind == ActionOutcomeKind.Completed)
            return outcome.Result!;
        return ToolInvocationOutcome.Rejected(
            outcome.Error?.Code ?? "TOOL_PHASE_FAILED",
            outcome.Error?.Message ?? $"Tool phase '{key.Value}' failed.");
    }
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
