using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Kernel;

public sealed record KernelActionEnvelope(SharpClawActionKey Key, object? Payload);

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
            .. SharpClawActionCatalog.Kernel
        ]);

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
            _ => "kernel"
        };
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

    /// <summary>Administrator grants keyed by event key. A missing key uses the descriptor grant.</summary>
    public IReadOnlyDictionary<string, EventInterceptionCapabilities>? EventCapabilityGrants { get; init; }

    /// <summary>Sensitive action keys approved by the host.</summary>
    public IReadOnlySet<string> ApprovedSensitiveActions { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Sensitive event keys approved by the host.</summary>
    public IReadOnlySet<string> ApprovedSensitiveEvents { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public int MaximumActionDepth { get; init; } = 32;
}

public sealed record KernelContinuationRequest(
    Guid InvocationId,
    SharpClawActionKey ActionKey,
    int ActionVersion,
    Guid IdempotencyKey,
    ActionDeferRequest Defer,
    ActionContinuationPolicy Policy,
    string ContractHash,
    ContinuationDestination? Destination = null,
    string? ProtectedInput = null);

public sealed record KernelUncertaintyRequest(
    Guid InvocationId,
    SharpClawActionKey ActionKey,
    int ActionVersion,
    Guid IdempotencyKey,
    ActionExecutionStage Stage,
    string Code,
    string Message,
    string? ReceiptReference,
    string ContractHash);

public interface IActionContinuationHost
{
    /// <summary>True only when the host persists continuation authority beyond this process.</summary>
    bool SupportsDurableState { get; }

    ValueTask<ContinuationToken> CreateAsync(KernelContinuationRequest request, CancellationToken cancellationToken);

    ValueTask<ActionUncertainty> RecordUncertaintyAsync(
        KernelUncertaintyRequest request,
        CancellationToken cancellationToken);
}

public sealed record KernelContinuationState(
    ContinuationToken Token,
    KernelContinuationRequest Request,
    ContinuationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? CompletedAt,
    string? ClaimOwner = null,
    DateTimeOffset? LeaseExpiresAt = null,
    int Generation = 0,
    long Revision = 0,
    ContinuationDestination? ResultDestination = null,
    string? ProtectedInput = null,
    string? CompletedOutcome = null);

public sealed record KernelRecoveryState(
    ActionRecoveryReference Reference,
    KernelUncertaintyRequest Request,
    ActionUncertainty Uncertainty,
    ContinuationState State,
    DateTimeOffset CreatedAt,
    string? ProtectedInput,
    ContinuationDestination ResultDestination);

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

internal static class KernelServiceResolution
{
    public static object Resolve(Type serviceType, IServiceProvider? serviceProvider)
    {
        var service = serviceProvider?.GetService(serviceType);
        if (service is not null)
            return service;

        try
        {
            return Activator.CreateInstance(serviceType)
                ?? throw new KernelGraphCompilationException($"Cannot create service '{serviceType.FullName}'.");
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
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    public static JsonElement Serialize(object? value) =>
        JsonSerializer.SerializeToElement(value, Options);

    public static T Deserialize<T>(JsonElement value) =>
        typeof(T) == typeof(object)
            ? DeserializeObjectValue<T>(value)
            : value.Deserialize<T>(Options)
              ?? throw new KernelActionExecutionException(
                  $"The action input cannot deserialize as '{typeof(T).FullName}'.");

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
}
