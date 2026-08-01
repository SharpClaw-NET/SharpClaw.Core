using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

public sealed class KernelActionDispatcher : IActionDispatcher
{
    private readonly KernelGraph _graph;
    private readonly IActionContinuationHost _continuationHost;

    public KernelActionDispatcher(KernelGraph graph, IActionContinuationHost? continuationHost = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _continuationHost = continuationHost ?? new InMemoryContinuationHost();
    }

    public ValueTask<IActionOutcome<TResult>> RunAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<TAction, CancellationToken, ValueTask<TResult>> terminal,
        ActionPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(snapshot);
        var definition = _graph.GetAction<TAction, TResult>(descriptor.Key);
        if (definition.Descriptor.Version != descriptor.Version)
            throw new KernelActionExecutionException(
                $"Action '{descriptor.Key.Value}' was invoked with version {descriptor.Version}, " +
                $"but the graph contains version {definition.Descriptor.Version}.");

        var invocation = new KernelActionInvocation<TAction, TResult>(
            definition,
            terminal,
            snapshot,
            _continuationHost,
            cancellationToken);
        return invocation.InvokeAsync(action, 0, 0, cancellationToken);
    }

    public async ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<TAction, CancellationToken, ValueTask<TResult>> terminal,
        ActionPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var outcome = await RunAsync(descriptor, action, terminal, snapshot, cancellationToken);
        if (outcome.Kind == ActionOutcomeKind.Completed)
            return outcome.Result!;

        throw new KernelActionExecutionException(
            $"Action '{descriptor.Key.Value}' did not complete. " +
            $"Kind={outcome.Kind}, Error={outcome.Error?.Code ?? "none"}. " +
            $"{outcome.Error?.Message ?? "No terminal result was returned."}");
    }

    private sealed class KernelActionInvocation<TAction, TResult>
    {
        private readonly CompiledActionDefinition<TAction, TResult> _definition;
        private readonly Func<TAction, CancellationToken, ValueTask<TResult>> _terminal;
        private readonly ActionPipelineSnapshot _snapshot;
        private readonly IActionContinuationHost _continuationHost;
        private readonly CancellationToken _rootCancellationToken;
        private readonly Guid _traceId = Guid.NewGuid();
        private readonly Guid _idempotencyKey = Guid.NewGuid();

        public KernelActionInvocation(
            CompiledActionDefinition<TAction, TResult> definition,
            Func<TAction, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            IActionContinuationHost continuationHost,
            CancellationToken rootCancellationToken)
        {
            _definition = definition;
            _terminal = terminal;
            _snapshot = snapshot;
            _continuationHost = continuationHost;
            _rootCancellationToken = rootCancellationToken;
        }

        public ValueTask<IActionOutcome<TResult>> InvokeAsync(
            TAction action,
            int index,
            int depth,
            CancellationToken cancellationToken,
            int attempt = 1,
            Guid? parentInvocationId = null)
        {
            if (depth > _snapshot.MaximumActionDepth)
            {
                return ValueTask.FromResult<IActionOutcome<TResult>>(KernelActionOutcome<TResult>.Failed(
                    "ACTION_DEPTH_EXCEEDED",
                    "The action graph exceeded its maximum recursion depth."));
            }

            var invocationId = Guid.NewGuid();
            var deadline = DateTimeOffset.UtcNow + _definition.Descriptor.DefaultTimeout;
            var context = new ActionContext<TAction>(
                invocationId,
                parentInvocationId,
                _traceId,
                _idempotencyKey,
                attempt,
                depth,
                deadline,
                _definition.Descriptor.Key,
                _definition.OwnerModuleId,
                RequestPrincipal.Anonymous,
                action,
                ExtensionFeatureSet.Empty,
                _snapshot);

            if (index >= _definition.Frames.Count)
                return InvokeTerminalAsync(action, invocationId, cancellationToken);

            var frame = _definition.Frames[index];
            if (frame is TypedActionFrame<TAction, TResult> typed)
            {
                var control = new TypedActionControl(
                    this,
                    context,
                    action,
                    index,
                    depth,
                    attempt,
                    invocationId);
                return InvokeTypedAsync(typed.Interceptor, context, control, cancellationToken);
            }

            if (frame is AnyActionFrame<TAction, TResult> any)
            {
                var descriptor = new UntypedActionDescriptor(
                    _definition.Descriptor.Key,
                    _definition.Descriptor.Version,
                    _definition.Descriptor.Category,
                    _definition.Descriptor.Capabilities,
                    new JsonSchemaReference("core.action.input", 1, string.Empty),
                    new JsonSchemaReference("core.action.result", 1, string.Empty),
                    _definition.Descriptor.ContainsSensitiveData);
                var untypedContext = new UntypedActionContext(
                    context.InvocationId,
                    context.ParentInvocationId,
                    context.TraceId,
                    context.IdempotencyKey,
                    context.Attempt,
                    context.Depth,
                    context.Deadline,
                    context.OwnerModuleId,
                    context.Caller,
                    context.Features,
                    context.Snapshot.ContractHash,
                    descriptor,
                    KernelJson.Serialize(action));
                var control = new UntypedActionControl(
                    this,
                    context,
                    action,
                    index,
                    depth,
                    attempt,
                    invocationId);
                return InvokeUntypedAsync(any.Interceptor, untypedContext, control, cancellationToken);
            }

            throw new KernelActionExecutionException("The compiled action graph contains an unknown frame.");
        }

        private async ValueTask<IActionOutcome<TResult>> InvokeTypedAsync(
            IActionInterceptor<TAction, TResult> interceptor,
            ActionContext<TAction> context,
            TypedActionControl control,
            CancellationToken cancellationToken)
        {
            try
            {
                var outcome = await interceptor.InvokeAsync(context, control, cancellationToken);
                return outcome ?? KernelActionOutcome<TResult>.Failed(
                    "ACTION_NULL_OUTCOME",
                    "An action interceptor returned no outcome.");
            }
            catch (ActionOutcomeUncertainException exception)
            {
                return await RecordUncertaintyAsync(context, exception.Uncertainty, cancellationToken);
            }
            catch (KernelCapabilityException exception)
            {
                return KernelActionOutcome<TResult>.Failed("ACTION_CAPABILITY_DENIED", exception.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return KernelActionOutcome<TResult>.Cancelled("ACTION_CANCELLED", "The action was cancelled.");
            }
            catch (Exception exception)
            {
                return KernelActionOutcome<TResult>.Failed("ACTION_INTERCEPTOR_FAILED", exception.Message);
            }
        }

        private async ValueTask<IActionOutcome<TResult>> InvokeUntypedAsync(
            IAnyActionInterceptor interceptor,
            UntypedActionContext context,
            UntypedActionControl control,
            CancellationToken cancellationToken)
        {
            try
            {
                var outcome = await interceptor.InvokeAsync(context, control, cancellationToken);
                return outcome is null
                    ? KernelActionOutcome<TResult>.Failed("ACTION_NULL_OUTCOME", "An action interceptor returned no outcome.")
                    : ConvertOutcome(outcome);
            }
            catch (ActionOutcomeUncertainException exception)
            {
                var typedContext = new ActionContext<TAction>(
                    context.InvocationId,
                    context.ParentInvocationId,
                    context.TraceId,
                    context.IdempotencyKey,
                    context.Attempt,
                    context.Depth,
                    context.Deadline,
                    context.Descriptor.Key,
                    context.OwnerModuleId,
                    context.Caller,
                    KernelJson.Deserialize<TAction>(context.Input),
                    context.Features,
                    _snapshot);
                return await RecordUncertaintyAsync(typedContext, exception.Uncertainty, cancellationToken);
            }
            catch (KernelCapabilityException exception)
            {
                return KernelActionOutcome<TResult>.Failed("ACTION_CAPABILITY_DENIED", exception.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return KernelActionOutcome<TResult>.Cancelled("ACTION_CANCELLED", "The action was cancelled.");
            }
            catch (Exception exception)
            {
                return KernelActionOutcome<TResult>.Failed("ACTION_INTERCEPTOR_FAILED", exception.Message);
            }
        }

        private async ValueTask<IActionOutcome<TResult>> InvokeTerminalAsync(
            TAction action,
            Guid invocationId,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await _terminal(action, cancellationToken);
                return KernelActionOutcome<TResult>.Completed(result);
            }
            catch (ActionOutcomeUncertainException exception)
            {
                var uncertainty = await _continuationHost.RecordUncertaintyAsync(
                    new KernelUncertaintyRequest(
                        invocationId,
                        _definition.Descriptor.Key,
                        _definition.Descriptor.Version,
                        _idempotencyKey,
                        exception.Uncertainty.Stage,
                        exception.Uncertainty.Code,
                        exception.Uncertainty.Message,
                        exception.Uncertainty.ReceiptReference,
                        _snapshot.ContractHash),
                    cancellationToken);
                return KernelActionOutcome<TResult>.Uncertain(uncertainty);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return KernelActionOutcome<TResult>.Cancelled("ACTION_CANCELLED", "The action was cancelled.");
            }
            catch (Exception exception)
            {
                return KernelActionOutcome<TResult>.Failed("ACTION_TERMINAL_FAILED", exception.Message);
            }
        }

        private async ValueTask<IActionOutcome<TResult>> RecordUncertaintyAsync(
            ActionContext<TAction> context,
            ActionUncertainty supplied,
            CancellationToken cancellationToken)
        {
            var uncertainty = await _continuationHost.RecordUncertaintyAsync(
                new KernelUncertaintyRequest(
                    context.InvocationId,
                    context.ActionKey,
                    _definition.Descriptor.Version,
                    context.IdempotencyKey,
                    supplied.Stage,
                    supplied.Code,
                    supplied.Message,
                    supplied.ReceiptReference,
                    context.Snapshot.ContractHash),
                cancellationToken);
            return KernelActionOutcome<TResult>.Uncertain(uncertainty);
        }

        private IActionOutcome<TResult> ConvertOutcome(IUntypedActionOutcome outcome)
        {
            var result = outcome.Result is { } value
                ? KernelJson.Deserialize<TResult>(value)
                : default!;
            return new KernelActionOutcome<TResult>(
                outcome.Kind,
                result,
                outcome.Error,
                outcome.Continuation,
                outcome.Uncertainty);
        }

        private sealed class TypedActionControl(
            KernelActionInvocation<TAction, TResult> owner,
            ActionContext<TAction> context,
            TAction action,
            int index,
            int depth,
            int attempt,
            Guid invocationId) : IActionControl<TAction, TResult>
        {
            private bool _used;

            public ValueTask<IActionOutcome<TResult>> ProceedAsync(CancellationToken cancellationToken) =>
                ProceedWith(action, cancellationToken);

            public ValueTask<IActionOutcome<TResult>> ProceedWithInputAsync(
                ActionReplacement<TAction> replacement,
                CancellationToken cancellationToken) =>
                ProceedWith(replacement.Value, cancellationToken);

            public IActionOutcome<TResult> ReplaceResult(TResult result, string reason)
            {
                EnsureCapability(ActionInterceptionCapabilities.ReplaceResult);
                _used = true;
                return KernelActionOutcome<TResult>.Completed(result);
            }

            public IActionOutcome<TResult> Cancel(string code, string message)
            {
                EnsureCapability(ActionInterceptionCapabilities.Cancel);
                _used = true;
                return KernelActionOutcome<TResult>.Cancelled(code, message);
            }

            public IActionOutcome<TResult> Fail(ExecutionError error)
            {
                _used = true;
                return KernelActionOutcome<TResult>.Failed(error);
            }

            public async ValueTask<IActionOutcome<TResult>> DeferAsync(
                ActionDeferRequest request,
                CancellationToken cancellationToken)
            {
                EnsureCapability(ActionInterceptionCapabilities.Defer);
                if (!(owner._definition.Descriptor.ContinuationPolicy ?? KernelCapabilities.DurableContinuation).Durable)
                    return KernelActionOutcome<TResult>.Failed(
                        "ACTION_CONTINUATION_DENIED",
                        "The action continuation policy is not durable.");
                if (!context.Snapshot.ActionGrants.Any(grant =>
                        grant.ActionKey == context.ActionKey &&
                        grant.Capabilities.HasFlag(ActionInterceptionCapabilities.Defer)))
                    return KernelActionOutcome<TResult>.Failed(
                        "ACTION_CAPABILITY_DENIED",
                        "The compiled snapshot does not grant defer capability.");
                _used = true;
                var token = await owner._continuationHost.CreateAsync(
                    new KernelContinuationRequest(
                        context.InvocationId,
                        context.ActionKey,
                        owner._definition.Descriptor.Version,
                        context.IdempotencyKey,
                        request,
                        owner._definition.Descriptor.ContinuationPolicy ?? KernelCapabilities.DurableContinuation,
                        context.Snapshot.ContractHash),
                    cancellationToken);
                return KernelActionOutcome<TResult>.Deferred(token);
            }

            public async ValueTask<IActionOutcome<TResult>> RepeatAsync(
                ActionRepeatRequest<TAction> request,
                CancellationToken cancellationToken)
            {
                EnsureCapability(ActionInterceptionCapabilities.Repeat);
                var policy = owner._definition.Descriptor.RepeatPolicy;
                if (policy.Kind == ActionRepeatKind.None || context.Attempt >= policy.MaximumAttempts)
                    return KernelActionOutcome<TResult>.Failed(
                        "ACTION_REPEAT_DENIED",
                        "The action repeat policy does not permit another attempt.");
                _used = true;
                if (request.Backoff is { } backoff && backoff > TimeSpan.Zero)
                    await Task.Delay(backoff, cancellationToken);
                return await owner.InvokeAsync(
                    request.Value,
                    0,
                    depth,
                    cancellationToken,
                    context.Attempt + 1,
                    context.InvocationId);
            }

            private ValueTask<IActionOutcome<TResult>> ProceedWith(
                TAction nextAction,
                CancellationToken cancellationToken)
            {
                EnsureCapability(ActionInterceptionCapabilities.Inspect);
                _used = true;
                return owner.InvokeAsync(
                    nextAction,
                    index + 1,
                    depth,
                    cancellationToken,
                    attempt,
                    invocationId);
            }

            private void EnsureCapability(ActionInterceptionCapabilities capability)
            {
                if (!owner._definition.Descriptor.Capabilities.HasFlag(capability))
                {
                    throw new KernelCapabilityException(
                        $"Action '{context.ActionKey.Value}' does not declare capability '{capability}'.");
                }
                if (_used)
                    throw new KernelActionExecutionException(
                        $"Action control for '{context.ActionKey.Value}' was already consumed.");
            }
        }

        private sealed class UntypedActionControl(
            KernelActionInvocation<TAction, TResult> owner,
            ActionContext<TAction> context,
            TAction action,
            int index,
            int depth,
            int attempt,
            Guid invocationId) : IUntypedActionControl
        {
            private readonly TypedActionControl _typed = new(
                owner,
                context,
                action,
                index,
                depth,
                attempt,
                invocationId);

            public ValueTask<IUntypedActionOutcome> ProceedAsync(CancellationToken cancellationToken) =>
                ConvertAsync(_typed.ProceedAsync(cancellationToken));

            public ValueTask<IUntypedActionOutcome> ProceedWithInputAsync(
                JsonElement input,
                string reason,
                CancellationToken cancellationToken) =>
                ConvertAsync(_typed.ProceedWithInputAsync(
                    new ActionReplacement<TAction>(KernelJson.Deserialize<TAction>(input), reason),
                    cancellationToken));

            public IUntypedActionOutcome ReplaceResult(JsonElement result, string reason) =>
                Convert(_typed.ReplaceResult(KernelJson.Deserialize<TResult>(result), reason));

            public IUntypedActionOutcome Cancel(string code, string message) =>
                Convert(_typed.Cancel(code, message));

            public IUntypedActionOutcome Fail(ExecutionError error) => Convert(_typed.Fail(error));

            public ValueTask<IUntypedActionOutcome> DeferAsync(
                ActionDeferRequest request,
                CancellationToken cancellationToken) =>
                ConvertAsync(_typed.DeferAsync(request, cancellationToken));

            public ValueTask<IUntypedActionOutcome> RepeatAsync(
                JsonElement input,
                string reason,
                TimeSpan? backoff,
                CancellationToken cancellationToken) =>
                ConvertAsync(_typed.RepeatAsync(
                    new ActionRepeatRequest<TAction>(KernelJson.Deserialize<TAction>(input), reason, backoff),
                    cancellationToken));

            private static async ValueTask<IUntypedActionOutcome> ConvertAsync(
                ValueTask<IActionOutcome<TResult>> outcomeTask) =>
                Convert(await outcomeTask);

            private static IUntypedActionOutcome Convert(IActionOutcome<TResult> outcome) =>
                new KernelUntypedActionOutcome(
                    outcome.Kind,
                    outcome.Result is null ? null : KernelJson.Serialize(outcome.Result),
                    outcome.Error,
                    outcome.Continuation,
                    outcome.Uncertainty);
        }
    }
}

public sealed class KernelActionOutcome<TResult> : IActionOutcome<TResult>
{
    public KernelActionOutcome(
        ActionOutcomeKind kind,
        TResult result,
        ExecutionError? error,
        ContinuationToken? continuation,
        ActionUncertainty? uncertainty)
    {
        Kind = kind;
        Result = result;
        Error = error;
        Continuation = continuation;
        Uncertainty = uncertainty;
    }

    public ActionOutcomeKind Kind { get; }

    public TResult Result { get; }

    public ContinuationToken? Continuation { get; }

    public ExecutionError? Error { get; }

    public ActionUncertainty? Uncertainty { get; }

    public static KernelActionOutcome<TResult> Completed(TResult result) =>
        new(ActionOutcomeKind.Completed, result, null, null, null);

    public static KernelActionOutcome<TResult> Cancelled(string code, string message) =>
        Failed(ActionOutcomeKind.Cancelled, code, message);

    public static KernelActionOutcome<TResult> Deferred(ContinuationToken token) =>
        new(ActionOutcomeKind.Deferred, default!, null, token, null);

    public static KernelActionOutcome<TResult> Uncertain(ActionUncertainty uncertainty) =>
        new(ActionOutcomeKind.Uncertain, default!, null, null, uncertainty);

    public static KernelActionOutcome<TResult> Failed(string code, string message) =>
        Failed(ActionOutcomeKind.Failed, code, message);

    public static KernelActionOutcome<TResult> Failed(ExecutionError error) =>
        new(ActionOutcomeKind.Failed, default!, error, null, null);

    private static KernelActionOutcome<TResult> Failed(
        ActionOutcomeKind kind,
        string code,
        string message) =>
        new(kind, default!, new ExecutionError(code, message, false, new Dictionary<string, string>()), null, null);
}

public sealed class KernelUntypedActionOutcome(
    ActionOutcomeKind kind,
    JsonElement? result,
    ExecutionError? error,
    ContinuationToken? continuation,
    ActionUncertainty? uncertainty) : IUntypedActionOutcome
{
    public ActionOutcomeKind Kind { get; } = kind;

    public JsonElement? Result { get; } = result;

    public ContinuationToken? Continuation { get; } = continuation;

    public ExecutionError? Error { get; } = error;

    public ActionUncertainty? Uncertainty { get; } = uncertainty;
}
