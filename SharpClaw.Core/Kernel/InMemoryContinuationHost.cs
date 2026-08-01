using System.Collections.Concurrent;
using System.Security.Cryptography;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

public sealed class InMemoryContinuationHost : IActionContinuationHost
{
    private readonly ConcurrentDictionary<Guid, KernelContinuationState> _states = new();

    public ValueTask<ContinuationToken> CreateAsync(
        KernelContinuationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = new ContinuationToken(Guid.NewGuid(), Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        var state = new KernelContinuationState(
            token,
            request,
            ContinuationState.Pending,
            DateTimeOffset.UtcNow,
            null,
            null);

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
        return ValueTask.FromResult(uncertainty);
    }

    public bool TryGet(Guid tokenId, out KernelContinuationState? state) =>
        _states.TryGetValue(tokenId, out state);

    public bool TryClaim(Guid tokenId, string owner, out KernelContinuationState? claimed)
    {
        while (_states.TryGetValue(tokenId, out var current))
        {
            if (current.State != ContinuationState.Pending)
            {
                claimed = null;
                return false;
            }

            var next = current with
            {
                State = ContinuationState.Claimed,
                ClaimedAt = DateTimeOffset.UtcNow
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

    public bool TryComplete(Guid tokenId, out KernelContinuationState? completed)
    {
        while (_states.TryGetValue(tokenId, out var current))
        {
            if (current.State is ContinuationState.Completed or ContinuationState.Expired)
            {
                completed = null;
                return false;
            }

            var next = current with
            {
                State = ContinuationState.Completed,
                CompletedAt = DateTimeOffset.UtcNow
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
