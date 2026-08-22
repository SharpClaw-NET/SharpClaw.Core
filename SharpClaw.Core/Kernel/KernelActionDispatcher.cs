using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

public sealed class KernelActionDispatcher : IActionDispatcher
{
    private static readonly AsyncLocal<InvocationScope?> CurrentScope = new();
    private sealed record InvocationScope(
        Guid InvocationId,
        int Depth,
        KernelActionExecutionContext ExecutionContext);
    private readonly KernelGraph _graph;
    private readonly KernelActionExecutionContext _executionContext;
    private readonly IActionContinuationHost _continuationHost;
    private readonly ICommittedEventWriter _eventWriter;
    private readonly IKernelActionResultSnapshotter _resultSnapshotter;
    private readonly IKernelActionRepeatEvidenceAuthority _repeatEvidenceAuthority;
    private readonly ISidecarExternalActionDispatchAuthorityVerifier? _externalAuthorityVerifier;

    internal KernelActionExecutionContext ExecutionContext => _executionContext;

    public KernelActionDispatcher(
        KernelGraph graph,
        KernelActionExecutionContext executionContext,
        IActionContinuationHost? continuationHost = null,
        ICommittedEventWriter? eventWriter = null,
        IKernelActionResultSnapshotter? resultSnapshotter = null,
        IKernelActionRepeatEvidenceAuthority? repeatEvidenceAuthority = null,
        ISidecarExternalActionDispatchAuthorityVerifier? externalAuthorityVerifier = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _executionContext = executionContext ?? throw new ArgumentNullException(nameof(executionContext));
        _continuationHost = continuationHost ?? new InMemoryContinuationHost();
        _eventWriter = eventWriter ?? new KernelEventDispatcher(graph);
        _resultSnapshotter = resultSnapshotter ?? new JsonKernelActionResultSnapshotter();
        _repeatEvidenceAuthority = repeatEvidenceAuthority ?? new DenyKernelActionRepeatEvidenceAuthority();
        _externalAuthorityVerifier = externalAuthorityVerifier;
    }

    public ValueTask<IActionOutcome<TResult>> RunAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        ActionPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
        => RunCoreAsync(
            _executionContext,
            descriptor,
            action,
            terminal,
            snapshot,
            cancellationToken);

    public ValueTask<IActionOutcome<TResult>> RunWithContextAsync<TAction, TResult>(
        KernelActionExecutionContext executionContext,
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        ActionPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
        => RunCoreAsync(
            executionContext,
            descriptor,
            action,
            terminal,
            snapshot,
            cancellationToken);

    public ValueTask<IActionOutcome<TResult>> RunExternalAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        ActionPipelineSnapshot snapshot,
        SidecarExternalActionDispatchAuthority authority,
        CancellationToken cancellationToken)
    {
        if (authority is null)
        {
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                KernelActionOutcome<TResult>.Failed(
                    "ACTION_EXTERNAL_AUTHORITY_MISSING",
                    "The external action authority is required."));
        }
        var validation = SidecarExternalActionDispatchAuthorityValidator.Validate(
            authority,
            descriptor,
            action,
            snapshot,
            DateTimeOffset.UtcNow);
        if (!validation.Accepted)
        {
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                KernelActionOutcome<TResult>.Failed(validation.Code, validation.Message));
        }

        if (!string.Equals(
                snapshot.ContractHash,
                _graph.ActionSnapshot.ContractHash,
                StringComparison.Ordinal))
        {
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                KernelActionOutcome<TResult>.Failed(
                    "ACTION_SNAPSHOT_MISMATCH",
                    "The external action snapshot is not compatible with the host graph."));
        }

        if (_externalAuthorityVerifier is null)
        {
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                KernelActionOutcome<TResult>.Failed(
                    "ACTION_EXTERNAL_AUTHORITY_UNAVAILABLE",
                    "A trusted external action authority verifier is required."));
        }

        var authorityResult = _externalAuthorityVerifier.ValidateAndConsume(
            authority,
            DateTimeOffset.UtcNow);
        if (!authorityResult.Accepted)
        {
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                KernelActionOutcome<TResult>.Failed(
                    authorityResult.Code,
                    authorityResult.Message));
        }

        var definition = new CompiledActionDefinition<TAction, TResult>(
            descriptor,
            authority.ModuleId,
            [],
            descriptor.Capabilities,
            true,
            descriptor.InputSchema!,
            descriptor.ResultSchema!);
        var effectiveContext = authority.EffectiveHostEntry.EffectiveContext;
        var executionContext = new KernelActionExecutionContext(
            effectiveContext.Caller,
            effectiveContext.Features,
            effectiveContext.TraceId,
            effectiveContext.IdempotencyKey,
            authority.InitiatingHostContext);
        var invocation = new KernelActionInvocation<TAction, TResult>(
            definition,
            terminal,
            snapshot,
            executionContext,
            _continuationHost,
            _eventWriter,
            _resultSnapshotter,
            _repeatEvidenceAuthority,
            cancellationToken,
            effectiveContext.Deadline);
        return invocation.InvokeExternalAsync(
            action,
            effectiveContext.InvocationId,
            effectiveContext.ParentInvocationId,
            effectiveContext.Depth,
            effectiveContext.Attempt,
            cancellationToken);
    }

    private ValueTask<IActionOutcome<TResult>> RunCoreAsync<TAction, TResult>(
        KernelActionExecutionContext executionContext,
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        ActionPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
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
        executionContext = parent?.ExecutionContext ?? executionContext;
        var invocation = new KernelActionInvocation<TAction, TResult>(
            definition,
            terminal,
            snapshot,
            executionContext,
            _continuationHost,
            _eventWriter,
            _resultSnapshotter,
            _repeatEvidenceAuthority,
            cancellationToken);
        return invocation.InvokeRootAsync(action, depth, parent?.InvocationId, cancellationToken);
    }

    public async ValueTask<TResult> RunRequiredAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        ActionPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var outcome = await RunAsync(descriptor, action, terminal, snapshot, cancellationToken);
        return RequireResult(descriptor, outcome);
    }

    public async ValueTask<TResult> RunRequiredWithContextAsync<TAction, TResult>(
        KernelActionExecutionContext executionContext,
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        ActionPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var outcome = await RunWithContextAsync(
            executionContext,
            descriptor,
            action,
            terminal,
            snapshot,
            cancellationToken);
        return RequireResult(descriptor, outcome);
    }

    public async ValueTask<TResult> RunExternalRequiredAsync<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
        ActionPipelineSnapshot snapshot,
        SidecarExternalActionDispatchAuthority authority,
        CancellationToken cancellationToken)
    {
        var outcome = await RunExternalAsync(
            descriptor,
            action,
            terminal,
            snapshot,
            authority,
            cancellationToken);
        return RequireResult(descriptor, outcome);
    }

    private static TResult RequireResult<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        IActionOutcome<TResult> outcome)
    {
        if (outcome.Kind == ActionOutcomeKind.Completed)
            return outcome.Result!;

        if (outcome.Kind == ActionOutcomeKind.Uncertain && outcome.Uncertainty is not null)
            throw new ActionOutcomeUncertainException(outcome.Uncertainty);
        if (outcome.Kind == ActionOutcomeKind.Cancelled)
            throw new KernelActionCancelledException(
                outcome.Error ?? new ExecutionError("ACTION_CANCELLED", "The action was cancelled."));
        if (outcome.Kind == ActionOutcomeKind.Deferred && outcome.Continuation is not null)
            throw new KernelActionDeferredException(outcome.Continuation);
        throw new KernelActionFailedException(
            outcome.Error ?? new ExecutionError(
                "ACTION_FAILED",
                $"Action '{descriptor.Key.Value}' did not return a terminal result."));
    }

    private sealed class KernelActionInvocation<TAction, TResult>
    {
        private static readonly TimeSpan CancellationObservationWindow = TimeSpan.FromMilliseconds(25);
        private sealed record ActionAttempt(
            Guid InvocationId,
            Guid? ParentInvocationId,
            int Depth,
            int Number);
        private readonly CompiledActionDefinition<TAction, TResult> _definition;
        private readonly Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> _terminal;
        private readonly ActionPipelineSnapshot _snapshot;
        private readonly KernelActionExecutionContext _executionContext;
        private readonly IActionContinuationHost _continuationHost;
        private readonly ICommittedEventWriter _eventWriter;
        private readonly IKernelActionResultSnapshotter _resultSnapshotter;
        private readonly IKernelActionRepeatEvidenceAuthority _repeatEvidenceAuthority;
        private readonly CancellationToken _rootCancellationToken;
        private readonly SemaphoreSlim _uncertaintyLock = new(1, 1);
        private readonly DateTimeOffset _deadline;
        private ActionUncertainty? _recordedUncertainty;

        public KernelActionInvocation(
            CompiledActionDefinition<TAction, TResult> definition,
            Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>> terminal,
            ActionPipelineSnapshot snapshot,
            KernelActionExecutionContext executionContext,
            IActionContinuationHost continuationHost,
            ICommittedEventWriter eventWriter,
            IKernelActionResultSnapshotter resultSnapshotter,
            IKernelActionRepeatEvidenceAuthority repeatEvidenceAuthority,
            CancellationToken rootCancellationToken,
            DateTimeOffset? deadline = null)
        {
            _definition = definition;
            _terminal = terminal;
            _snapshot = snapshot;
            _executionContext = executionContext;
            _continuationHost = continuationHost;
            _eventWriter = eventWriter;
            _resultSnapshotter = resultSnapshotter;
            _repeatEvidenceAuthority = repeatEvidenceAuthority;
            _rootCancellationToken = rootCancellationToken;
            _deadline = deadline ?? DateTimeOffset.UtcNow + definition.Descriptor.DefaultTimeout;
        }

        public async ValueTask<IActionOutcome<TResult>> InvokeRootAsync(
            TAction action,
            int depth,
            Guid? parentInvocationId,
            CancellationToken cancellationToken) =>
            await InvokeAttemptAsync(
                action,
                new ActionAttempt(Guid.NewGuid(), parentInvocationId, depth, 1),
                cancellationToken);

        public ValueTask<IActionOutcome<TResult>> InvokeExternalAsync(
            TAction action,
            Guid invocationId,
            Guid? parentInvocationId,
            int depth,
            int attempt,
            CancellationToken cancellationToken) =>
            InvokeAttemptAsync(
                action,
                new ActionAttempt(invocationId, parentInvocationId, depth, attempt),
                cancellationToken);

        private async ValueTask<IActionOutcome<TResult>> InvokeAttemptAsync(
            TAction action,
            ActionAttempt attempt,
            CancellationToken cancellationToken)
        {
            var previous = CurrentScope.Value;
            CurrentScope.Value = new InvocationScope(
                attempt.InvocationId,
                attempt.Depth,
                _executionContext);
            try
            {
                await PublishLifecycleAsync(
                    attempt,
                    KernelActionLifecycleEvents.DescriptorFor(SharpClawEvents.ActionStarting),
                    null,
                    CancellationToken.None);
                var outcome = await InvokeFrameAsync(
                    action,
                    0,
                    attempt,
                    cancellationToken);
                await PublishLifecycleAsync(
                    attempt,
                    KernelActionLifecycleEvents.ForOutcome(outcome.Kind),
                    outcome,
                    CancellationToken.None);
                return outcome;
            }
            finally
            {
                CurrentScope.Value = previous;
            }
        }

        private ValueTask PublishLifecycleAsync(
            ActionAttempt attempt,
            EventDescriptor<KernelActionLifecycleEvent> descriptor,
            IActionOutcome<TResult>? outcome,
            CancellationToken cancellationToken) =>
            _eventWriter.PublishAsync(
                descriptor,
                new KernelActionLifecycleEvent(
                    attempt.InvocationId,
                    attempt.ParentInvocationId,
                    _executionContext.TraceId,
                    _definition.Descriptor.Key,
                    _definition.Descriptor.Version,
                    outcome?.Kind,
                    outcome?.Error,
                    outcome?.Continuation,
                    outcome?.Uncertainty,
                    DateTimeOffset.UtcNow),
                cancellationToken);

        private async ValueTask<IActionOutcome<TResult>> InvokeFrameAsync(
            TAction action,
            int index,
            ActionAttempt attempt,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return KernelActionOutcome<TResult>.Cancelled(
                    "ACTION_CANCELLED",
                    "The action was cancelled before the next action path started.");
            if (attempt.Depth > _snapshot.MaximumActionDepth)
                return KernelActionOutcome<TResult>.Failed(
                    "ACTION_DEPTH_EXCEEDED",
                    "The action graph exceeded its maximum recursion depth.");
            if (DateTimeOffset.UtcNow >= _deadline)
                return KernelActionOutcome<TResult>.Failed(
                    "ACTION_DEADLINE_EXCEEDED",
                    "The action deadline expired before this action path completed.");

            var ownerModuleId = index >= _definition.Frames.Count
                ? _definition.OwnerModuleId
                : _definition.Frames[index].OwnerModuleId;
            var context = new ActionContext<TAction>(
                attempt.InvocationId,
                attempt.ParentInvocationId,
                _executionContext.TraceId,
                _executionContext.IdempotencyKey,
                attempt.Depth,
                attempt.Number,
                _deadline,
                _definition.Descriptor.Key,
                ownerModuleId,
                _executionContext.Caller,
                action,
                _executionContext.Features,
                _snapshot);

            if (index >= _definition.Frames.Count)
                return await InvokeTerminalAsync(context, cancellationToken);

            var activeFrame = _definition.Frames[index];

            var frame = activeFrame;
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
                return ValidateOutcome(outcome, control);
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
                return await InvokeFrameAsync(
                    context.Action,
                    FrameIndex(frame) + 1,
                    AttemptFrom(context),
                    cancellationToken);
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
            catch (KernelOperationCancellationException exception)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return Reissue(known!, control.Authority);
                if (control.ContinuationStarted || exception.OperationStillRunning)
                    return await RecordControlUncertaintyAsync(
                        context,
                        control,
                        exception.Message,
                        CancellationToken.None);
                return KernelActionOutcome<TResult>.Cancelled(
                    "ACTION_CANCELLED",
                    "The action was cancelled before an external effect started.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return Reissue(known!, control.Authority);
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
                        AttemptFrom(context),
                        cancellationToken);
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
                _definition.InputSchema,
                _definition.ResultSchema,
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
                SerializeUntypedInput(context.Action));
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
                if (!control.OwnsOutcome(outcome))
                    return KernelActionOutcome<TResult>.Failed(
                        "ACTION_FORGED_OUTCOME",
                        "The action interceptor returned an outcome that this control did not issue.");
                return control.TryGetKnownOutcome(out var known)
                    ? known!
                    : KernelActionOutcome<TResult>.Failed(
                        "ACTION_NULL_OUTCOME",
                        "The action interceptor returned no authoritative outcome.");
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
                return await InvokeFrameAsync(
                    context.Action,
                    FrameIndex(frame) + 1,
                    AttemptFrom(context),
                    cancellationToken);
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
            catch (KernelOperationCancellationException exception)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return Reissue(known!, control.Authority);
                if (control.ContinuationStarted || exception.OperationStillRunning)
                    return await RecordControlUncertaintyAsync(
                        context,
                        control,
                        exception.Message,
                        CancellationToken.None);
                return KernelActionOutcome<TResult>.Cancelled(
                    "ACTION_CANCELLED",
                    "The action was cancelled before an external effect started.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return Reissue(known!, control.Authority);
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
                        AttemptFrom(context),
                        cancellationToken);
                return KernelActionOutcome<TResult>.Failed("ACTION_INTERCEPTOR_FAILED", exception.Message);
            }
        }

        private async ValueTask<IActionOutcome<TResult>> InvokeTerminalAsync(
            ActionContext<TAction> context,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await InvokeBoundedAsync(
                    token => _terminal(context, token),
                    null,
                    cancellationToken);
                return KernelActionOutcome<TResult>.Completed(result);
            }
            catch (ActionOutcomeUncertainException exception)
            {
                var existing = await _continuationHost.GetRecoveryAsync(
                    exception.Uncertainty.Recovery.RecoveryId,
                    CancellationToken.None);
                if (existing is not null && existing.Uncertainty == exception.Uncertainty)
                    return KernelActionOutcome<TResult>.Uncertain(exception.Uncertainty);
                if (!CanRecordUncertainty)
                    return KernelActionOutcome<TResult>.Failed(
                        "ACTION_CONTINUATION_DENIED",
                        "An uncertain durable action requires a durable continuation host.");
                var uncertainty = await RecordUncertaintyAsync(
                    context.InvocationId,
                    exception.Uncertainty,
                    cancellationToken,
                    KernelJson.Serialize(context.Action).GetRawText());
                return KernelActionOutcome<TResult>.Uncertain(uncertainty);
            }
            catch (KernelActionCancelledException exception)
            {
                return KernelActionOutcome<TResult>.Cancelled(
                    exception.Error.Code,
                    exception.Error.Message);
            }
            catch (KernelActionDeferredException exception)
            {
                return KernelActionOutcome<TResult>.Deferred(exception.Continuation);
            }
            catch (KernelActionFailedException exception)
            {
                return KernelActionOutcome<TResult>.Failed(exception.Error);
            }
            catch (KernelOperationTimeoutException exception) when (exception.OperationStillRunning)
            {
                if (!CanRecordUncertainty)
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
                        _executionContext.IdempotencyKey),
                    DateTimeOffset.UtcNow);
                var uncertainty = await RecordUncertaintyAsync(
                    context.InvocationId,
                    supplied,
                    cancellationToken,
                    KernelJson.Serialize(context.Action).GetRawText());
                return KernelActionOutcome<TResult>.Uncertain(uncertainty);
            }
            catch (TimeoutException exception)
            {
                return KernelActionOutcome<TResult>.Failed("ACTION_DEADLINE_EXCEEDED", exception.Message);
            }
            catch (KernelOperationCancellationException exception)
            {
                if (!exception.OperationStillRunning)
                    return KernelActionOutcome<TResult>.Cancelled(
                        "ACTION_CANCELLED",
                        "The action was cancelled before an external effect started.");
                var supplied = new ActionUncertainty(
                    "ACTION_OUTCOME_UNCERTAIN",
                    exception.Message,
                    ActionExecutionStage.ContinuationRunning,
                    null,
                    new ActionRecoveryReference(
                        Guid.NewGuid(),
                        _definition.Descriptor.Key,
                        _definition.Descriptor.Version,
                        _executionContext.IdempotencyKey),
                    DateTimeOffset.UtcNow);
                var uncertainty = await RecordUncertaintyAsync(
                    context.InvocationId,
                    supplied,
                    CancellationToken.None,
                    KernelJson.Serialize(context.Action).GetRawText());
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
                AttemptFrom(context),
                cancellationToken);
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
                AttemptFrom(context),
                cancellationToken);
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
            if (!CanRecordUncertainty)
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
            await _uncertaintyLock.WaitAsync(CancellationToken.None);
            try
            {
                if (_recordedUncertainty is not null)
                    return _recordedUncertainty;
                var recovery = await _continuationHost.RecordUncertaintyAsync(
                    new KernelUncertaintyRequest(
                        invocationId,
                        _definition.Descriptor.Key,
                        _definition.Descriptor.Version,
                        _executionContext.IdempotencyKey,
                        supplied.Stage,
                        supplied.Code,
                        supplied.Message,
                        supplied.ReceiptReference,
                        _snapshot.ContractHash,
                        new ContinuationDestination("action-recovery", _definition.Descriptor.Key.Value),
                        protectedInput,
                        _definition.Descriptor.ContinuationPolicy),
                    CancellationToken.None);
                _recordedUncertainty = recovery.Uncertainty;
                return _recordedUncertainty;
            }
            finally
            {
                _uncertaintyLock.Release();
            }
        }

        private bool CanRecordUncertainty =>
            _continuationHost.SupportsDurableState &&
            _definition.Descriptor.ContinuationPolicy?.Durable != false;

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
            catch (KernelActionCancelledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                linked.Cancel();
                if (await ObserveCompletionAsync(operationTask))
                {
                    try
                    {
                        return await operationTask;
                    }
                    catch (OperationCanceledException)
                    {
                        throw new KernelOperationTimeoutException(
                            "The action hook or terminal exceeded its deadline.",
                            false);
                    }
                }
                throw new KernelOperationTimeoutException(
                    "The action hook or terminal exceeded its deadline.",
                    true);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && linked.IsCancellationRequested)
            {
                throw new KernelOperationTimeoutException(
                    "The action hook or terminal exceeded its deadline.",
                    false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                linked.Cancel();
                if (await ObserveCompletionAsync(operationTask))
                    return await operationTask;
                throw new KernelOperationCancellationException(
                    "Caller cancellation occurred while the action hook or terminal was still running.",
                    true);
            }
        }

        private static async ValueTask<bool> ObserveCompletionAsync(Task operationTask)
        {
            if (operationTask.IsCompleted)
                return true;
            var observed = await Task.WhenAny(
                operationTask,
                Task.Delay(CancellationObservationWindow, CancellationToken.None));
            return ReferenceEquals(observed, operationTask);
        }

        private JsonElement SerializeUntypedInput(TAction action)
        {
            if (!UsesStandardEnvelope)
                return KernelJson.Serialize(action);
            var envelope = (KernelActionEnvelope)(object)action!;
            return KernelJson.Serialize(envelope.Payload);
        }

        private TAction DeserializeUntypedInput(JsonElement input)
        {
            if (!UsesStandardEnvelope)
                return KernelJson.Deserialize<TAction>(input);
            var contract = KernelActionCatalog.DescriptorFor(_definition.Descriptor.Key);
            var payloadType = contract.InputPayloadType ?? throw new KernelActionExecutionException(
                $"Action '{_definition.Descriptor.Key.Value}' has no host envelope payload type.");
            var payload = KernelJson.Deserialize(input, payloadType);
            return (TAction)(object)new KernelActionEnvelope(_definition.Descriptor.Key, payload);
        }

        private TResult DeserializeUntypedResult(JsonElement result)
        {
            if (!UsesStandardEnvelope)
                return KernelJson.Deserialize<TResult>(result);
            var contract = KernelActionCatalog.DescriptorFor(_definition.Descriptor.Key);
            var resultType = contract.ResultPayloadType ?? throw new KernelActionExecutionException(
                $"Action '{_definition.Descriptor.Key.Value}' has no host envelope result type.");
            return (TResult)KernelJson.Deserialize(result, resultType)!;
        }

        private bool UsesStandardEnvelope =>
            typeof(TAction) == typeof(KernelActionEnvelope) &&
            typeof(TResult) == typeof(object) &&
            SharpClawActionCatalog.Kernel.Contains(_definition.Descriptor.Key);

        private TResult SnapshotResult(TResult result) => _resultSnapshotter.Snapshot(result);

        private IActionOutcome<TResult> SnapshotOutcome(
            IActionOutcome<TResult> outcome,
            object authority)
        {
            if (outcome is not KernelActionOutcome<TResult> trusted)
            {
                return KernelActionOutcome<TResult>.FailedBy(
                    new ExecutionError(
                        "ACTION_FORGED_OUTCOME",
                        "The action path returned an outcome that Core did not issue."),
                    authority);
            }

            var result = trusted.Kind == ActionOutcomeKind.Completed
                ? SnapshotResult(trusted.Result)
                : default!;
            return KernelActionOutcome<TResult>.FromAuthority(
                trusted.Kind,
                result,
                trusted.Error,
                trusted.Continuation,
                trusted.Uncertainty,
                authority);
        }

        private interface IKernelControlState
        {
            object Authority { get; }

            bool ContinuationStarted { get; }

            ActionExecutionStage ExecutionStage { get; }

            bool TryGetKnownOutcome(out IActionOutcome<TResult>? outcome);

            bool OwnsOutcome(object? outcome);

            void ConsumeForUncertainty();

        }

        private IActionOutcome<TResult> ValidateOutcome(
            IActionOutcome<TResult>? outcome,
            IKernelControlState control)
        {
            if (outcome is null)
                return KernelActionOutcome<TResult>.Failed(
                    "ACTION_NULL_OUTCOME",
                    "An action interceptor returned no outcome.");
            if (!control.OwnsOutcome(outcome))
                return KernelActionOutcome<TResult>.Failed(
                    "ACTION_FORGED_OUTCOME",
                    "The action interceptor returned an outcome that this control did not issue.");
            return control.TryGetKnownOutcome(out var known)
                ? known!
                : KernelActionOutcome<TResult>.Failed(
                    "ACTION_NULL_OUTCOME",
                    "An action interceptor returned no authoritative outcome.");
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
            private IActionOutcome<TResult>? _knownOutcome;
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
                outcome = _knownOutcome;
                return outcome is not null;
            }

            public bool OwnsOutcome(object? outcome) => ReferenceEquals(outcome, _issuedOutcome);

            public void ConsumeForUncertainty()
            {
                _used = true;
                _executionStage = _continuationStarted
                    ? ActionExecutionStage.AfterContinuation
                    : ActionExecutionStage.TerminalReturned;
            }

            public ValueTask<IActionOutcome<TResult>> ProceedAsync(CancellationToken cancellationToken) =>
                ProceedWith(action, cancellationToken, ActionInterceptionCapabilities.Inspect);

            public ValueTask<IActionOutcome<TResult>> ProceedWithInputAsync(
                ActionReplacement<TAction> replacement,
                CancellationToken cancellationToken) =>
                ProceedWith(
                    replacement.Value,
                    cancellationToken,
                    ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.ReplaceInput);

            public IActionOutcome<TResult> ReplaceResult(TResult result, string reason)
            {
                EnsureCapability(ActionInterceptionCapabilities.ReplaceResult);
                _used = true;
                return Issue(KernelActionOutcome<TResult>.CompletedBy(result, _authority));
            }

            public IActionOutcome<TResult> Cancel(string code, string message)
            {
                EnsureCapability(ActionInterceptionCapabilities.Cancel);
                _used = true;
                return Issue(KernelActionOutcome<TResult>.CancelledBy(code, message, _authority));
            }

            public IActionOutcome<TResult> Fail(ExecutionError error)
            {
                ArgumentNullException.ThrowIfNull(error);
                EnsureAvailable();
                _used = true;
                return Issue(KernelActionOutcome<TResult>.FailedBy(error, _authority));
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
                    return Issue(KernelActionOutcome<TResult>.FailedBy(
                        new ExecutionError(
                            "ACTION_CONTINUATION_DENIED",
                            "A durable continuation requires durable action policy and durable host state."),
                        _authority));
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
                        KernelJson.Serialize(context.Action).GetRawText(),
                        owner._definition.Descriptor.RepeatPolicy),
                    cancellationToken);
                return Issue(KernelActionOutcome<TResult>.DeferredBy(token, _authority));
            }

            public async ValueTask<IActionOutcome<TResult>> RepeatAsync(
                ActionRepeatRequest<TAction> request,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(request);
                EnsureCapability(ActionInterceptionCapabilities.Repeat);
                var policy = owner._definition.Descriptor.RepeatPolicy;
                if (!CanRequestRepeat(policy))
                {
                    _used = true;
                    return Issue(KernelActionOutcome<TResult>.FailedBy(
                        new ExecutionError(
                            "ACTION_REPEAT_DENIED",
                            "The action repeat policy does not permit another attempt."),
                        _authority));
                }

                _used = true;
                var nextInvocationId = Guid.NewGuid();
                var requestedAt = DateTimeOffset.UtcNow;
                var evidenceRequest = new KernelActionRepeatEvidenceRequest(
                    RequiredEvidence(policy.Kind),
                    context.ActionKey,
                    owner._definition.Descriptor.Version,
                    policy.IdempotencyScope,
                    context.IdempotencyKey,
                    context.InvocationId,
                    context.Attempt,
                    nextInvocationId,
                    context.Attempt + 1,
                    requestedAt);
                var evidence = await owner._repeatEvidenceAuthority.AuthorizeAsync(
                    evidenceRequest,
                    cancellationToken);
                if (!ValidEvidence(evidenceRequest, evidence, DateTimeOffset.UtcNow))
                {
                    return Issue(KernelActionOutcome<TResult>.FailedBy(
                        new ExecutionError(
                            "ACTION_REPEAT_EVIDENCE_INVALID",
                            "The host did not issue valid evidence for the requested action repeat."),
                        _authority));
                }

                var backoff = request.Backoff.GetValueOrDefault();
                if (policy.MinimumBackoff > backoff)
                    backoff = policy.MinimumBackoff;
                if (backoff > TimeSpan.Zero)
                    await Task.Delay(backoff, cancellationToken);
                if (!ValidEvidence(evidenceRequest, evidence, DateTimeOffset.UtcNow))
                {
                    return Issue(KernelActionOutcome<TResult>.FailedBy(
                        new ExecutionError(
                            "ACTION_REPEAT_EVIDENCE_INVALID",
                            "The host evidence expired before the next action attempt started."),
                        _authority));
                }
                var outcome = await owner.InvokeAttemptAsync(
                    request.Value,
                    new ActionAttempt(
                        nextInvocationId,
                        context.ParentInvocationId,
                        context.Depth,
                        context.Attempt + 1),
                    cancellationToken);
                return Issue(outcome);
            }

            private ValueTask<IActionOutcome<TResult>> ProceedWith(
                TAction nextAction,
                CancellationToken cancellationToken,
                ActionInterceptionCapabilities required)
            {
                EnsureCapability(ActionInterceptionCapabilities.Wrap);
                EnsureCapability(required & ~ActionInterceptionCapabilities.Wrap);
                _used = true;
                _continuationStarted = true;
                _executionStage = ActionExecutionStage.ContinuationRunning;
                return new ValueTask<IActionOutcome<TResult>>(
                    ProceedCoreAsync(nextAction, cancellationToken));
            }

            private async Task<IActionOutcome<TResult>> ProceedCoreAsync(
                TAction nextAction,
                CancellationToken cancellationToken)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    owner._rootCancellationToken,
                    cancellationToken);
                _continuationTask = owner.InvokeFrameAsync(
                    nextAction,
                    owner.FrameIndex(frame) + 1,
                    AttemptFrom(context),
                    linked.Token).AsTask();
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
                var issued = Issue(outcome);
                _executionStage = outcome.Kind == ActionOutcomeKind.Uncertain
                    ? ActionExecutionStage.TerminalReturned
                    : ActionExecutionStage.Committed;
                _executionStage = ActionExecutionStage.AfterContinuation;
                return issued;
            }

            private IActionOutcome<TResult> Issue(IActionOutcome<TResult> outcome)
            {
                _knownOutcome = owner.SnapshotOutcome(outcome, _authority);
                _issuedOutcome = owner.SnapshotOutcome(_knownOutcome, _authority);
                return _issuedOutcome;
            }

            private bool CanRequestRepeat(ActionRepeatPolicy policy)
            {
                if (policy.Kind == ActionRepeatKind.None || context.Attempt >= policy.MaximumAttempts)
                    return false;
                return !string.IsNullOrWhiteSpace(policy.IdempotencyScope) &&
                       context.IdempotencyKey != Guid.Empty;
            }

            private static KernelActionRepeatEvidenceKind RequiredEvidence(ActionRepeatKind kind) =>
                kind switch
                {
                    ActionRepeatKind.ConflictOnly => KernelActionRepeatEvidenceKind.Conflict,
                    ActionRepeatKind.Idempotent => KernelActionRepeatEvidenceKind.IdempotencyAccepted,
                    ActionRepeatKind.Receipted => KernelActionRepeatEvidenceKind.DurableReceipt,
                    _ => throw new KernelActionExecutionException(
                        $"Repeat kind '{kind}' does not have an evidence contract.")
                };

            private static bool ValidEvidence(
                KernelActionRepeatEvidenceRequest request,
                KernelActionRepeatEvidence? evidence,
                DateTimeOffset now) =>
                evidence is not null &&
                !string.IsNullOrWhiteSpace(evidence.EvidenceId) &&
                evidence.Kind == request.RequiredKind &&
                evidence.ActionKey == request.ActionKey &&
                evidence.ActionVersion == request.ActionVersion &&
                string.Equals(evidence.IdempotencyScope, request.IdempotencyScope, StringComparison.Ordinal) &&
                evidence.IdempotencyKey == request.IdempotencyKey &&
                evidence.PriorInvocationId == request.PriorInvocationId &&
                evidence.PriorAttempt == request.PriorAttempt &&
                evidence.NextInvocationId == request.NextInvocationId &&
                evidence.NextAttempt == request.NextAttempt &&
                evidence.IssuedAt >= request.RequestedAt &&
                evidence.IssuedAt <= now &&
                evidence.ExpiresAt > now;

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

        private static ActionAttempt AttemptFrom(ActionContext<TAction> context) =>
            new(
                context.InvocationId,
                context.ParentInvocationId,
                context.Depth,
                context.Attempt);

        private sealed class UntypedActionControl(
            KernelActionInvocation<TAction, TResult> owner,
            ActionContext<TAction> context,
            AnyActionFrame<TAction, TResult> frame,
            TAction action) : IUntypedActionControl, IKernelControlState
        {
            private readonly TypedActionControl _typed = new(owner, context, frame, action);
            private IUntypedActionOutcome? _issuedOutcome;

            public object Authority => _typed.Authority;

            public bool ContinuationStarted => _typed.ContinuationStarted;

            public ActionExecutionStage ExecutionStage => _typed.ExecutionStage;

            public bool TryGetKnownOutcome(out IActionOutcome<TResult>? outcome) =>
                _typed.TryGetKnownOutcome(out outcome);

            public bool OwnsOutcome(object? outcome) => ReferenceEquals(outcome, _issuedOutcome);

            public void ConsumeForUncertainty() => _typed.ConsumeForUncertainty();

            public async ValueTask<IUntypedActionOutcome> ProceedAsync(CancellationToken cancellationToken) =>
                Issue(await _typed.ProceedAsync(cancellationToken));

            public async ValueTask<IUntypedActionOutcome> ProceedWithInputAsync(
                JsonElement input,
                string reason,
                CancellationToken cancellationToken) =>
                Issue(await _typed.ProceedWithInputAsync(
                    new ActionReplacement<TAction>(owner.DeserializeUntypedInput(input), reason),
                    cancellationToken));

            public IUntypedActionOutcome ReplaceResult(JsonElement result, string reason) =>
                Issue(_typed.ReplaceResult(owner.DeserializeUntypedResult(result), reason));

            public IUntypedActionOutcome Cancel(string code, string message) =>
                Issue(_typed.Cancel(code, message));

            public IUntypedActionOutcome Fail(ExecutionError error) => Issue(_typed.Fail(error));

            public async ValueTask<IUntypedActionOutcome> DeferAsync(
                ActionDeferRequest request,
                CancellationToken cancellationToken) =>
                Issue(await _typed.DeferAsync(request, cancellationToken));

            public async ValueTask<IUntypedActionOutcome> RepeatAsync(
                JsonElement input,
                string reason,
                TimeSpan? backoff,
                CancellationToken cancellationToken) =>
                Issue(await _typed.RepeatAsync(
                    new ActionRepeatRequest<TAction>(owner.DeserializeUntypedInput(input), reason, backoff),
                    cancellationToken));

            private IUntypedActionOutcome Issue(IActionOutcome<TResult> outcome)
            {
                _issuedOutcome = new KernelUntypedActionOutcome(
                    outcome.Kind,
                    outcome.Result is null ? null : KernelJson.Serialize(outcome.Result),
                    outcome.Error,
                    outcome.Continuation,
                    outcome.Uncertainty,
                    ((KernelActionOutcome<TResult>)outcome).Authority);
                return _issuedOutcome;
            }
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

internal sealed class KernelOperationCancellationException(
    string message,
    bool operationStillRunning) : OperationCanceledException(message)
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
