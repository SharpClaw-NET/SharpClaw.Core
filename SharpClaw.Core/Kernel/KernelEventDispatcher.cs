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
        {
            throw new KernelActionExecutionException(
                $"Event '{descriptor.Key.Value}' was compiled for '{definition.EventType.FullName}'.");
        }
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
                $"Event '{descriptor.Key.Value}' was not delivered. {result.Error?.Message ?? result.Kind.ToString()}.");
    }

    private sealed class KernelEventInvocation<TEvent>
    {
        private readonly CompiledEventDefinition<TEvent> _definition;
        private readonly ActionPipelineSnapshot _snapshot;
        private readonly RequestPrincipal _caller;
        private readonly ExtensionFeatureSet _features;
        private readonly IKernelEventDeliverySink _deliverySink;
        private readonly CancellationToken _rootCancellationToken;
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
            _rootCancellationToken = rootCancellationToken;
        }

        public async ValueTask<IEventInterception<TEvent>> InvokeAsync(
            TEvent payload,
            int index,
            CancellationToken cancellationToken)
        {
            if (index >= _definition.Interceptors.Count)
                return new KernelEventInterception<TEvent>(EventInterceptionKind.Continued, payload, null);

            var envelope = new EventEnvelope<TEvent>(
                Guid.NewGuid(),
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
            try
            {
                if (frame is TypedEventFrame<TEvent> typed)
                {
                    var control = new TypedEventControl(this, payload, index, cancellationToken);
                    return await typed.Interceptor.InterceptAsync(context, control, cancellationToken);
                }

                if (frame is AnyEventFrame<TEvent> any)
                {
                    var descriptor = new UntypedEventDescriptor(
                        _definition.Descriptor.Key,
                        _definition.Descriptor.Version,
                        _definition.Descriptor.Category,
                        _definition.Descriptor.Capabilities,
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
                    var control = new UntypedEventControl(this, payload, index, cancellationToken);
                    return Convert(await any.Interceptor.InterceptAsync(
                        new UntypedEventContext(untypedEnvelope),
                        control,
                        cancellationToken),
                        payload);
                }

                throw new KernelActionExecutionException("The compiled event graph contains an unknown frame.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new KernelEventInterception<TEvent>(
                    EventInterceptionKind.Failed,
                    default!,
                    new ExecutionError(
                        "EVENT_CANCELLED",
                        "The event was cancelled.",
                        false,
                    new Dictionary<string, string>()));
            }
            catch (KernelCapabilityException exception)
            {
                return new KernelEventInterception<TEvent>(
                    EventInterceptionKind.Failed,
                    default!,
                    new ExecutionError(
                        "EVENT_CAPABILITY_DENIED",
                        exception.Message,
                        false,
                        new Dictionary<string, string>()));
            }
            catch (Exception exception)
            {
                return new KernelEventInterception<TEvent>(
                    EventInterceptionKind.Failed,
                    default!,
                    new ExecutionError(
                        "EVENT_INTERCEPTOR_FAILED",
                        exception.Message,
                        false,
                        new Dictionary<string, string>()));
            }
        }

        public async ValueTask DeliverListenersAsync(TEvent payload, CancellationToken cancellationToken)
        {
            var envelope = new EventEnvelope<TEvent>(
                Guid.NewGuid(),
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
                        await typed.OnEventAsync(envelope, cancellationToken);
                    else
                    {
                        var descriptor = new UntypedEventDescriptor(
                            _definition.Descriptor.Key,
                            _definition.Descriptor.Version,
                            _definition.Descriptor.Category,
                            _definition.Descriptor.Capabilities,
                            new JsonSchemaReference("core.event.payload", 1, string.Empty),
                            _definition.Descriptor.ContainsSensitiveData);
                        await ((IAnyEventListener)listener.Listener).OnEventAsync(
                            new UntypedEventEnvelope(
                                descriptor,
                                envelope.EventId,
                                envelope.ActionInvocationId,
                                envelope.TraceId,
                                envelope.Timestamp,
                                envelope.OwnerModuleId,
                                KernelJson.Serialize(payload)),
                            cancellationToken);
                    }
                }
                else
                {
                    await _deliverySink.EnqueueAsync(
                        _definition.Descriptor.Key,
                        listener.Listener is IEventListener<TEvent>
                            ? envelope
                            : new UntypedEventEnvelope(
                                new UntypedEventDescriptor(
                                    _definition.Descriptor.Key,
                                    _definition.Descriptor.Version,
                                    _definition.Descriptor.Category,
                                    _definition.Descriptor.Capabilities,
                                    new JsonSchemaReference("core.event.payload", 1, string.Empty),
                                    _definition.Descriptor.ContainsSensitiveData),
                                envelope.EventId,
                                envelope.ActionInvocationId,
                                envelope.TraceId,
                                envelope.Timestamp,
                                envelope.OwnerModuleId,
                                KernelJson.Serialize(payload)),
                        listener.Delivery,
                        cancellationToken);
                }
            }
        }

        private static IEventInterception<TEvent> Convert(
            IUntypedEventInterception interception,
            TEvent original)
        {
            var payload = interception.Payload is { } value
                ? KernelJson.Deserialize<TEvent>(value)
                : original;
            return new KernelEventInterception<TEvent>(interception.Kind, payload, interception.Error);
        }

        private sealed class TypedEventControl(
            KernelEventInvocation<TEvent> owner,
            TEvent payload,
            int index,
            CancellationToken cancellationToken) : IEventControl<TEvent>
        {
            private bool _used;

            public IEventInterception<TEvent> Continue()
            {
                Ensure(EventInterceptionCapabilities.Inspect);
                return owner.InvokeAsync(payload, index + 1, cancellationToken).GetAwaiter().GetResult();
            }

            public IEventInterception<TEvent> Replace(TEvent replacement, string reason)
            {
                Ensure(EventInterceptionCapabilities.Replace);
                return owner.InvokeAsync(replacement, index + 1, cancellationToken).GetAwaiter().GetResult();
            }

            public IEventInterception<TEvent> Cancel(string code, string message)
            {
                Ensure(EventInterceptionCapabilities.Cancel);
                _used = true;
                return new KernelEventInterception<TEvent>(
                    EventInterceptionKind.Cancelled,
                    default!,
                    new ExecutionError(code, message, false, new Dictionary<string, string>()));
            }

            public IEventInterception<TEvent> StopPropagation()
            {
                Ensure(EventInterceptionCapabilities.StopPropagation);
                _used = true;
                return new KernelEventInterception<TEvent>(
                    EventInterceptionKind.PropagationStopped,
                    payload,
                    null);
            }

            private void Ensure(EventInterceptionCapabilities capability)
            {
                if (!owner._definition.Descriptor.Capabilities.HasFlag(capability))
                    throw new KernelCapabilityException(
                        $"Event '{owner._definition.Descriptor.Key.Value}' does not declare '{capability}'.");
                if (_used)
                    throw new KernelActionExecutionException("Event control was already consumed.");
                _used = true;
            }
        }

        private sealed class UntypedEventControl(
            KernelEventInvocation<TEvent> owner,
            TEvent payload,
            int index,
            CancellationToken cancellationToken) : IUntypedEventControl
        {
            private readonly TypedEventControl _typed = new(owner, payload, index, cancellationToken);

            public IUntypedEventInterception Continue() => Convert(_typed.Continue());

            public IUntypedEventInterception Replace(JsonElement replacement, string reason) =>
                Convert(_typed.Replace(KernelJson.Deserialize<TEvent>(replacement), reason));

            public IUntypedEventInterception Cancel(string code, string message) =>
                Convert(_typed.Cancel(code, message));

            public IUntypedEventInterception StopPropagation() => Convert(_typed.StopPropagation());

            private static IUntypedEventInterception Convert(IEventInterception<TEvent> interception) =>
                new KernelUntypedEventInterception(
                    interception.Kind,
                    interception.Payload is null ? null : KernelJson.Serialize(interception.Payload),
                    interception.Error);
        }
    }
}

public sealed class KernelEventInterception<TEvent>(
    EventInterceptionKind kind,
    TEvent payload,
    ExecutionError? error) : IEventInterception<TEvent>
{
    public EventInterceptionKind Kind { get; } = kind;

    public TEvent Payload { get; } = payload;

    public ExecutionError? Error { get; } = error;
}

public sealed class KernelUntypedEventInterception(
    EventInterceptionKind kind,
    JsonElement? payload,
    ExecutionError? error) : IUntypedEventInterception
{
    public EventInterceptionKind Kind { get; } = kind;

    public JsonElement? Payload { get; } = payload;

    public ExecutionError? Error { get; } = error;
}

public sealed class InMemoryEventDeliverySink : IKernelEventDeliverySink
{
    private readonly ConcurrentQueue<KernelQueuedEvent> _events = new();

    public ValueTask EnqueueAsync(
        SharpClawEventKey eventKey,
        object envelope,
        EventDelivery delivery,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Enqueue(new KernelQueuedEvent(eventKey, envelope, delivery, DateTimeOffset.UtcNow));
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<KernelQueuedEvent> Drain()
    {
        var result = new List<KernelQueuedEvent>();
        while (_events.TryDequeue(out var item))
            result.Add(item);
        return result;
    }
}
