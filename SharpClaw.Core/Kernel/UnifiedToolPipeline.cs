using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

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
    private readonly IReadOnlyList<IToolInvocationGate>? _gates;
    private readonly IToolExecutionCoordinator? _coordinator;
    private readonly IServiceProvider _serviceProvider;

    public UnifiedToolPipeline(
        KernelGraph graph,
        KernelActionDispatcher dispatcher,
        IEnumerable<IToolInvocationGate>? gates = null,
        IToolExecutionCoordinator? coordinator = null,
        IServiceProvider? serviceProvider = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _gates = gates?.ToArray();
        _coordinator = coordinator;
        _serviceProvider = serviceProvider ?? graph.Services.Services;
    }

    public IReadOnlyList<ToolDescriptor> Tools =>
        _graph.Tools.Select(tool => tool.Descriptor).ToArray();

    public async ValueTask<ToolInvocationOutcome> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!IsWellFormed(invocation))
            return ToolInvocationOutcome.Rejected(
                "TOOL_INVOCATION_INVALID",
                "The Tool invocation does not contain valid host-issued authority.");

        var authority = ToolAuthorityTuple.Capture(invocation);

        var descriptor = _graph.GetStandardAction(SharpClawActions.Tools.Invoke);
        var outcome = await _dispatcher.RunAsync(
            descriptor,
            new KernelActionEnvelope(SharpClawActions.Tools.Invoke, invocation),
            async (context, ct) =>
            {
                var effectiveInvocation = ExtractInput<ToolInvocation>(context.Action);
                var invalid = ValidateEffectiveInvocation(effectiveInvocation, authority);
                return invalid is not null
                    ? invalid
                    : await ExecutePipelineAsync(effectiveInvocation, authority, ct);
            },
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

    private static bool IsWellFormed(ToolInvocation invocation)
    {
        try
        {
            return invocation.IsWellFormed(DateTimeOffset.UtcNow);
        }
        catch
        {
            return false;
        }
    }

    private async ValueTask<object> ExecutePipelineAsync(
        ToolInvocation invocation,
        ToolAuthorityTuple authority,
        CancellationToken cancellationToken)
    {
        invocation = (ToolInvocation)await RunPhaseAsync(
            Parse,
            invocation,
            static (value, _) => ValueTask.FromResult<object>(value),
            cancellationToken);
        var invalid = ValidateEffectiveInvocation(invocation, authority);
        if (invalid is not null)
            return invalid;

        invocation = (ToolInvocation)await RunPhaseAsync(
            InputTransform,
            invocation,
            static (value, _) => ValueTask.FromResult<object>(value),
            cancellationToken);
        invalid = ValidateEffectiveInvocation(invocation, authority);
        if (invalid is not null)
            return invalid;

        var resolution = await ResolveToolAsync(invocation, authority, cancellationToken);
        if (resolution is null)
            return ToolInvocationOutcome.Rejected(
                "TOOL_INVOCATION_AUTHORITY_CHANGED",
                "The resolved tool handler is outside host-bound authority.");
        if (resolution.SelectedToolName is null)
            return ToolInvocationOutcome.Rejected(
                "TOOL_NOT_REGISTERED",
                $"No handler is registered for tool '{resolution.Invocation.ToolName}'.");

        var checkResult = await RunPhaseAsync(
            SharpClawActions.Tools.Check,
            resolution.Invocation,
            async (effectiveInvocation, ct) =>
            {
                EnsureEffectiveInvocation(effectiveInvocation, authority, "check");
                var holds = new List<ToolHoldRequirement>();
                var services = KernelExecutionScope.Current(_serviceProvider);
                foreach (var gate in _gates ?? services.GetServices<IToolInvocationGate>().ToArray())
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
        invalid = ValidateEffectiveInvocation(checkedInput.Invocation, authority);
        if (invalid is not null)
            return invalid;
        if (checkedInput.Rejection is { } rejection)
            return ToolInvocationOutcome.Rejected(rejection.Code, rejection.Message);
        var registration = await ResolveToolAsync(checkedInput.Invocation, authority, cancellationToken);
        if (registration is null)
            return ToolInvocationOutcome.Rejected(
                "TOOL_INVOCATION_AUTHORITY_CHANGED",
                "The resolved tool handler is outside host-bound authority.");
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
            invalid = ValidateEffectiveInvocation(checkedInput.Invocation, authority);
            if (invalid is not null)
                return invalid;
        }

        var coordinated = await RunPhaseAsync(
            SharpClawActions.Tools.Coordinate,
            checkedInput.Invocation,
            async (effectiveInvocation, ct) =>
            {
                EnsureEffectiveInvocation(effectiveInvocation, authority, "coordinate");
                var plan = new ToolExecutionPlan(effectiveInvocation, checkedInput.Holds);
                ToolExecutionDelegate terminal = async (handlerInvocation, handlerCt) =>
                {
                    EnsureEffectiveInvocation(handlerInvocation, authority, "handler-plan");
                    var handlerResult = await RunPhaseAsync(
                        SharpClawActions.Tools.InvokeHandler,
                        handlerInvocation,
                        async (effectiveHandlerInvocation, handlerCancellationToken) =>
                        {
                            EnsureEffectiveInvocation(effectiveHandlerInvocation, authority, "handler");
                            var handlerResolution = await ResolveToolAsync(
                                effectiveHandlerInvocation,
                                authority,
                                handlerCancellationToken);
                            if (handlerResolution is null)
                                throw new KernelActionExecutionException(
                                    "The resolved tool handler is outside host-bound authority.");
                            if (handlerResolution.SelectedToolName is null)
                                return (object)ToolResult.Error(
                                    $"No handler is registered for tool '{handlerResolution.Invocation.ToolName}'.");
                            var handlerRegistration = _graph.Tools.Single(tool =>
                                string.Equals(
                                    tool.Descriptor.Name,
                                    handlerResolution.SelectedToolName,
                                    StringComparison.Ordinal));
                            var typedHandler = handlerRegistration.Handler
                                ?? KernelServiceResolution.Resolve(
                                    handlerRegistration.HandlerType,
                                    KernelExecutionScope.Current(_serviceProvider)) as IToolHandler;
                            if (typedHandler is null)
                                throw new KernelActionExecutionException(
                                    $"Tool handler '{handlerRegistration.HandlerType.FullName}' does not implement IToolHandler.");
                            EnsureEffectiveInvocation(effectiveHandlerInvocation, authority, "handler-before-call");
                            return (object)await typedHandler.InvokeAsync(
                                effectiveHandlerInvocation,
                                handlerCancellationToken);
                        },
                        handlerCt);
                    KernelToolResultStage? transformedStage = null;
                    var transformed = await RunPhaseAsync(
                        ResultTransform,
                        new KernelToolResultStage(
                            handlerInvocation,
                            handlerResult as ToolResult
                            ?? ToolResult.Error("The tool handler returned no result.")),
                        (stage, _) =>
                        {
                            transformedStage = stage;
                            return ValueTask.FromResult<object>(stage.Result);
                        },
                        handlerCt);
                    if (transformedStage is not null)
                        EnsureEffectiveInvocation(transformedStage.Invocation, authority, "result-transform");

                    KernelToolResultStage? returnedStage = null;
                    var returned = await RunPhaseAsync(
                        ResultReturn,
                        new KernelToolResultStage(
                            handlerInvocation,
                            transformed as ToolResult
                            ?? ToolResult.Error("The tool result transform returned no result.")),
                        (stage, _) =>
                        {
                            returnedStage = stage;
                            return ValueTask.FromResult<object>(stage.Result);
                        },
                        handlerCt);
                    if (returnedStage is not null)
                        EnsureEffectiveInvocation(returnedStage.Invocation, authority, "result-return");
                    return returned as ToolResult
                        ?? ToolResult.Error("The tool handler returned no result.");
                };
                var coordinator = _coordinator
                    ?? KernelExecutionScope.Current(_serviceProvider)
                        .GetService<IToolExecutionCoordinator>()
                    ?? new ImmediateToolExecutionCoordinator();
                return (object)await coordinator.CoordinateAsync(plan, terminal, ct);
            },
            cancellationToken);
        return coordinated is ToolInvocationOutcome result
            ? result
            : ToolInvocationOutcome.Rejected("TOOL_COORDINATION_FAILED", "The tool coordinator returned no result.");
    }

    private async ValueTask<KernelToolResolution?> ResolveToolAsync(
        ToolInvocation invocation,
        ToolAuthorityTuple authority,
        CancellationToken cancellationToken)
    {
        var result = await RunPhaseAsync(
            SharpClawActions.Tools.Resolve,
            invocation,
            (effectiveInvocation, _) =>
            {
                EnsureEffectiveInvocation(effectiveInvocation, authority, "resolve");
                return new ValueTask<object>(new KernelToolResolution(
                    effectiveInvocation,
                    _graph.Tools.FirstOrDefault(tool =>
                        string.Equals(tool.Descriptor.Name, effectiveInvocation.ToolName, StringComparison.Ordinal))
                        ?.Descriptor.Name));
            },
            cancellationToken);
        var resolution = result as KernelToolResolution
            ?? throw new KernelActionExecutionException("The tool resolve action returned an invalid result.");
        if (!authority.MatchesIdentity(resolution.Invocation))
            throw new KernelActionExecutionException(
                "The effective Tool invocation changed host-bound authority.");
        if (!authority.IsWellFormedWithOriginalPayload(resolution.Invocation))
            throw new KernelActionExecutionException(
                "The effective Tool invocation does not contain valid host-issued authority.");
        if (resolution.SelectedToolName is not null &&
            !string.Equals(
                resolution.SelectedToolName,
                authority.ToolName,
                StringComparison.Ordinal))
            return null;
        return resolution;
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
            async (context, ct) => await terminal(ExtractInput<TInput>(context.Action), ct),
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

    private static ToolInvocationOutcome? ValidateEffectiveInvocation(
        ToolInvocation invocation,
        ToolAuthorityTuple authority)
    {
        if (!authority.MatchesIdentity(invocation))
            return ToolInvocationOutcome.Rejected(
                "TOOL_INVOCATION_AUTHORITY_CHANGED",
                "The effective Tool invocation changed host-bound authority.");
        return authority.IsWellFormedWithOriginalPayload(invocation)
            ? null
            : ToolInvocationOutcome.Rejected(
                "TOOL_INVOCATION_INVALID",
                "The effective Tool invocation does not contain valid host-issued authority.");
    }

    private static bool IsEffectiveInvocation(
        ToolInvocation invocation,
        ToolAuthorityTuple authority) =>
        authority.MatchesIdentity(invocation) &&
        authority.IsWellFormedWithOriginalPayload(invocation);

    private static void EnsureEffectiveInvocation(
        ToolInvocation invocation,
        ToolAuthorityTuple authority,
        string boundary)
    {
        if (!IsEffectiveInvocation(invocation, authority))
            throw new KernelActionExecutionException(
                $"The effective Tool invocation changed host-bound authority at {boundary}.");
    }

    private sealed record ToolAuthorityTuple(
        Guid InvocationId,
        Guid? ConversationId,
        string ToolCallId,
        string ToolName,
        HostActionEntryRequestContext HostActionContext,
        JsonElement OriginalArguments)
    {
        public static ToolAuthorityTuple Capture(ToolInvocation invocation) => new(
            invocation.InvocationId,
            invocation.ConversationId,
            invocation.ToolCallId,
            invocation.ToolName,
            invocation.HostActionContext!,
            invocation.Arguments);

        public bool MatchesIdentity(ToolInvocation invocation) =>
            InvocationId == invocation.InvocationId &&
            ConversationId == invocation.ConversationId &&
            string.Equals(ToolCallId, invocation.ToolCallId, StringComparison.Ordinal) &&
            string.Equals(ToolName, invocation.ToolName, StringComparison.Ordinal) &&
            SameHostContext(HostActionContext, invocation.HostActionContext);

        public bool IsWellFormedWithOriginalPayload(ToolInvocation invocation)
        {
            try
            {
                var authorityBoundInvocation = invocation with { Arguments = OriginalArguments };
                return authorityBoundInvocation.IsWellFormed(DateTimeOffset.UtcNow);
            }
            catch
            {
                return false;
            }
        }

        private static bool SameHostContext(
            HostActionEntryRequestContext left,
            HostActionEntryRequestContext? right) =>
            right is not null &&
            left.CapabilityId == right.CapabilityId &&
            string.Equals(left.CapabilityHandle, right.CapabilityHandle, StringComparison.Ordinal) &&
            left.Ingress == right.Ingress &&
            left.InvocationId == right.InvocationId &&
            left.RequestId == right.RequestId &&
            left.CancellationId == right.CancellationId &&
            SamePrincipal(left.Caller, right.Caller) &&
            SameFeatures(left.Features, right.Features) &&
            left.TraceId == right.TraceId &&
            left.IdempotencyKey == right.IdempotencyKey &&
            left.Deadline == right.Deadline &&
            left.ExpiresAt == right.ExpiresAt &&
            SameContribution(left.Contribution, right.Contribution) &&
            left.ParentInvocationId == right.ParentInvocationId &&
            left.Depth == right.Depth &&
            left.Attempt == right.Attempt;

        private static bool SamePrincipal(RequestPrincipal left, RequestPrincipal right) =>
            string.Equals(left.SubjectId, right.SubjectId, StringComparison.Ordinal) &&
            string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) &&
            left.IsAuthenticated == right.IsAuthenticated &&
            SameRoles(left.Roles, right.Roles);

        private static bool SameRoles(
            IReadOnlySet<string>? left,
            IReadOnlySet<string>? right) =>
            left is null
                ? right is null
                : right is not null &&
                  left.Count == right.Count &&
                  left.All(role => right.Contains(role));

        private static bool SameFeatures(ExtensionFeatureSet left, ExtensionFeatureSet right) =>
            left.Items.Count == right.Items.Count &&
            left.Items.Zip(right.Items).All(pair =>
                string.Equals(pair.First.ContractName, pair.Second.ContractName, StringComparison.Ordinal) &&
                pair.First.SchemaVersion == pair.Second.SchemaVersion &&
                string.Equals(pair.First.OwnerId, pair.Second.OwnerId, StringComparison.Ordinal) &&
                pair.First.MaxBytes == pair.Second.MaxBytes &&
                pair.First.Value.GetRawText() == pair.Second.Value.GetRawText());

        private static bool SameContribution(
            HostActionEntryContribution? left,
            HostActionEntryContribution? right) =>
            left is not null &&
            right is not null &&
            left.IngressBinding.Ingress == right.IngressBinding.Ingress &&
            string.Equals(left.IngressBinding.PrimaryIdentity, right.IngressBinding.PrimaryIdentity, StringComparison.Ordinal) &&
            string.Equals(left.IngressBinding.SecondaryIdentity, right.IngressBinding.SecondaryIdentity, StringComparison.Ordinal) &&
            left.Lineage.ActionKey == right.Lineage.ActionKey &&
            left.Lineage.ActionVersion == right.Lineage.ActionVersion &&
            string.Equals(left.Lineage.DescriptorHash, right.Lineage.DescriptorHash, StringComparison.Ordinal) &&
            string.Equals(left.Lineage.InputTypeIdentity, right.Lineage.InputTypeIdentity, StringComparison.Ordinal) &&
            left.Lineage.InputSchemaVersion == right.Lineage.InputSchemaVersion &&
            string.Equals(left.Lineage.InputSchemaHash, right.Lineage.InputSchemaHash, StringComparison.Ordinal) &&
            string.Equals(left.Lineage.PayloadContentHash, right.Lineage.PayloadContentHash, StringComparison.Ordinal) &&
            left.Lineage.PayloadByteLength == right.Lineage.PayloadByteLength;
    }

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
