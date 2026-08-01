using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

/// <summary>Provides bounded continuation state for tests and process-local hosts.</summary>
public sealed class InMemoryContinuationHost : IActionContinuationHost
{
    private readonly ConcurrentDictionary<Guid, KernelContinuationState> _states = new();
    private readonly ConcurrentDictionary<Guid, KernelRecoveryState> _recoveries = new();
    private readonly bool _durable;
    private readonly TimeSpan _leaseDuration;

    public InMemoryContinuationHost(bool supportsDurableState = false, TimeSpan? leaseDuration = null)
    {
        _durable = supportsDurableState;
        _leaseDuration = leaseDuration ?? TimeSpan.FromMinutes(5);
        if (_leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
    }

    public bool SupportsDurableState => _durable;

    public ValueTask<ContinuationToken> CreateAsync(
        KernelContinuationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_durable)
            throw new KernelActionExecutionException(
                "A durable continuation requires a continuation host with durable state support.");

        var token = new ContinuationToken(
            Guid.NewGuid(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        var state = new KernelContinuationState(
            token,
            request,
            ContinuationState.Pending,
            DateTimeOffset.UtcNow,
            null,
            null,
            ResultDestination: request.Destination ?? new ContinuationDestination("continuation"),
            ProtectedInput: request.ProtectedInput,
            Revision: 1);

        if (!_states.TryAdd(token.TokenId, state))
            throw new KernelActionExecutionException("The continuation token could not be allocated.");

        return ValueTask.FromResult(token);
    }

    public ValueTask<ActionUncertainty> RecordUncertaintyAsync(
        KernelUncertaintyRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recoveryId = Guid.NewGuid();
        var recovery = new ActionRecoveryReference(
            recoveryId,
            request.ActionKey,
            request.ActionVersion,
            request.IdempotencyKey);
        var uncertainty = new ActionUncertainty(
            request.Code,
            request.Message,
            request.Stage,
            request.ReceiptReference ?? string.Empty,
            recovery,
            DateTimeOffset.UtcNow);
        var state = new KernelRecoveryState(
            recovery,
            request,
            uncertainty,
            ContinuationState.OutcomeUncertain,
            uncertainty.RecordedAt,
            ProtectedInput: null,
            new ContinuationDestination("action-recovery", recoveryId.ToString("N")));
        if (!_recoveries.TryAdd(recoveryId, state))
            throw new KernelActionExecutionException("The action recovery record could not be stored.");
        return ValueTask.FromResult(uncertainty);
    }

    public bool TryGet(Guid tokenId, out KernelContinuationState? state) =>
        _states.TryGetValue(tokenId, out state);

    public bool TryGetRecovery(Guid recoveryId, out KernelRecoveryState? state) =>
        _recoveries.TryGetValue(recoveryId, out state);

    public bool TryClaim(Guid tokenId, string owner, out KernelContinuationState? claimed) =>
        TryClaim(tokenId, string.Empty, owner, DateTimeOffset.UtcNow, out claimed);

    public bool TryClaim(
        Guid tokenId,
        string secret,
        string owner,
        DateTimeOffset now,
        out KernelContinuationState? claimed)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            claimed = null;
            return false;
        }

        while (_states.TryGetValue(tokenId, out var current))
        {
            if (secret.Length > 0 && !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(current.Token.Secret),
                    Encoding.UTF8.GetBytes(secret)))
            {
                claimed = null;
                return false;
            }

            var leaseExpired = current.LeaseExpiresAt is { } expiry && expiry <= now;
            if (current.State is ContinuationState.Completed or ContinuationState.Cancelled or ContinuationState.Deleted ||
                (current.State == ContinuationState.Claimed && !leaseExpired))
            {
                claimed = null;
                return false;
            }

            if (current.Request.Defer.ExpiresAt <= now)
            {
                var expired = current with
                {
                    State = ContinuationState.Expired,
                    Revision = current.Revision + 1
                };
                _states.TryUpdate(tokenId, expired, current);
                claimed = null;
                return false;
            }

            var next = current with
            {
                State = ContinuationState.Claimed,
                ClaimedAt = now,
                ClaimOwner = owner,
                LeaseExpiresAt = now + _leaseDuration,
                Generation = current.Generation + 1,
                Revision = current.Revision + 1
            };

            if (_states.TryUpdate(tokenId, next, current))
            {
                claimed = next;
                return true;
            }
        }

        claimed = null;
        return false;
    }

    public bool TryComplete(Guid tokenId, out KernelContinuationState? completed) =>
        TryComplete(tokenId, string.Empty, 0, null, DateTimeOffset.UtcNow, out completed);

    public bool TryComplete(
        Guid tokenId,
        string owner,
        int generation,
        string? outcome,
        DateTimeOffset now,
        out KernelContinuationState? completed)
    {
        while (_states.TryGetValue(tokenId, out var current))
        {
            if (current.State is ContinuationState.Completed or ContinuationState.Expired or ContinuationState.Deleted ||
                (!string.IsNullOrEmpty(owner) &&
                 (current.ClaimOwner != owner || current.Generation != generation ||
                  current.LeaseExpiresAt is not null && current.LeaseExpiresAt <= now)))
            {
                completed = null;
                return false;
            }

            var next = current with
            {
                State = ContinuationState.Completed,
                CompletedAt = now,
                CompletedOutcome = outcome,
                Revision = current.Revision + 1
            };

            if (_states.TryUpdate(tokenId, next, current))
            {
                completed = next;
                return true;
            }
        }

        completed = null;
        return false;
    }
}
