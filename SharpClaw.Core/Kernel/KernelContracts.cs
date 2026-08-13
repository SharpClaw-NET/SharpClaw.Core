using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Kernel;

public sealed record KernelActionEnvelope(SharpClawActionKey Key, object? Payload);

public sealed record KernelActionLifecycleEvent(
    Guid InvocationId,
    Guid? ParentInvocationId,
    Guid TraceId,
    SharpClawActionKey ActionKey,
    int ActionVersion,
    ActionOutcomeKind? OutcomeKind,
    ExecutionError? Error,
    ContinuationToken? Continuation,
    ActionUncertainty? Uncertainty,
    DateTimeOffset Timestamp);

public static class KernelActionLifecycleEvents
{
    private static readonly IReadOnlyDictionary<string, EventDescriptor<KernelActionLifecycleEvent>>
        DescriptorMap = new ReadOnlyDictionary<string, EventDescriptor<KernelActionLifecycleEvent>>(
            new Dictionary<string, EventDescriptor<KernelActionLifecycleEvent>>(StringComparer.Ordinal)
            {
                [SharpClawEvents.ActionStarting.Value] = Create(SharpClawEvents.ActionStarting),
                [SharpClawEvents.ActionCompleted.Value] = Create(SharpClawEvents.ActionCompleted),
                [SharpClawEvents.ActionDeferred.Value] = Create(SharpClawEvents.ActionDeferred),
                [SharpClawEvents.ActionFailed.Value] = Create(SharpClawEvents.ActionFailed),
                [SharpClawEvents.ActionCancelled.Value] = Create(SharpClawEvents.ActionCancelled)
            });

    public static IReadOnlyList<EventDescriptor<KernelActionLifecycleEvent>> Descriptors { get; } =
        DescriptorMap.Values.OrderBy(value => value.Key.Value, StringComparer.Ordinal).ToArray();

    public static EventDescriptor<KernelActionLifecycleEvent> DescriptorFor(SharpClawEventKey key) =>
        DescriptorMap.TryGetValue(key.Value, out var descriptor)
            ? descriptor
            : throw new KernelActionExecutionException(
                $"Action lifecycle event '{key.Value}' is not registered.");

    public static EventDescriptor<KernelActionLifecycleEvent> ForOutcome(ActionOutcomeKind kind) =>
        DescriptorFor(kind switch
        {
            ActionOutcomeKind.Completed => SharpClawEvents.ActionCompleted,
            ActionOutcomeKind.Cancelled => SharpClawEvents.ActionCancelled,
            ActionOutcomeKind.Deferred => SharpClawEvents.ActionDeferred,
            ActionOutcomeKind.Failed or ActionOutcomeKind.Uncertain => SharpClawEvents.ActionFailed,
            _ => SharpClawEvents.ActionFailed
        });

    private static EventDescriptor<KernelActionLifecycleEvent> Create(SharpClawEventKey key) =>
        new(
            key,
            1,
            "action.lifecycle",
            EventInterceptionCapabilities.Inspect |
            EventInterceptionCapabilities.Observe,
            false,
            false)
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            DeliveryClasses = [EventDelivery.Inline, EventDelivery.Queued, EventDelivery.Durable]
        };
}

public sealed record KernelCoverageEntry(string Id, string Boundary, SharpClawActionKey ActionKey);

public static class KernelActionCatalog
{
    public static IReadOnlyList<KernelCoverageEntry> Coverage { get; } =
        new ReadOnlyCollection<KernelCoverageEntry>(
        [
            new("K01", "runtime lifecycle", new("runtime.start.prepare")),
            new("K02", "request ingress", new("runtime.request.receive")),
            new("K03", "security boundary", new("security.session.validate")),
            new("K04", "command ingress", new("runtime.cli.parse")),
            new("K05", "client interaction", new("client.command.dispatch")),
            new("K06", "chat turn", SharpClawActions.Chat.Turn),
            new("K07", "provider round", SharpClawActions.Chat.ProviderRound),
            new("K08", "tool invocation", SharpClawActions.Tools.Invoke),
            new("K09", "storage operation", new("storage.get")),
            new("K10", "transaction commit", new("storage.transaction.commit")),
            new("K11", "module lifecycle", new("module.start")),
            new("K12", "event delivery", new("event.deliver")),
            new("K13", "background execution", new("background.tick.execute")),
            new("K14", "gateway boundary", new("gateway.request.receive"))
        ]);

    public static IReadOnlyList<SharpClawActionKey> RequiredKeys { get; } =
        new ReadOnlyCollection<SharpClawActionKey>(
        [
            .. SharpClawActionCatalog.All
        ]);

    public static IReadOnlyList<KernelStandardActionManifestEntry> Descriptors =>
        KernelStandardActionManifest.Entries;

    public static KernelStandardActionManifestEntry DescriptorFor(SharpClawActionKey key) =>
        KernelStandardActionManifest.Get(key);

    public static string CategoryFor(SharpClawActionKey key) =>
        key.Value switch
        {
            var value when value.StartsWith("chat.", StringComparison.Ordinal) => "chat",
            var value when value.StartsWith("tool.", StringComparison.Ordinal) => "tool",
            var value when value.StartsWith("runtime.", StringComparison.Ordinal) => "runtime",
            var value when value.StartsWith("request.", StringComparison.Ordinal) => "request",
            var value when value.StartsWith("security.", StringComparison.Ordinal) => "security",
            var value when value.StartsWith("command.", StringComparison.Ordinal) => "command",
            var value when value.StartsWith("client.", StringComparison.Ordinal) => "client",
            var value when value.StartsWith("storage.", StringComparison.Ordinal) => "storage",
            var value when value.StartsWith("transaction.", StringComparison.Ordinal) => "transaction",
            var value when value.StartsWith("module.", StringComparison.Ordinal) => "module",
            var value when value.StartsWith("event.", StringComparison.Ordinal) => "event",
            var value when value.StartsWith("background.", StringComparison.Ordinal) => "background",
            var value when value.StartsWith("gateway.", StringComparison.Ordinal) => "gateway",
            var value when value.StartsWith("provider.", StringComparison.Ordinal) => "provider",
            var value when value.StartsWith("conversation.", StringComparison.Ordinal) => "conversation",
            var value when value.StartsWith("continuation.", StringComparison.Ordinal) => "continuation",
            var value when value.StartsWith("action_recovery.", StringComparison.Ordinal) => "action_recovery",
            var value when value.StartsWith("jobs.", StringComparison.Ordinal) => "jobs",
            _ => "kernel"
        };

    public static ActionInterceptionCapabilities StandardCapabilities(SharpClawActionKey key) =>
        DescriptorFor(key).Capabilities;

    public static bool StandardSensitive(SharpClawActionKey key) =>
        key.Value.StartsWith("security.", StringComparison.Ordinal) ||
        key.Value.StartsWith("provider.", StringComparison.Ordinal) ||
        key.Value.StartsWith("storage.", StringComparison.Ordinal) ||
        key.Value.StartsWith("runtime.request.", StringComparison.Ordinal) ||
        key.Value.StartsWith("gateway.request.", StringComparison.Ordinal) ||
        key.Value.StartsWith("jobs.", StringComparison.Ordinal);
}

public sealed class KernelGraphCompileOptions
{
    public ActionInterceptionCapabilities SupportedActionCapabilities { get; init; } =
        ActionInterceptionCapabilities.Inspect |
        ActionInterceptionCapabilities.ReplaceInput |
        ActionInterceptionCapabilities.Cancel |
        ActionInterceptionCapabilities.ReplaceResult |
        ActionInterceptionCapabilities.Defer |
        ActionInterceptionCapabilities.Repeat |
        ActionInterceptionCapabilities.Wrap |
        ActionInterceptionCapabilities.Observe |
        ActionInterceptionCapabilities.PublishEvents;

    public EventInterceptionCapabilities SupportedEventCapabilities { get; init; } =
        EventInterceptionCapabilities.Inspect |
        EventInterceptionCapabilities.Replace |
        EventInterceptionCapabilities.Cancel |
        EventInterceptionCapabilities.StopPropagation |
        EventInterceptionCapabilities.Observe;

    /// <summary>Administrator grants keyed by action key. A missing key uses the descriptor grant.</summary>
    public IReadOnlyDictionary<string, ActionInterceptionCapabilities>? ActionCapabilityGrants { get; init; }

    /// <summary>Module manifest grants keyed by module id and action key.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>?
        ActionModuleCapabilityGrants
    { get; init; }

    /// <summary>Administrator grants keyed by event key. A missing key uses the descriptor grant.</summary>
    public IReadOnlyDictionary<string, EventInterceptionCapabilities>? EventCapabilityGrants { get; init; }

    /// <summary>Module manifest grants keyed by module id and event key.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, EventInterceptionCapabilities>>?
        EventModuleCapabilityGrants
    { get; init; }

    /// <summary>Exact sensitive action approvals bound to module, version, and schema identity.</summary>
    public IReadOnlyList<KernelSensitiveActionApproval> SensitiveActionApprovals { get; init; } = [];

    /// <summary>Exact sensitive event approvals bound to module, version, and schema identity.</summary>
    public IReadOnlyList<KernelSensitiveEventApproval> SensitiveEventApprovals { get; init; } = [];

    public int MaximumActionDepth { get; init; } = 32;
}

public sealed record KernelSensitiveActionApproval(
    string ModuleId,
    SharpClawActionKey ActionKey,
    int ActionVersion,
    string ActionType,
    string ResultType,
    string SchemaIdentity);

public sealed record KernelSensitiveEventApproval(
    string ModuleId,
    SharpClawEventKey EventKey,
    int EventVersion,
    string EventType,
    string SchemaIdentity);

public sealed record KernelContinuationRequest(
    Guid InvocationId,
    SharpClawActionKey ActionKey,
    int ActionVersion,
    Guid IdempotencyKey,
    ActionDeferRequest Defer,
    ActionContinuationPolicy Policy,
    string ContractHash,
    ContinuationDestination? Destination = null,
    string? ProtectedInput = null,
    ActionRepeatPolicy? RepeatPolicy = null);

public sealed record KernelContinuationExecutionUpdate(
    ContinuationExecutionStage Stage,
    ActionOutcomeCertainty Certainty,
    string? ReceiptReference = null,
    string? PersistedOutcome = null);

public sealed record KernelContinuationReceiptRequest(
    Guid TokenId,
    ActionRecoveryReference RecoveryReference,
    SharpClawActionKey ActionKey,
    int ActionVersion,
    Guid IdempotencyKey,
    string ContractHash,
    ContinuationExecutionStage Stage,
    string? ReceiptReference);

public sealed record KernelContinuationReceipt(
    Guid TokenId,
    Guid RecoveryId,
    SharpClawActionKey ActionKey,
    int ActionVersion,
    Guid IdempotencyKey,
    string ContractHash,
    string ReceiptReference,
    string Outcome,
    DateTimeOffset ObservedAt);

public interface IKernelContinuationReceiptResolver
{
    ValueTask<KernelContinuationReceipt?> FindAsync(
        KernelContinuationReceiptRequest request,
        CancellationToken cancellationToken);
}

public sealed record KernelUncertaintyRequest(
    Guid InvocationId,
    SharpClawActionKey ActionKey,
    int ActionVersion,
    Guid IdempotencyKey,
    ActionExecutionStage Stage,
    string Code,
    string Message,
    string? ReceiptReference,
    string ContractHash,
    ContinuationDestination? ResultDestination = null,
    string? ProtectedInput = null,
    ActionContinuationPolicy? Policy = null);

public sealed record KernelRecoveryToken(Guid RecoveryId, string Secret);

public sealed record KernelRecoveryRecord(
    ActionUncertainty Uncertainty,
    KernelRecoveryToken Token,
    KernelRecoveryState State);

/// <summary>One continuation claim bound to the compiled graph snapshot.</summary>
public sealed record KernelContinuationClaim(
    ContinuationClaim Claim,
    string ContractHash)
{
    public string Owner => Claim.Owner;

    public DateTimeOffset LeaseExpiresAt => Claim.LeaseExpiresAt;

    public int Generation => Claim.Generation;

    public long ExpectedRevision => Claim.ExpectedRevision;
}

public interface IActionContinuationHost
{
    /// <summary>True only when the host persists continuation authority beyond this process.</summary>
    bool SupportsDurableState { get; }

    ValueTask<ContinuationToken> CreateAsync(KernelContinuationRequest request, CancellationToken cancellationToken);

    ValueTask<KernelContinuationState?> GetAsync(
        Guid tokenId,
        CancellationToken cancellationToken);

    ValueTask<KernelContinuationState?> ClaimAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelContinuationState?> RenewLeaseAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelContinuationState?> ResumeAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelContinuationState?> SetExecutionStateAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        KernelContinuationExecutionUpdate update,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelContinuationState?> CompleteAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        string outcome,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelContinuationState?> CancelAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelContinuationState?> ClaimContinuationDeliveryAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelContinuationState?> BeginDeliveryAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelContinuationState?> AcknowledgeAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelContinuationState?> ExpireAsync(
        Guid tokenId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelContinuationState?> ClaimContinuationRecoveryAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelContinuationState?> RecoverContinuationAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<bool> DeleteAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelRecoveryRecord> RecordUncertaintyAsync(
        KernelUncertaintyRequest request,
        CancellationToken cancellationToken);

    ValueTask<KernelRecoveryState?> GetRecoveryAsync(
        Guid recoveryId,
        CancellationToken cancellationToken);

    ValueTask<KernelRecoveryState?> ClaimRecoveryAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelRecoveryState?> RenewRecoveryLeaseAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelRecoveryState?> ResumeRecoveryAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelRecoveryState?> CompleteRecoveryAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        string outcome,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelRecoveryState?> CancelRecoveryAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelRecoveryState?> DeliverRecoveryAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelRecoveryState?> AcknowledgeRecoveryAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<KernelRecoveryState?> ExpireRecoveryAsync(
        Guid recoveryId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<bool> DeleteRecoveryAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public sealed record KernelContinuationState(
    Guid TokenId,
    string TokenHash,
    KernelContinuationRequest Request,
    ContinuationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? CompletedAt,
    string? ClaimOwner = null,
    DateTimeOffset? LeaseExpiresAt = null,
    int Generation = 0,
    long Revision = 0,
    ContinuationDestination? ResultDestination = null,
    string? ProtectedInput = null,
    string? CompletedOutcome = null,
    ContinuationExecutionStage ExecutionStage = ContinuationExecutionStage.BeforeTerminal,
    ActionOutcomeCertainty OutcomeCertainty = ActionOutcomeCertainty.Certain,
    ActionRecoveryReference? RecoveryReference = null,
    DateTimeOffset RetainUntil = default,
    DateTimeOffset? DeliveryAcknowledgedAt = null,
    string? ReceiptReference = null,
    DateTimeOffset? CancellationRequestedAt = null);

public sealed record KernelRecoveryState(
    ActionRecoveryReference Reference,
    KernelUncertaintyRequest Request,
    ActionUncertainty Uncertainty,
    ContinuationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string TokenHash,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? CompletedAt,
    string? ClaimOwner,
    DateTimeOffset? LeaseExpiresAt,
    int Generation,
    long Revision,
    string? ProtectedInput,
    ContinuationDestination ResultDestination,
    string? CompletedOutcome = null);

public interface IActionContinuationStore
{
    /// <summary>True only when state survives process loss and uses durable storage.</summary>
    bool IsDurable { get; }

    ValueTask<KernelContinuationState?> ReadAsync(
        Guid tokenId,
        CancellationToken cancellationToken);

    ValueTask<bool> TryCreateAsync(
        KernelContinuationState state,
        CancellationToken cancellationToken);

    ValueTask<bool> TryUpdateAsync(
        Guid tokenId,
        KernelContinuationState expected,
        KernelContinuationState replacement,
        CancellationToken cancellationToken);

    ValueTask<bool> TryDeleteAsync(
        Guid tokenId,
        KernelContinuationState expected,
        CancellationToken cancellationToken);

    ValueTask<KernelRecoveryState?> ReadRecoveryAsync(
        Guid recoveryId,
        CancellationToken cancellationToken);

    ValueTask<bool> TryCreateRecoveryAsync(
        KernelRecoveryState state,
        CancellationToken cancellationToken);

    ValueTask<bool> TryUpdateRecoveryAsync(
        Guid recoveryId,
        KernelRecoveryState expected,
        KernelRecoveryState replacement,
        CancellationToken cancellationToken);

    ValueTask<bool> TryDeleteRecoveryAsync(
        Guid recoveryId,
        KernelRecoveryState expected,
        CancellationToken cancellationToken);
}

public interface IKernelProviderTransport
{
    ValueTask<ChatCompletionResult> CompleteAsync(
        ProviderTurnRequest request,
        IReadOnlyList<ToolAwareMessage> messages,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ChatStreamChunk> StreamAsync(
        ProviderTurnRequest request,
        IReadOnlyList<ToolAwareMessage> messages,
        CancellationToken cancellationToken);
}

public interface IKernelEventDeliverySink
{
    /// <summary>True only when the sink persists durable delivery.</summary>
    bool SupportsDurable { get; }

    ValueTask EnqueueAsync(
        SharpClawEventKey eventKey,
        object envelope,
        EventDelivery delivery,
        CancellationToken cancellationToken);

    ValueTask EnqueueAsync(
        SharpClawEventKey eventKey,
        object envelope,
        EventDelivery delivery,
        CancellationToken cancellationToken,
        string targetListenerId) =>
        EnqueueAsync(eventKey, envelope, delivery, cancellationToken);
}

public sealed record KernelQueuedEvent(
    SharpClawEventKey EventKey,
    object Envelope,
    EventDelivery Delivery,
    DateTimeOffset EnqueuedAt,
    string TargetListenerId);

public sealed record KernelToolRegistration(
    ToolDescriptor Descriptor,
    string OwnerModuleId,
    Type HandlerType);

public sealed class KernelGraphCompilationException(string message) : InvalidOperationException(message);

public sealed class KernelActionExecutionException(string message) : InvalidOperationException(message);

public sealed class KernelCapabilityException(string message) : InvalidOperationException(message);

public sealed class KernelActionCancelledException : OperationCanceledException
{
    public KernelActionCancelledException(ExecutionError error)
        : base(error.Message)
    {
        Error = error;
    }

    public ExecutionError Error { get; }
}

public sealed class KernelActionDeferredException : Exception
{
    public KernelActionDeferredException(ContinuationToken continuation)
        : base("The action was deferred to a durable continuation.")
    {
        Continuation = continuation;
    }

    public ContinuationToken Continuation { get; }
}

public sealed class KernelActionFailedException : Exception
{
    public KernelActionFailedException(ExecutionError error)
        : base(error.Message)
    {
        Error = error;
    }

    public ExecutionError Error { get; }
}

internal static class KernelServiceResolution
{
    public static object Resolve(Type serviceType, IServiceProvider? serviceProvider)
    {
        var service = serviceProvider?.GetService(serviceType);
        if (service is not null)
            return service;

        try
        {
            return serviceProvider is null
                ? (Activator.CreateInstance(serviceType)
                   ?? throw new KernelGraphCompilationException($"Cannot create service '{serviceType.FullName}'."))
                : ActivatorUtilities.CreateInstance(serviceProvider, serviceType);
        }
        catch (Exception exception) when (exception is MissingMethodException or MemberAccessException)
        {
            throw new KernelGraphCompilationException(
                $"Service '{serviceType.FullName}' requires a host service provider.");
        }
    }

    public static T Resolve<T>(IServiceProvider? serviceProvider) where T : notnull =>
        (T)Resolve(typeof(T), serviceProvider);
}

internal static class KernelJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    public static JsonElement Serialize(object? value) =>
        JsonSerializer.SerializeToElement(value, Options);

    public static T Deserialize<T>(JsonElement value) =>
        typeof(T) == typeof(object)
            ? DeserializeObjectValue<T>(value)
            : value.Deserialize<T>(Options)
              ?? throw new KernelActionExecutionException(
                  $"The action input cannot deserialize as '{typeof(T).FullName}'.");

    public static object? Deserialize(JsonElement value, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type == typeof(object))
            return DeserializeObject(value);
        return value.Deserialize(type, Options)
               ?? throw new KernelActionExecutionException(
                   $"The action input cannot deserialize as '{type.FullName}'.");
    }

    private static T DeserializeObjectValue<T>(JsonElement value)
    {
        var result = DeserializeObject(value);
        return result is null ? default! : (T)result;
    }

    private static object? DeserializeObject(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue,
        _ => value.Clone()
    };

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General);
        options.Converters.Add(new ReadOnlySetJsonConverterFactory());
        return options;
    }
}

internal sealed class ReadOnlySetJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var itemType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)(Activator.CreateInstance(
            typeof(ReadOnlySetJsonConverter<>).MakeGenericType(itemType))
            ?? throw new KernelActionExecutionException(
                $"The read-only set codec for '{itemType.FullName}' cannot be created."));
    }
}

internal sealed class ReadOnlySetJsonConverter<T> : JsonConverter<IReadOnlySet<T>>
    where T : notnull
{
    public override IReadOnlySet<T> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        new HashSet<T>(JsonSerializer.Deserialize<T[]>(ref reader, options) ?? []);

    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlySet<T> value,
        JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToArray(), options);
}
