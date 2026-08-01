using System.Collections.Frozen;
using System.Collections.ObjectModel;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

public sealed class KernelActionExecutionContext
{
    public KernelActionExecutionContext(
        RequestPrincipal caller,
        ExtensionFeatureSet features,
        Guid traceId,
        Guid idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(features);
        if (traceId == Guid.Empty)
            throw new ArgumentException("The action trace identifier must not be empty.", nameof(traceId));
        if (idempotencyKey == Guid.Empty)
            throw new ArgumentException("The action idempotency key must not be empty.", nameof(idempotencyKey));

        Caller = new RequestPrincipal(
            caller.SubjectId,
            caller.DisplayName,
            (caller.Roles ?? FrozenSet<string>.Empty).ToFrozenSet(StringComparer.Ordinal),
            caller.IsAuthenticated);
        Features = new ExtensionFeatureSet(new ReadOnlyCollection<ExtensionFeature>(
            features.Items.Select(feature => new ExtensionFeature(
                feature.ContractName,
                feature.SchemaVersion,
                feature.OwnerModuleId,
                feature.MaxBytes,
                feature.Value.ValueKind == System.Text.Json.JsonValueKind.Undefined
                    ? default
                    : feature.Value.Clone()))
                .ToArray()));
        TraceId = traceId;
        IdempotencyKey = idempotencyKey;
    }

    public RequestPrincipal Caller { get; }

    public ExtensionFeatureSet Features { get; }

    public Guid TraceId { get; }

    public Guid IdempotencyKey { get; }
}

public enum KernelActionRepeatEvidenceKind
{
    Conflict,
    IdempotencyAccepted,
    DurableReceipt
}

public sealed record KernelActionRepeatEvidenceRequest(
    KernelActionRepeatEvidenceKind RequiredKind,
    SharpClawActionKey ActionKey,
    int ActionVersion,
    string IdempotencyScope,
    Guid IdempotencyKey,
    Guid PriorInvocationId,
    int PriorAttempt,
    Guid NextInvocationId,
    int NextAttempt,
    DateTimeOffset RequestedAt);

public sealed record KernelActionRepeatEvidence(
    string EvidenceId,
    KernelActionRepeatEvidenceKind Kind,
    SharpClawActionKey ActionKey,
    int ActionVersion,
    string IdempotencyScope,
    Guid IdempotencyKey,
    Guid PriorInvocationId,
    int PriorAttempt,
    Guid NextInvocationId,
    int NextAttempt,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public interface IKernelActionRepeatEvidenceAuthority
{
    ValueTask<KernelActionRepeatEvidence?> AuthorizeAsync(
        KernelActionRepeatEvidenceRequest request,
        CancellationToken cancellationToken);
}

internal sealed class DenyKernelActionRepeatEvidenceAuthority : IKernelActionRepeatEvidenceAuthority
{
    public ValueTask<KernelActionRepeatEvidence?> AuthorizeAsync(
        KernelActionRepeatEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<KernelActionRepeatEvidence?>(null);
    }
}
