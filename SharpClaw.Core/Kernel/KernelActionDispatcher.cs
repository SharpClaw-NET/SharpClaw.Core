using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

public sealed class KernelActionDispatcher : IActionDispatcher
{
    private static readonly AsyncLocal<InvocationScope?> CurrentScope = new();
    private sealed record InvocationScope(Guid InvocationId, int Depth);
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
        if (!string.Equals(
                snapshot.ContractHash,
                _graph.ActionSnapshot.ContractHash,
                StringComparison.Ordinal))
            throw new KernelActionExecutionException(
                "The action pipeline snapshot is not compatible with the compiled kernel graph.");
        var definition = _graph.GetAction<TAction, TResult>(descriptor.Key);
        if (definition.Descriptor.Version != descriptor.Version)
            throw new KernelActionExecutionException(
                $"Action '{descriptor.Key.Value}' was invoked with version {descriptor.Version}, " +
                $"but the graph contains version {definition.Descriptor.Version}.");
        if (!KernelGraphHasher.Flatten("descriptor", definition.Descriptor)
                .SequenceEqual(KernelGraphHasher.Flatten("descriptor", descriptor)))
            throw new KernelActionExecutionException(
                $"Action '{descriptor.Key.Value}' does not match the compiled descriptor schema.");

        var parent = CurrentScope.Value;
        var depth = parent is null ? 0 : parent.Depth + 1;
        var invocation = new KernelActionInvocation<TAction, TResult>(
            definition,
            terminal,
            snapshot,
            _continuationHost,
            cancellationToken);
        return invocation.InvokeRootAsync(action, depth, parent?.InvocationId, cancellationToken);
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

        if (outcome.Kind == ActionOutcomeKind.Uncertain && outcome.Uncertainty is not null)
            throw new ActionOutcomeUncertainException(outcome.Uncertainty);

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
        private readonly Guid _invocationId = Guid.NewGuid();
        private readonly Guid _traceId = Guid.NewGuid();
        private readonly Guid _idempotencyKey = Guid.NewGuid();
        private readonly DateTimeOffset _deadline;
        private Guid? _parentInvocationId;

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
            _deadline = DateTimeOffset.UtcNow + definition.Descriptor.DefaultTimeout;
        }

        public async ValueTask<IActionOutcome<TResult>> InvokeRootAsync(
            TAction action,
            int depth,
            Guid? parentInvocationId,
            CancellationToken cancellationToken)
        {
            _parentInvocationId = parentInvocationId;
            var previous = CurrentScope.Value;
            CurrentScope.Value = new InvocationScope(_invocationId, depth);
            try
            {
                return await InvokeFrameAsync(
                    action,
                    0,
                    depth,
                    cancellationToken,
                    1);
            }
            finally
            {
                CurrentScope.Value = previous;
            }
        }

        private async ValueTask<IActionOutcome<TResult>> InvokeFrameAsync(
            TAction action,
            int index,
            int depth,
            CancellationToken cancellationToken,
            int attempt = 1)
        {
            if (depth > _snapshot.MaximumActionDepth)
                return KernelActionOutcome<TResult>.Failed(
                    "ACTION_DEPTH_EXCEEDED",
                    "The action graph exceeded its maximum recursion depth.");
            if (DateTimeOffset.UtcNow >= _deadline)
                return KernelActionOutcome<TResult>.Failed(
                    "ACTION_DEADLINE_EXCEEDED",
                    "The action deadline expired before this action path completed.");

            var activeFrame = index < _definition.Frames.Count ? _definition.Frames[index] : null;
            var context = new ActionContext<TAction>(
                _invocationId,
                _parentInvocationId,
                _traceId,
                _idempotencyKey,
                depth,
                attempt,
                _deadline,
                _definition.Descriptor.Key,
                activeFrame?.OwnerModuleId ?? _definition.OwnerModuleId,
                RequestPrincipal.Anonymous,
                action,
                ExtensionFeatureSet.Empty,
                _snapshot);

            if (index >= _definition.Frames.Count)
                return await InvokeTerminalAsync(action, _invocationId, cancellationToken);

            var frame = activeFrame!;
            if (frame is TypedActionFrame<TAction, TResult> typed)
                return await InvokeTypedAsync(typed, context, cancellationToken);

            if (frame is AnyActionFrame<TAction, TResult> any)
                return await InvokeUntypedAsync(any, context, cancellationToken);

            return KernelActionOutcome<TResult>.Failed(
                "ACTION_FRAME_INVALID",
                "The compiled action graph contains an unknown frame.");
        }

        private async ValueTask<IActionOutcome<TResult>> InvokeTypedAsync(
            TypedActionFrame<TAction, TResult> frame,
            ActionContext<TAction> context,
            CancellationToken cancellationToken)
        {
            var control = new TypedActionControl(this, context, frame, context.Action);
            try
            {
                var outcome = await InvokeHookAsync(
                    token => frame.Interceptor.InvokeAsync(context, control, token),
                    frame.Ordering,
                    cancellationToken);
                return ValidateOutcome(outcome, control.Authority);
            }
            catch (ActionOutcomeUncertainException exception)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return Reissue(known!, control.Authority);
                return await RecordUncertaintyAsync(context, exception.Uncertainty, control.Authority, cancellationToken);
            }
            catch (KernelCapabilityException exception)
            {
                return KernelActionOutcome<TResult>.Failed("ACTION_CAPABILITY_DENIED", exception.Message);
            }
            catch (KernelControlException exception)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return Reissue(known!, control.Authority);
                return KernelActionOutcome<TResult>.Failed("ACTION_CONTROL_CONSUMED", exception.Message);
            }
            catch (KernelOperationTimeoutException exception) when (frame.Ordering.FailurePolicy == HookFailurePolicy.BestEffort)
            {
                return await ResolveHookTimeoutAsync(
                    frame,
                    context,
                    control,
                    exception,
                    cancellationToken);
            }
            catch (TimeoutException exception) when (frame.Ordering.FailurePolicy == HookFailurePolicy.BestEffort)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return Reissue(known!, control.Authority);
                if (control.ContinuationStarted)
                    return await RecordControlUncertaintyAsync(context, control, exception.Message, cancellationToken);
                return await InvokeFrameAsync(context.Action, FrameIndex(frame) + 1,
                    context.Depth, cancellationToken, context.Attempt);
            }
            catch (KernelOperationTimeoutException exception)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return Reissue(known!, control.Authority);
                if (control.ContinuationStarted || exception.OperationStillRunning)
                    return await RecordControlUncertaintyAsync(context, control, exception.Message, cancellationToken);
                return KernelActionOutcome<TResult>.Failed("ACTION_HOOK_TIMEOUT", exception.Message);
            }
            catch (TimeoutException exception)
            {
                return KernelActionOutcome<TResult>.Failed("ACTION_HOOK_TIMEOUT", exception.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return KernelActionOutcome<TResult>.Cancelled("ACTION_CANCELLED", "The action was cancelled.");
            }
            catch (Exception exception)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return Reissue(known!, control.Authority);
                if (control.ContinuationStarted)
                    return await RecordControlUncertaintyAsync(context, control, exception.Message, cancellationToken);
                if (frame.Ordering.FailurePolicy == HookFailurePolicy.BestEffort)
                    return await InvokeFrameAsync(
                        context.Action,
                        FrameIndex(frame) + 1,
                        context.Depth,
                        cancellationToken,
                        context.Attempt);
                return KernelActionOutcome<TResult>.Failed("ACTION_INTERCEPTOR_FAILED", exception.Message);
            }
        }

        private async ValueTask<IActionOutcome<TResult>> InvokeUntypedAsync(
            AnyActionFrame<TAction, TResult> frame,
            ActionContext<TAction> context,
            CancellationToken cancellationToken)
        {
            var descriptor = new UntypedActionDescriptor(
                _definition.Descriptor.Key,
                _definition.Descriptor.Version,
                _definition.Descriptor.Category,
                frame.EffectiveCapabilities,
                new JsonSchemaReference("core.action.input", 1, string.Empty),
                new JsonSchemaReference("core.action.result", 1, string.Empty),
                _definition.Descriptor.ContainsSensitiveData);
            var untypedContext = new UntypedActionContext(
                context.InvocationId,
                context.ParentInvocationId,
                context.TraceId,
                context.IdempotencyKey,
                context.Depth,
                context.Attempt,
                context.Deadline,
                context.OwnerModuleId,
                context.Caller,
                context.Features,
                context.Snapshot.ContractHash,
                descriptor,
                KernelJson.Serialize(context.Action));
            var control = new UntypedActionControl(
                this,
                context,
                frame,
                context.Action);
            try
            {
                var outcome = await InvokeHookAsync(
                    token => frame.Interceptor.InvokeAsync(untypedContext, control, token),
                    frame.Ordering,
                    cancellationToken);
                if (outcome is not KernelUntypedActionOutcome trusted ||
                    !ReferenceEquals(trusted.Authority, control.Authority))
                    return KernelActionOutcome<TResult>.Failed(
                        "ACTION_FORGED_OUTCOME",
                        "The action interceptor returned an outcome that this control did not issue.");
                return ConvertOutcome(trusted, control.Authority);
            }
            catch (ActionOutcomeUncertainException exception)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return Reissue(known!, control.Authority);
                return await RecordUncertaintyAsync(context, exception.Uncertainty, control.Authority, cancellationToken);
            }
            catch (KernelCapabilityException exception)
            {
                return KernelActionOutcome<TResult>.Failed("ACTION_CAPABILITY_DENIED", exception.Message);
            }
            catch (KernelControlException exception)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return Reissue(known!, control.Authority);
                return KernelActionOutcome<TResult>.Failed("ACTION_CONTROL_CONSUMED", exception.Message);
            }
            catch (KernelOperationTimeoutException exception) when (frame.Ordering.FailurePolicy == HookFailurePolicy.BestEffort)
            {
                return await ResolveUntypedHookTimeoutAsync(
                    frame,
                    context,
                    control,
                    exception,
                    cancellationToken);
            }
            catch (TimeoutException exception) when (frame.Ordering.FailurePolicy == HookFailurePolicy.BestEffort)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return Reissue(known!, control.Authority);
                if (control.ContinuationStarted)
                    return await RecordControlUncertaintyAsync(context, control, exception.Message, cancellationToken);
                return await InvokeFrameAsync(context.Action, FrameIndex(frame) + 1,
                    context.Depth, cancellationToken, context.Attempt);
            }
            catch (KernelOperationTimeoutException exception)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return Reissue(known!, control.Authority);
                if (control.ContinuationStarted || exception.OperationStillRunning)
                    return await RecordControlUncertaintyAsync(context, control, exception.Message, cancellationToken);
                return KernelActionOutcome<TResult>.Failed("ACTION_HOOK_TIMEOUT", exception.Message);
            }
            catch (TimeoutException exception)
            {
                return KernelActionOutcome<TResult>.Failed("ACTION_HOOK_TIMEOUT", exception.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return KernelActionOutcome<TResult>.Cancelled("ACTION_CANCELLED", "The action was cancelled.");
            }
            catch (Exception exception)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return Reissue(known!, control.Authority);
                if (control.ContinuationStarted)
                    return await RecordControlUncertaintyAsync(context, control, exception.Message, cancellationToken);
                if (frame.Ordering.FailurePolicy == HookFailurePolicy.BestEffort)
                    return await InvokeFrameAsync(
                        context.Action,
                        FrameIndex(frame) + 1,
                        context.Depth,
                        cancellationToken,
                        context.Attempt);
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
                var result = await InvokeBoundedAsync(
                    token => _terminal(action, token),
                    null,
                    cancellationToken);
                return KernelActionOutcome<TResult>.Completed(result);
            }
            catch (ActionOutcomeUncertainException exception)
            {
                if (RequiresDurableAuthority && !_continuationHost.SupportsDurableState)
                    return KernelActionOutcome<TResult>.Failed(
                        "ACTION_CONTINUATION_DENIED",
                        "An uncertain durable action requires a durable continuation host.");
                var uncertainty = await RecordUncertaintyAsync(
                    invocationId,
                    exception.Uncertainty,
                    cancellationToken,
                    KernelJson.Serialize(action).GetRawText());
                return KernelActionOutcome<TResult>.Uncertain(uncertainty);
            }
            catch (KernelOperationTimeoutException exception) when (exception.OperationStillRunning)
            {
                if (RequiresDurableAuthority && !_continuationHost.SupportsDurableState)
                    return KernelActionOutcome<TResult>.Failed(
                        "ACTION_CONTINUATION_DENIED",
                        "An uncertain durable action requires a durable continuation host.");
                var supplied = new ActionUncertainty(
                    "ACTION_OUTCOME_UNCERTAIN",
                    exception.Message,
                    ActionExecutionStage.ContinuationRunning,
                    null,
                    new ActionRecoveryReference(
                        Guid.NewGuid(),
                        _definition.Descriptor.Key,
                        _definition.Descriptor.Version,
                        _idempotencyKey),
                    DateTimeOffset.UtcNow);
                var uncertainty = await RecordUncertaintyAsync(
                    invocationId,
                    supplied,
                    cancellationToken,
                    KernelJson.Serialize(action).GetRawText());
                return KernelActionOutcome<TResult>.Uncertain(uncertainty);
            }
            catch (TimeoutException exception)
            {
                return KernelActionOutcome<TResult>.Failed("ACTION_DEADLINE_EXCEEDED", exception.Message);
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

        private async ValueTask<IActionOutcome<TResult>> ResolveHookTimeoutAsync(
            TypedActionFrame<TAction, TResult> frame,
            ActionContext<TAction> context,
            IKernelControlState control,
            KernelOperationTimeoutException exception,
            CancellationToken cancellationToken)
        {
            if (control.TryGetKnownOutcome(out var known))
                return Reissue(known!, control.Authority);
            if (control.ContinuationStarted || exception.OperationStillRunning)
                return await RecordControlUncertaintyAsync(
                    context,
                    control,
                    exception.Message,
                    cancellationToken);
            return await InvokeFrameAsync(
                context.Action,
                FrameIndex(frame) + 1,
                context.Depth,
                cancellationToken,
                context.Attempt);
        }

        private async ValueTask<IActionOutcome<TResult>> ResolveUntypedHookTimeoutAsync(
            AnyActionFrame<TAction, TResult> frame,
            ActionContext<TAction> context,
            IKernelControlState control,
            KernelOperationTimeoutException exception,
            CancellationToken cancellationToken)
        {
            if (control.TryGetKnownOutcome(out var known))
                return Reissue(known!, control.Authority);
            if (control.ContinuationStarted || exception.OperationStillRunning)
                return await RecordControlUncertaintyAsync(
                    context,
                    control,
                    exception.Message,
                    cancellationToken);
            return await InvokeFrameAsync(
                context.Action,
                FrameIndex(frame) + 1,
                context.Depth,
                cancellationToken,
                context.Attempt);
        }

        private async ValueTask<IActionOutcome<TResult>> RecordControlUncertaintyAsync(
            ActionContext<TAction> context,
            IKernelControlState control,
            string message,
            CancellationToken cancellationToken)
        {
            control.ConsumeForUncertainty();
            var supplied = new ActionUncertainty(
                "ACTION_OUTCOME_UNCERTAIN",
                message,
                control.ExecutionStage,
                null,
                new ActionRecoveryReference(
                    Guid.NewGuid(),
                    context.ActionKey,
                    _definition.Descriptor.Version,
                    context.IdempotencyKey),
                DateTimeOffset.UtcNow);
            return await RecordUncertaintyAsync(context, supplied, control.Authority, cancellationToken);
        }

        private async ValueTask<IActionOutcome<TResult>> RecordUncertaintyAsync(
            ActionContext<TAction> context,
            ActionUncertainty supplied,
            object authority,
            CancellationToken cancellationToken)
        {
            if (RequiresDurableAuthority && !_continuationHost.SupportsDurableState)
                return KernelActionOutcome<TResult>.FailedBy(
                    new ExecutionError(
                        "ACTION_CONTINUATION_DENIED",
                        "An uncertain durable action requires a durable continuation host."),
                    authority);
            var result = await RecordUncertaintyAsync(
                context.InvocationId,
                supplied,
                cancellationToken,
                KernelJson.Serialize(context.Action).GetRawText());
            return KernelActionOutcome<TResult>.UncertainBy(result, authority);
        }

        private async ValueTask<ActionUncertainty> RecordUncertaintyAsync(
            Guid invocationId,
            ActionUncertainty supplied,
            CancellationToken cancellationToken,
            string? protectedInput = null)
        {
            var recovery = await _continuationHost.RecordUncertaintyAsync(
                new KernelUncertaintyRequest(
                    invocationId,
                    _definition.Descriptor.Key,
                    _definition.Descriptor.Version,
                    _idempotencyKey,
                    supplied.Stage,
                    supplied.Code,
                    supplied.Message,
                    supplied.ReceiptReference,
                    _snapshot.ContractHash,
                    new ContinuationDestination("action-recovery", _definition.Descriptor.Key.Value),
                    protectedInput,
                    _definition.Descriptor.ContinuationPolicy),
                cancellationToken);
            return recovery.Uncertainty;
        }

        private bool RequiresDurableAuthority =>
            _definition.Descriptor.ContinuationPolicy?.Durable == true;

        private async ValueTask<T> InvokeHookAsync<T>(
            Func<CancellationToken, ValueTask<T>> operation,
            HookOrdering ordering,
            CancellationToken cancellationToken) =>
            await InvokeBoundedAsync(operation, ordering.Timeout, cancellationToken);

        private async ValueTask<T> InvokeBoundedAsync<T>(
            Func<CancellationToken, ValueTask<T>> operation,
            TimeSpan? configuredTimeout,
            CancellationToken cancellationToken)
        {
            var remaining = _deadline - DateTimeOffset.UtcNow;
            if (configuredTimeout is { } timeout)
                remaining = remaining < timeout ? remaining : timeout;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("The action deadline expired.");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(remaining);
            var operationTask = Task.Run(
                () => operation(linked.Token).AsTask(),
                CancellationToken.None);
            try
            {
                return await operationTask.WaitAsync(remaining, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new KernelOperationTimeoutException(
                    "The action hook or terminal exceeded its deadline.",
                    await IsStillRunningAsync(operationTask));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new KernelOperationTimeoutException(
                    "The action hook or terminal exceeded its deadline.",
                    await IsStillRunningAsync(operationTask));
            }
        }

        private static ValueTask<bool> IsStillRunningAsync(Task operationTask) =>
            ValueTask.FromResult(!operationTask.IsCompleted);

        private interface IKernelControlState
        {
            object Authority { get; }

            bool ContinuationStarted { get; }

            ActionExecutionStage ExecutionStage { get; }

            bool TryGetKnownOutcome(out IActionOutcome<TResult>? outcome);

            void ConsumeForUncertainty();
        }

        private IActionOutcome<TResult> ValidateOutcome(
            IActionOutcome<TResult>? outcome,
            object authority) => outcome switch
            {
                KernelActionOutcome<TResult> trusted when ReferenceEquals(trusted.Authority, authority) => trusted,
                null => KernelActionOutcome<TResult>.Failed("ACTION_NULL_OUTCOME", "An action interceptor returned no outcome."),
                _ => KernelActionOutcome<TResult>.Failed(
                    "ACTION_FORGED_OUTCOME",
                    "The action interceptor returned an outcome that this control did not issue.")
            };

        private IActionOutcome<TResult> ConvertOutcome(
            KernelUntypedActionOutcome outcome,
            object authority)
        {
            var result = outcome.Result is { } value
                ? KernelJson.Deserialize<TResult>(value)
                : default!;
            return KernelActionOutcome<TResult>.FromAuthority(
                outcome.Kind,
                result,
                outcome.Error,
                outcome.Continuation,
                outcome.Uncertainty,
                authority);
        }

        private sealed class TypedActionControl(
            KernelActionInvocation<TAction, TResult> owner,
            ActionContext<TAction> context,
            IActionFrame<TAction, TResult> frame,
            TAction action) : IActionControl<TAction, TResult>, IKernelControlState
        {
            private readonly object _authority = new();
            private bool _used;
            private bool _continuationStarted;
            private Task<IActionOutcome<TResult>>? _continuationTask;
            private IActionOutcome<TResult>? _continuationOutcome;
            private IActionOutcome<TResult>? _issuedOutcome;
            private ActionExecutionStage _executionStage = ActionExecutionStage.BeforeContinuation;

            public object Authority => _authority;

            public bool ContinuationStarted => _continuationStarted;

            public ActionExecutionStage ExecutionStage =>
                _continuationTask is { IsCompleted: false }
                    ? ActionExecutionStage.ContinuationRunning
                    : _executionStage;

            public bool TryGetKnownOutcome(out IActionOutcome<TResult>? outcome)
            {
                outcome = _issuedOutcome ?? _continuationOutcome;
                return outcome is not null;
            }

            public void ConsumeForUncertainty()
            {
                _used = true;
                _executionStage = _continuationStarted
                    ? ActionExecutionStage.AfterContinuation
                    : ActionExecutionStage.TerminalReturned;
            }

            public async ValueTask<IActionOutcome<TResult>> ProceedAsync(CancellationToken cancellationToken) =>
                await ProceedWith(action, cancellationToken, ActionInterceptionCapabilities.Inspect);

            public async ValueTask<IActionOutcome<TResult>> ProceedWithInputAsync(
                ActionReplacement<TAction> replacement,
                CancellationToken cancellationToken) =>
                await ProceedWith(
                    replacement.Value,
                    cancellationToken,
                    ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.ReplaceInput);

            public IActionOutcome<TResult> ReplaceResult(TResult result, string reason)
            {
                EnsureCapability(ActionInterceptionCapabilities.ReplaceResult);
                _used = true;
                return _issuedOutcome = KernelActionOutcome<TResult>.CompletedBy(result, _authority);
            }

            public IActionOutcome<TResult> Cancel(string code, string message)
            {
                EnsureCapability(ActionInterceptionCapabilities.Cancel);
                _used = true;
                return _issuedOutcome = KernelActionOutcome<TResult>.CancelledBy(code, message, _authority);
            }

            public IActionOutcome<TResult> Fail(ExecutionError error)
            {
                ArgumentNullException.ThrowIfNull(error);
                EnsureAvailable();
                _used = true;
                return _issuedOutcome = KernelActionOutcome<TResult>.FailedBy(error, _authority);
            }

            public async ValueTask<IActionOutcome<TResult>> DeferAsync(
                ActionDeferRequest request,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(request);
                EnsureCapability(ActionInterceptionCapabilities.Defer);
                var policy = owner._definition.Descriptor.ContinuationPolicy ?? KernelCapabilities.DurableContinuation;
                if (!policy.Durable || !owner._continuationHost.SupportsDurableState)
                {
                    _used = true;
                    return _issuedOutcome = KernelActionOutcome<TResult>.FailedBy(
                        new ExecutionError(
                            "ACTION_CONTINUATION_DENIED",
                            "A durable continuation requires durable action policy and durable host state."),
                        _authority);
                }
                _used = true;
                var token = await owner._continuationHost.CreateAsync(
                    new KernelContinuationRequest(
                        context.InvocationId,
                        context.ActionKey,
                        owner._definition.Descriptor.Version,
                        context.IdempotencyKey,
                        request,
                        policy,
                        context.Snapshot.ContractHash,
                        new ContinuationDestination("action", context.ActionKey.Value),
                        KernelJson.Serialize(context.Action).GetRawText()),
                    cancellationToken);
                return _issuedOutcome = KernelActionOutcome<TResult>.DeferredBy(token, _authority);
            }

            public async ValueTask<IActionOutcome<TResult>> RepeatAsync(
                ActionRepeatRequest<TAction> request,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(request);
                EnsureCapability(ActionInterceptionCapabilities.Repeat);
                var policy = owner._definition.Descriptor.RepeatPolicy;
                if (!CanRepeat(policy, request))
                {
                    _used = true;
                    return _issuedOutcome = KernelActionOutcome<TResult>.FailedBy(
                        new ExecutionError(
                            "ACTION_REPEAT_DENIED",
                            "The action repeat policy does not permit another attempt."),
                        _authority);
                }

                _used = true;
                var backoff = request.Backoff.GetValueOrDefault();
                if (policy.MinimumBackoff > backoff)
                    backoff = policy.MinimumBackoff;
                if (backoff > TimeSpan.Zero)
                    await Task.Delay(backoff, cancellationToken);
                var outcome = await owner.InvokeFrameAsync(
                    request.Value,
                    0,
                    context.Depth,
                    cancellationToken,
                    context.Attempt + 1);
                return _issuedOutcome = Reissue(outcome, _authority);
            }

            private async ValueTask<IActionOutcome<TResult>> ProceedWith(
                TAction nextAction,
                CancellationToken cancellationToken,
                ActionInterceptionCapabilities required)
            {
                EnsureCapability(ActionInterceptionCapabilities.Wrap);
                EnsureCapability(required & ~ActionInterceptionCapabilities.Wrap);
                _used = true;
                _continuationStarted = true;
                _executionStage = ActionExecutionStage.ContinuationRunning;
                _continuationTask = owner.InvokeFrameAsync(
                    nextAction,
                    owner.FrameIndex(frame) + 1,
                    context.Depth,
                    cancellationToken,
                    context.Attempt).AsTask();
                IActionOutcome<TResult> outcome;
                try
                {
                    outcome = await _continuationTask;
                }
                catch
                {
                    _executionStage = ActionExecutionStage.AfterContinuation;
                    throw;
                }
                _continuationOutcome = outcome;
                _issuedOutcome = Reissue(outcome, _authority);
                _executionStage = outcome.Kind == ActionOutcomeKind.Uncertain
                    ? ActionExecutionStage.TerminalReturned
                    : ActionExecutionStage.Committed;
                _executionStage = ActionExecutionStage.AfterContinuation;
                return _issuedOutcome;
            }

            private bool CanRepeat(ActionRepeatPolicy policy, ActionRepeatRequest<TAction> request)
            {
                if (policy.Kind == ActionRepeatKind.None || context.Attempt >= policy.MaximumAttempts)
                    return false;
                if (string.IsNullOrWhiteSpace(policy.IdempotencyScope) || context.IdempotencyKey == Guid.Empty)
                    return false;
                return policy.Kind switch
                {
                    ActionRepeatKind.ConflictOnly =>
                        request.Reason.Contains("conflict", StringComparison.OrdinalIgnoreCase),
                    ActionRepeatKind.Receipted => context.Features.Contains("action.receipt"),
                    _ => true
                };
            }

            private void EnsureCapability(ActionInterceptionCapabilities capability)
            {
                EnsureAvailable();
                if (!frame.EffectiveCapabilities.HasFlag(capability))
                {
                    throw new KernelCapabilityException(
                        $"Module '{frame.OwnerModuleId}' does not have effective capability '{capability}' " +
                        $"for action '{context.ActionKey.Value}'.");
                }
            }

            private void EnsureAvailable()
            {
                if (_used)
                    throw new KernelControlException(
                        $"Action control for '{context.ActionKey.Value}' was already consumed.");
            }
        }

        private int FrameIndex(object frame)
        {
            for (var index = 0; index < _definition.Frames.Count; index++)
            {
                if (ReferenceEquals(_definition.Frames[index], frame))
                    return index;
            }

            throw new KernelActionExecutionException("The compiled action frame is not registered.");
        }

        private sealed class UntypedActionControl(
            KernelActionInvocation<TAction, TResult> owner,
            ActionContext<TAction> context,
            AnyActionFrame<TAction, TResult> frame,
            TAction action) : IUntypedActionControl, IKernelControlState
        {
            private readonly TypedActionControl _typed = new(owner, context, frame, action);

            public object Authority => _typed.Authority;

            public bool ContinuationStarted => _typed.ContinuationStarted;

            public ActionExecutionStage ExecutionStage => _typed.ExecutionStage;

            public bool TryGetKnownOutcome(out IActionOutcome<TResult>? outcome) =>
                _typed.TryGetKnownOutcome(out outcome);

            public void ConsumeForUncertainty() => _typed.ConsumeForUncertainty();

            public async ValueTask<IUntypedActionOutcome> ProceedAsync(CancellationToken cancellationToken) =>
                Convert(await _typed.ProceedAsync(cancellationToken));

            public async ValueTask<IUntypedActionOutcome> ProceedWithInputAsync(
                JsonElement input,
                string reason,
                CancellationToken cancellationToken) =>
                Convert(await _typed.ProceedWithInputAsync(
                    new ActionReplacement<TAction>(KernelJson.Deserialize<TAction>(input), reason),
                    cancellationToken));

            public IUntypedActionOutcome ReplaceResult(JsonElement result, string reason) =>
                Convert(_typed.ReplaceResult(KernelJson.Deserialize<TResult>(result), reason));

            public IUntypedActionOutcome Cancel(string code, string message) =>
                Convert(_typed.Cancel(code, message));

            public IUntypedActionOutcome Fail(ExecutionError error) => Convert(_typed.Fail(error));

            public async ValueTask<IUntypedActionOutcome> DeferAsync(
                ActionDeferRequest request,
                CancellationToken cancellationToken) =>
                Convert(await _typed.DeferAsync(request, cancellationToken));

            public async ValueTask<IUntypedActionOutcome> RepeatAsync(
                JsonElement input,
                string reason,
                TimeSpan? backoff,
                CancellationToken cancellationToken) =>
                Convert(await _typed.RepeatAsync(
                    new ActionRepeatRequest<TAction>(KernelJson.Deserialize<TAction>(input), reason, backoff),
                    cancellationToken));

            private static IUntypedActionOutcome Convert(IActionOutcome<TResult> outcome) =>
                new KernelUntypedActionOutcome(
                    outcome.Kind,
                    outcome.Result is null ? null : KernelJson.Serialize(outcome.Result),
                    outcome.Error,
                    outcome.Continuation,
                    outcome.Uncertainty,
                    ((KernelActionOutcome<TResult>)outcome).Authority);
        }
    }

    private static IActionOutcome<TResult> Reissue<TResult>(IActionOutcome<TResult> outcome, object authority) =>
        outcome is KernelActionOutcome<TResult> trusted
            ? KernelActionOutcome<TResult>.FromAuthority(
                trusted.Kind,
                trusted.Result,
                trusted.Error,
                trusted.Continuation,
                trusted.Uncertainty,
                authority)
            : KernelActionOutcome<TResult>.FailedBy(
                new ExecutionError(
                    "ACTION_FORGED_OUTCOME",
                    "The action path returned an outcome that Core did not issue."),
                authority);
}

internal sealed class KernelControlException(string message) : InvalidOperationException(message);

internal sealed class KernelOperationTimeoutException(
    string message,
    bool operationStillRunning) : TimeoutException(message)
{
    public bool OperationStillRunning { get; } = operationStillRunning;
}

public sealed class KernelActionOutcome<TResult> : IActionOutcome<TResult>
{
    public KernelActionOutcome(
        ActionOutcomeKind kind,
        TResult result,
        ExecutionError? error,
        ContinuationToken? continuation,
        ActionUncertainty? uncertainty)
        : this(kind, result, error, continuation, uncertainty, null)
    {
    }

    private KernelActionOutcome(
        ActionOutcomeKind kind,
        TResult result,
        ExecutionError? error,
        ContinuationToken? continuation,
        ActionUncertainty? uncertainty,
        object? authority)
    {
        Kind = kind;
        Result = result;
        Error = error;
        Continuation = continuation;
        Uncertainty = uncertainty;
        Authority = authority;
    }

    internal object? Authority { get; }

    public ActionOutcomeKind Kind { get; }
    public TResult Result { get; }
    public ContinuationToken? Continuation { get; }
    public ExecutionError? Error { get; }
    public ActionUncertainty? Uncertainty { get; }

    public static KernelActionOutcome<TResult> Completed(TResult result) =>
        new(ActionOutcomeKind.Completed, result, null, null, null);

    internal static KernelActionOutcome<TResult> CompletedBy(TResult result, object authority) =>
        new(ActionOutcomeKind.Completed, result, null, null, null, authority);

    public static KernelActionOutcome<TResult> Cancelled(string code, string message) =>
        Failed(ActionOutcomeKind.Cancelled, code, message);

    internal static KernelActionOutcome<TResult> CancelledBy(string code, string message, object authority) =>
        new(ActionOutcomeKind.Cancelled, default!, new ExecutionError(code, message), null, null, authority);

    public static KernelActionOutcome<TResult> Deferred(ContinuationToken token) =>
        new(ActionOutcomeKind.Deferred, default!, null, token, null);

    internal static KernelActionOutcome<TResult> DeferredBy(ContinuationToken token, object authority) =>
        new(ActionOutcomeKind.Deferred, default!, null, token, null, authority);

    public static KernelActionOutcome<TResult> Uncertain(ActionUncertainty uncertainty) =>
        new(ActionOutcomeKind.Uncertain, default!, null, null, uncertainty);

    internal static KernelActionOutcome<TResult> UncertainBy(ActionUncertainty uncertainty, object authority) =>
        new(ActionOutcomeKind.Uncertain, default!, null, null, uncertainty, authority);

    public static KernelActionOutcome<TResult> Failed(string code, string message) =>
        Failed(ActionOutcomeKind.Failed, code, message);

    public static KernelActionOutcome<TResult> Failed(ExecutionError error) =>
        new(ActionOutcomeKind.Failed, default!, error, null, null);

    internal static KernelActionOutcome<TResult> FailedBy(ExecutionError error, object authority) =>
        new(ActionOutcomeKind.Failed, default!, error, null, null, authority);

    internal static KernelActionOutcome<TResult> FromAuthority(
        ActionOutcomeKind kind,
        TResult result,
        ExecutionError? error,
        ContinuationToken? continuation,
        ActionUncertainty? uncertainty,
        object authority) =>
        new(kind, result, error, continuation, uncertainty, authority);

    private static KernelActionOutcome<TResult> Failed(
        ActionOutcomeKind kind,
        string code,
        string message) =>
        new(kind, default!, new ExecutionError(code, message, false, new Dictionary<string, string>()), null, null);
}

public sealed class KernelUntypedActionOutcome : IUntypedActionOutcome
{
    public KernelUntypedActionOutcome(
        ActionOutcomeKind kind,
        JsonElement? result,
        ExecutionError? error,
        ContinuationToken? continuation,
        ActionUncertainty? uncertainty)
        : this(kind, result, error, continuation, uncertainty, null)
    {
    }

    internal KernelUntypedActionOutcome(
        ActionOutcomeKind kind,
        JsonElement? result,
        ExecutionError? error,
        ContinuationToken? continuation,
        ActionUncertainty? uncertainty,
        object? authority)
    {
        Kind = kind;
        Result = result;
        Error = error;
        Continuation = continuation;
        Uncertainty = uncertainty;
        Authority = authority;
    }

    internal object? Authority { get; }
    public ActionOutcomeKind Kind { get; }
    public JsonElement? Result { get; }
    public ContinuationToken? Continuation { get; }
    public ExecutionError? Error { get; }
    public ActionUncertainty? Uncertainty { get; }
}
