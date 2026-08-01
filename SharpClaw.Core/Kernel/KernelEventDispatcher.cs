using System.Collections.Concurrent;
using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

public sealed class KernelEventDispatcher : ICommittedEventWriter
{
    private readonly KernelGraph _graph;
    private readonly IKernelEventDeliverySink _deliverySink;

    public KernelEventDispatcher(KernelGraph graph, IKernelEventDeliverySink? deliverySink = null)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _deliverySink = deliverySink ?? new InMemoryEventDeliverySink();
    }

    public ValueTask PublishAsync<TEvent>(
        EventDescriptor<TEvent> descriptor,
        TEvent payload,
        CancellationToken cancellationToken) =>
        PublishAndDeliverAsync(
            descriptor,
            payload,
            _graph.ActionSnapshot,
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            cancellationToken);

    public async ValueTask<IEventInterception<TEvent>> DispatchAsync<TEvent>(
        EventDescriptor<TEvent> descriptor,
        TEvent payload,
        ActionPipelineSnapshot snapshot,
        RequestPrincipal? caller = null,
        ExtensionFeatureSet? features = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(
                snapshot.ContractHash,
                _graph.ActionSnapshot.ContractHash,
                StringComparison.Ordinal))
            throw new KernelActionExecutionException(
                "The event pipeline snapshot is not compatible with the compiled kernel graph.");
        var definition = _graph.GetEvent(descriptor.Key);
        if (definition is not CompiledEventDefinition<TEvent> typed)
            throw new KernelActionExecutionException(
                $"Event '{descriptor.Key.Value}' was compiled for '{definition.EventType.FullName}'.");
        if (typed.Descriptor.Version != descriptor.Version)
            throw new KernelActionExecutionException(
                $"Event '{descriptor.Key.Value}' was invoked with version {descriptor.Version}, " +
                $"but the graph contains version {typed.Descriptor.Version}.");
        if (!KernelGraphHasher.Flatten("descriptor", typed.Descriptor)
                .SequenceEqual(KernelGraphHasher.Flatten("descriptor", descriptor)))
            throw new KernelActionExecutionException(
                $"Event '{descriptor.Key.Value}' does not match the compiled descriptor schema.");
        if (typed.Interceptors.Count == 0 && typed.Listeners.Count == 0)
            return KernelEventInterception<TEvent>.Continued(payload);

        var invocation = new KernelEventInvocation<TEvent>(
            typed,
            snapshot,
            caller ?? RequestPrincipal.Anonymous,
            features ?? ExtensionFeatureSet.Empty,
            _deliverySink,
            cancellationToken);
        var result = await invocation.InvokeAsync(payload, 0, cancellationToken);
        if (result.Kind is EventInterceptionKind.Continued or EventInterceptionKind.Replaced)
            await invocation.DeliverListenersAsync(result.Payload!, cancellationToken);
        return result;
    }

    private async ValueTask PublishAndDeliverAsync<TEvent>(
        EventDescriptor<TEvent> descriptor,
        TEvent payload,
        ActionPipelineSnapshot snapshot,
        RequestPrincipal caller,
        ExtensionFeatureSet features,
        CancellationToken cancellationToken)
    {
        var result = await DispatchAsync(descriptor, payload, snapshot, caller, features, cancellationToken);
        if (result.Kind is EventInterceptionKind.Cancelled or EventInterceptionKind.Failed)
            throw new KernelActionExecutionException(
                $"Event '{descriptor.Key.Value}' was not delivered. " +
                $"{result.Error?.Message ?? result.Kind.ToString()}.");
    }

    private sealed class KernelEventInvocation<TEvent>
    {
        private static readonly TimeSpan CancellationObservationWindow = TimeSpan.FromMilliseconds(25);
        private readonly CompiledEventDefinition<TEvent> _definition;
        private readonly ActionPipelineSnapshot _snapshot;
        private readonly RequestPrincipal _caller;
        private readonly ExtensionFeatureSet _features;
        private readonly IKernelEventDeliverySink _deliverySink;
        private readonly Guid _eventId = Guid.NewGuid();
        private readonly Guid _traceId = Guid.NewGuid();

        public KernelEventInvocation(
            CompiledEventDefinition<TEvent> definition,
            ActionPipelineSnapshot snapshot,
            RequestPrincipal caller,
            ExtensionFeatureSet features,
            IKernelEventDeliverySink deliverySink,
            CancellationToken rootCancellationToken)
        {
            _definition = definition;
            _snapshot = snapshot;
            _caller = caller;
            _features = features;
            _deliverySink = deliverySink;
        }

        public async ValueTask<IEventInterception<TEvent>> InvokeAsync(
            TEvent payload,
            int index,
            CancellationToken cancellationToken)
        {
            if (index >= _definition.Interceptors.Count)
                return KernelEventInterception<TEvent>.Continued(payload);

            var envelope = new EventEnvelope<TEvent>(
                _eventId,
                null,
                _traceId,
                DateTimeOffset.UtcNow,
                _definition.OwnerModuleId,
                payload);
            var context = new EventContext<TEvent>(
                _definition.Descriptor,
                envelope,
                _caller,
                _features,
                _snapshot.ContractHash);
            var frame = _definition.Interceptors[index];
            var control = new TypedEventControl(this, payload, index, cancellationToken, frame);
            try
            {
                IEventInterception<TEvent> outcome;
                if (frame is TypedEventFrame<TEvent> typed)
                {
                    outcome = await InvokeBoundedAsync(
                        token => typed.Interceptor.InterceptAsync(context, control, token),
                        frame.Ordering,
                        cancellationToken);
                }
                else if (frame is AnyEventFrame<TEvent> any)
                {
                    var descriptor = new UntypedEventDescriptor(
                        _definition.Descriptor.Key,
                        _definition.Descriptor.Version,
                        _definition.Descriptor.Category,
                        frame.EffectiveCapabilities,
                        _definition.PayloadSchema,
                        _definition.Descriptor.ContainsSensitiveData);
                    var untypedEnvelope = new UntypedEventEnvelope(
                        descriptor,
                        envelope.EventId,
                        envelope.ActionInvocationId,
                        envelope.TraceId,
                        envelope.Timestamp,
                        envelope.OwnerModuleId,
                        KernelJson.Serialize(payload));
                    var untypedControl = new UntypedEventControl(control);
                    var untypedOutcome = await InvokeBoundedAsync(
                        token => any.Interceptor.InterceptAsync(
                            new UntypedEventContext(untypedEnvelope),
                            untypedControl,
                            token),
                        frame.Ordering,
                        cancellationToken);
                    if (untypedOutcome is not KernelUntypedEventInterception trusted ||
                        !ReferenceEquals(trusted.Authority, control.Authority))
                    {
                        return Failed("EVENT_FORGED_OUTCOME", "The event interceptor returned an outcome that Core did not issue.");
                    }

                    outcome = Convert(trusted, payload, control.Authority);
                }
                else
                {
                    return Failed("EVENT_FRAME_INVALID", "The compiled event graph contains an unknown frame.");
                }

                return Validate(outcome, control.Authority);
            }
            catch (KernelCapabilityException exception)
            {
                return Failed("EVENT_CAPABILITY_DENIED", exception.Message);
            }
            catch (KernelControlException exception)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return control.Reissue(known!);
                return Failed("EVENT_CONTROL_CONSUMED", exception.Message);
            }
            catch (KernelEventOperationTimeoutException exception) when (frame.Ordering.FailurePolicy == HookFailurePolicy.BestEffort)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return control.Reissue(known!);
                if (control.ContinuationStarted || exception.OperationStillRunning)
                {
                    control.ConsumeForUncertainty();
                    return Failed("EVENT_OUTCOME_UNCERTAIN", exception.Message, control.Authority);
                }
                return await InvokeAsync(payload, index + 1, cancellationToken);
            }
            catch (TimeoutException exception) when (frame.Ordering.FailurePolicy == HookFailurePolicy.BestEffort)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return control.Reissue(known!);
                if (control.ContinuationStarted)
                    return Failed("EVENT_OUTCOME_UNCERTAIN", exception.Message, control.Authority);
                return await InvokeAsync(payload, index + 1, cancellationToken);
            }
            catch (KernelEventOperationTimeoutException exception)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return control.Reissue(known!);
                if (control.ContinuationStarted || exception.OperationStillRunning)
                {
                    control.ConsumeForUncertainty();
                    return Failed("EVENT_OUTCOME_UNCERTAIN", exception.Message, control.Authority);
                }
                return Failed("EVENT_HOOK_TIMEOUT", exception.Message, control.Authority);
            }
            catch (KernelEventOperationCancellationException exception)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return control.Reissue(known!);
                if (control.ContinuationStarted || exception.OperationStillRunning)
                {
                    control.ConsumeForUncertainty();
                    return Failed("EVENT_OUTCOME_UNCERTAIN", exception.Message, control.Authority);
                }
                return Failed("EVENT_CANCELLED", "The event was cancelled.", control.Authority);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return control.Reissue(known!);
                return Failed("EVENT_CANCELLED", "The event was cancelled.");
            }
            catch (TimeoutException exception)
            {
                return Failed("EVENT_HOOK_TIMEOUT", exception.Message);
            }
            catch (Exception exception)
            {
                if (control.TryGetKnownOutcome(out var known))
                    return control.Reissue(known!);
                if (control.ContinuationStarted)
                    return Failed("EVENT_OUTCOME_UNCERTAIN", exception.Message, control.Authority);
                if (frame.Ordering.FailurePolicy == HookFailurePolicy.BestEffort)
                    return await InvokeAsync(payload, index + 1, cancellationToken);
                return Failed("EVENT_INTERCEPTOR_FAILED", exception.Message);
            }
        }

        public async ValueTask DeliverListenersAsync(TEvent payload, CancellationToken cancellationToken)
        {
            var envelope = new EventEnvelope<TEvent>(
                _eventId,
                null,
                _traceId,
                DateTimeOffset.UtcNow,
                _definition.OwnerModuleId,
                payload);
            foreach (var listener in _definition.Listeners)
            {
                if (listener.Delivery == EventDelivery.Inline)
                {
                    if (listener.Listener is IEventListener<TEvent> typed)
                    {
                        await InvokeListenerAsync(
                            token => typed.OnEventAsync(envelope, token),
                            listener.Ordering,
                            cancellationToken);
                    }
                    else
                    {
                        await InvokeListenerAsync(
                            token => ((IAnyEventListener)listener.Listener).OnEventAsync(
                                new UntypedEventEnvelope(
                                    CreateUntypedDescriptor(listener.EffectiveCapabilities),
                                    envelope.EventId,
                                    envelope.ActionInvocationId,
                                    envelope.TraceId,
                                    envelope.Timestamp,
                                    envelope.OwnerModuleId,
                                    KernelJson.Serialize(payload)),
                                token),
                            listener.Ordering,
                            cancellationToken);
                    }
                }
                else
                {
                    if (listener.Delivery == EventDelivery.Durable && !_deliverySink.SupportsDurable)
                        throw new KernelActionExecutionException(
                            $"Event '{_definition.Descriptor.Key.Value}' requires a durable event sink.");
                    object value = listener.Listener is IEventListener<TEvent>
                        ? envelope
                        : new UntypedEventEnvelope(
                            CreateUntypedDescriptor(listener.EffectiveCapabilities),
                            envelope.EventId,
                            envelope.ActionInvocationId,
                            envelope.TraceId,
                            envelope.Timestamp,
                            envelope.OwnerModuleId,
                            KernelJson.Serialize(payload));
                    await _deliverySink.EnqueueAsync(
                        _definition.Descriptor.Key,
                        value,
                        listener.Delivery,
                        cancellationToken,
                        listener.Id);
                }
            }
        }

        private UntypedEventDescriptor CreateUntypedDescriptor(
            EventInterceptionCapabilities effectiveCapabilities) => new(
            _definition.Descriptor.Key,
            _definition.Descriptor.Version,
            _definition.Descriptor.Category,
            effectiveCapabilities,
            _definition.PayloadSchema,
            _definition.Descriptor.ContainsSensitiveData);

        private async ValueTask InvokeListenerAsync(
            Func<CancellationToken, ValueTask> operation,
            HookOrdering ordering,
            CancellationToken cancellationToken)
        {
            try
            {
                await InvokeBoundedAsync(
                    async token =>
                    {
                        await operation(token);
                        return true;
                    },
                    ordering,
                    cancellationToken);
            }
            catch (KernelEventOperationTimeoutException exception) when (exception.OperationStillRunning)
            {
                throw new KernelActionExecutionException(
                    $"EVENT_OUTCOME_UNCERTAIN: {exception.Message}");
            }
            catch (KernelEventOperationCancellationException exception) when (exception.OperationStillRunning)
            {
                throw new KernelActionExecutionException(
                    $"EVENT_OUTCOME_UNCERTAIN: {exception.Message}");
            }
            catch (Exception) when (ordering.FailurePolicy == HookFailurePolicy.BestEffort)
            {
            }
        }

        private async ValueTask<T> InvokeBoundedAsync<T>(
            Func<CancellationToken, ValueTask<T>> operation,
            HookOrdering ordering,
            CancellationToken cancellationToken)
        {
            var timeout = ordering.Timeout ?? TimeSpan.FromSeconds(30);
            if (timeout <= TimeSpan.Zero)
                throw new TimeoutException("The event hook timeout is not positive.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            var operationTask = Task.Run(
                () => operation(linked.Token).AsTask(),
                CancellationToken.None);
            try
            {
                return await operationTask.WaitAsync(timeout, cancellationToken);
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
                        throw new KernelEventOperationTimeoutException(
                            "The event hook exceeded its timeout.",
                            false);
                    }
                }
                throw new KernelEventOperationTimeoutException(
                    "The event hook exceeded its timeout.",
                    true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new KernelEventOperationTimeoutException(
                    "The event hook exceeded its timeout.",
                    false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                linked.Cancel();
                if (await ObserveCompletionAsync(operationTask))
                    return await operationTask;
                throw new KernelEventOperationCancellationException(
                    "Caller cancellation occurred while the event hook was still running.",
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

        private IEventInterception<TEvent> Validate(
            IEventInterception<TEvent>? outcome,
            object authority) => outcome switch
            {
                KernelEventInterception<TEvent> trusted when ReferenceEquals(trusted.Authority, authority) => trusted,
                null => Failed("EVENT_NULL_OUTCOME", "An event interceptor returned no outcome."),
                _ => Failed("EVENT_FORGED_OUTCOME", "The event interceptor returned an outcome that Core did not issue.")
            };

        private static IEventInterception<TEvent> Convert(
            KernelUntypedEventInterception interception,
            TEvent original,
            object authority)
        {
            var payload = interception.Payload is { } value
                ? KernelJson.Deserialize<TEvent>(value)
                : original;
            return KernelEventInterception<TEvent>.FromAuthority(
                interception.Kind,
                payload,
                interception.Error,
                authority);
        }

        private static KernelEventInterception<TEvent> Failed(
            string code,
            string message,
            object? authority = null) =>
            KernelEventInterception<TEvent>.Failed(code, message, authority);

        private sealed class TypedEventControl(
            KernelEventInvocation<TEvent> owner,
            TEvent payload,
            int index,
            CancellationToken cancellationToken,
            IEventFrame<TEvent> frame) : IEventControl<TEvent>
        {
            private readonly object _authority = new();
            private bool _used;
            private bool _continuationStarted;
            private Task<IEventInterception<TEvent>>? _continuationTask;
            private IEventInterception<TEvent>? _continuationOutcome;
            private IEventInterception<TEvent>? _issuedOutcome;

            public object Authority => _authority;

            public bool ContinuationStarted => _continuationStarted;

            public bool TryGetKnownOutcome(out IEventInterception<TEvent>? outcome)
            {
                outcome = _issuedOutcome ?? _continuationOutcome;
                return outcome is not null;
            }

            public void ConsumeForUncertainty() => _used = true;

            public IEventInterception<TEvent> Continue()
            {
                Ensure(EventInterceptionCapabilities.Inspect);
                var result = InvokeContinuation(payload);
                return _issuedOutcome = Reissue(result);
            }

            public IEventInterception<TEvent> Replace(TEvent replacement, string reason)
            {
                Ensure(EventInterceptionCapabilities.Replace);
                var result = InvokeContinuation(replacement);
                return _issuedOutcome = Reissue(result);
            }

            private IEventInterception<TEvent> InvokeContinuation(TEvent nextPayload)
            {
                _continuationStarted = true;
                _continuationTask = owner.InvokeAsync(nextPayload, index + 1, cancellationToken).AsTask();
                try
                {
                    _continuationOutcome = _continuationTask.GetAwaiter().GetResult();
                    return _continuationOutcome;
                }
                catch
                {
                    throw;
                }
            }

            public IEventInterception<TEvent> Cancel(string code, string message)
            {
                Ensure(EventInterceptionCapabilities.Cancel);
                _used = true;
                return _issuedOutcome = KernelEventInterception<TEvent>.Cancelled(code, message, _authority);
            }

            public IEventInterception<TEvent> StopPropagation()
            {
                Ensure(EventInterceptionCapabilities.StopPropagation);
                _used = true;
                return _issuedOutcome = KernelEventInterception<TEvent>.Stopped(payload, _authority);
            }

            public IEventInterception<TEvent> Reissue(IEventInterception<TEvent> result) =>
                result is KernelEventInterception<TEvent> trusted
                    ? KernelEventInterception<TEvent>.FromAuthority(
                        trusted.Kind,
                        trusted.Payload,
                        trusted.Error,
                        _authority)
                    : KernelEventInterception<TEvent>.Failed(
                        "EVENT_FORGED_OUTCOME",
                        "The nested event path returned an outcome that Core did not issue.",
                        _authority);

            private void Ensure(EventInterceptionCapabilities capability)
            {
                if (_used)
                    throw new KernelControlException(
                        $"Event control for '{owner._definition.Descriptor.Key.Value}' was already consumed.");
                if (!frame.EffectiveCapabilities.HasFlag(capability))
                {
                    throw new KernelCapabilityException(
                        $"Module '{frame.OwnerModuleId}' does not have effective capability '{capability}' " +
                        $"for event '{owner._definition.Descriptor.Key.Value}'.");
                }
                _used = true;
            }
        }

        private sealed class UntypedEventControl(TypedEventControl typed) : IUntypedEventControl
        {
            public IUntypedEventInterception Continue() => Convert(typed.Continue());

            public IUntypedEventInterception Replace(JsonElement payload, string reason) =>
                Convert(typed.Replace(KernelJson.Deserialize<TEvent>(payload), reason));

            public IUntypedEventInterception Cancel(string code, string message) => Convert(typed.Cancel(code, message));

            public IUntypedEventInterception StopPropagation() => Convert(typed.StopPropagation());

            private static IUntypedEventInterception Convert(IEventInterception<TEvent> result) =>
                new KernelUntypedEventInterception(
                    result.Kind,
                    result.Payload is null ? null : KernelJson.Serialize(result.Payload),
                    result.Error,
                    ((KernelEventInterception<TEvent>)result).Authority);
        }
    }
}

public sealed class KernelEventInterception<TEvent> : IEventInterception<TEvent>
{
    private KernelEventInterception(
        EventInterceptionKind kind,
        TEvent payload,
        ExecutionError? error,
        object? authority)
    {
        Kind = kind;
        Payload = payload;
        Error = error;
        Authority = authority;
    }

    internal object? Authority { get; }
    public EventInterceptionKind Kind { get; }
    public TEvent Payload { get; }
    public ExecutionError? Error { get; }

    public static KernelEventInterception<TEvent> Continued(TEvent payload) =>
        new(EventInterceptionKind.Continued, payload, null, null);

    internal static KernelEventInterception<TEvent> FromAuthority(
        EventInterceptionKind kind,
        TEvent payload,
        ExecutionError? error,
        object authority) => new(kind, payload, error, authority);

    internal static KernelEventInterception<TEvent> Cancelled(string code, string message, object authority) =>
        new(EventInterceptionKind.Cancelled, default!, new ExecutionError(code, message), authority);

    internal static KernelEventInterception<TEvent> Stopped(TEvent payload, object authority) =>
        new(EventInterceptionKind.PropagationStopped, payload, null, authority);

    internal static KernelEventInterception<TEvent> Failed(
        string code,
        string message,
        object? authority = null) =>
        new(EventInterceptionKind.Failed, default!, new ExecutionError(code, message), authority);
}

public sealed class KernelUntypedEventInterception : IUntypedEventInterception
{
    public KernelUntypedEventInterception(
        EventInterceptionKind kind,
        JsonElement? payload,
        ExecutionError? error)
        : this(kind, payload, error, null)
    {
    }

    internal KernelUntypedEventInterception(
        EventInterceptionKind kind,
        JsonElement? payload,
        ExecutionError? error,
        object? authority)
    {
        Kind = kind;
        Payload = payload;
        Error = error;
        Authority = authority;
    }

    internal object? Authority { get; }
    public EventInterceptionKind Kind { get; }
    public JsonElement? Payload { get; }
    public ExecutionError? Error { get; }
}

internal sealed class KernelEventOperationTimeoutException(
    string message,
    bool operationStillRunning) : TimeoutException(message)
{
    public bool OperationStillRunning { get; } = operationStillRunning;
}

internal sealed class KernelEventOperationCancellationException(
    string message,
    bool operationStillRunning) : OperationCanceledException(message)
{
    public bool OperationStillRunning { get; } = operationStillRunning;
}

public sealed class InMemoryEventDeliverySink : IKernelEventDeliverySink
{
    private readonly ConcurrentQueue<KernelQueuedEvent> _events = new();
    private readonly int _capacity;
    private readonly bool _durable;
    private int _count;

    public InMemoryEventDeliverySink(int capacity = 1024, bool supportsDurable = false)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _durable = supportsDurable;
    }

    public bool SupportsDurable => _durable;

    public ValueTask EnqueueAsync(
        SharpClawEventKey eventKey,
        object envelope,
        EventDelivery delivery,
        CancellationToken cancellationToken) =>
        EnqueueAsync(eventKey, envelope, delivery, cancellationToken, "unknown");

    public ValueTask EnqueueAsync(
        SharpClawEventKey eventKey,
        object envelope,
        EventDelivery delivery,
        CancellationToken cancellationToken,
        string targetListenerId)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delivery == EventDelivery.Durable && !_durable)
            throw new KernelActionExecutionException("A durable event requires a durable delivery sink.");
        if (Interlocked.Increment(ref _count) > _capacity)
        {
            Interlocked.Decrement(ref _count);
            throw new KernelActionExecutionException(
                $"EVENT_BACKPRESSURE: the event sink capacity {_capacity} is full.");
        }

        _events.Enqueue(new KernelQueuedEvent(
            eventKey,
            envelope,
            delivery,
            DateTimeOffset.UtcNow,
            targetListenerId));
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<KernelQueuedEvent> Drain()
    {
        var result = new List<KernelQueuedEvent>();
        while (_events.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _count);
            result.Add(item);
        }

        return result;
    }
}
