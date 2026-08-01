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
        var definition = _graph.GetEvent(descriptor.Key);
        if (definition is not CompiledEventDefinition<TEvent> typed)
            throw new KernelActionExecutionException(
                $"Event '{descriptor.Key.Value}' was compiled for '{definition.EventType.FullName}'.");
        if (typed.Descriptor.Version != descriptor.Version)
            throw new KernelActionExecutionException(
                $"Event '{descriptor.Key.Value}' was invoked with version {descriptor.Version}, " +
                $"but the graph contains version {typed.Descriptor.Version}.");

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
            var control = new TypedEventControl(this, payload, index, cancellationToken);
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
                        _definition.EffectiveCapabilities,
                        new JsonSchemaReference("core.event.payload", 1, string.Empty),
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
                return Failed("EVENT_CONTROL_CONSUMED", exception.Message);
            }
            catch (TimeoutException) when (frame.Ordering.FailurePolicy == HookFailurePolicy.BestEffort)
            {
                return await InvokeAsync(payload, index + 1, cancellationToken);
            }
            catch (Exception) when (frame.Ordering.FailurePolicy == HookFailurePolicy.BestEffort)
            {
                return await InvokeAsync(payload, index + 1, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failed("EVENT_CANCELLED", "The event was cancelled.");
            }
            catch (TimeoutException exception)
            {
                return Failed("EVENT_HOOK_TIMEOUT", exception.Message);
            }
            catch (Exception exception)
            {
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
                                    CreateUntypedDescriptor(),
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
                            CreateUntypedDescriptor(),
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

        private UntypedEventDescriptor CreateUntypedDescriptor() => new(
            _definition.Descriptor.Key,
            _definition.Descriptor.Version,
            _definition.Descriptor.Category,
            _definition.EffectiveCapabilities,
            new JsonSchemaReference("core.event.payload", 1, string.Empty),
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
            try
            {
                return await operation(linked.Token).AsTask().WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("The event hook exceeded its timeout.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The event hook exceeded its timeout.");
            }
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

        private static KernelEventInterception<TEvent> Failed(string code, string message) =>
            KernelEventInterception<TEvent>.Failed(code, message);

        private sealed class TypedEventControl(
            KernelEventInvocation<TEvent> owner,
            TEvent payload,
            int index,
            CancellationToken cancellationToken) : IEventControl<TEvent>
        {
            private readonly object _authority = new();
            private bool _used;

            public object Authority => _authority;

            public IEventInterception<TEvent> Continue()
            {
                Ensure(EventInterceptionCapabilities.Inspect);
                var result = owner.InvokeAsync(payload, index + 1, cancellationToken).GetAwaiter().GetResult();
                return Reissue(result);
            }

            public IEventInterception<TEvent> Replace(TEvent replacement, string reason)
            {
                Ensure(EventInterceptionCapabilities.Replace);
                var result = owner.InvokeAsync(replacement, index + 1, cancellationToken).GetAwaiter().GetResult();
                return Reissue(result);
            }

            public IEventInterception<TEvent> Cancel(string code, string message)
            {
                Ensure(EventInterceptionCapabilities.Cancel);
                _used = true;
                return KernelEventInterception<TEvent>.Cancelled(code, message, _authority);
            }

            public IEventInterception<TEvent> StopPropagation()
            {
                Ensure(EventInterceptionCapabilities.StopPropagation);
                _used = true;
                return KernelEventInterception<TEvent>.Stopped(payload, _authority);
            }

            private IEventInterception<TEvent> Reissue(IEventInterception<TEvent> result) =>
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
                if (!owner._definition.EffectiveCapabilities.HasFlag(capability) ||
                    !owner._snapshot.EventGrants!.Any(grant =>
                        grant.EventKey == owner._definition.Descriptor.Key &&
                        grant.Capabilities.HasFlag(capability)))
                {
                    throw new KernelCapabilityException(
                        $"Event '{owner._definition.Descriptor.Key.Value}' does not have effective capability '{capability}'.");
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
