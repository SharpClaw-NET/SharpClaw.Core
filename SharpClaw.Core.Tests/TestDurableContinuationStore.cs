using System.Collections.Concurrent;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

internal sealed class TestDurableContinuationStore : IActionContinuationStore
{
    private readonly ConcurrentDictionary<Guid, KernelContinuationState> _states = new();
    private readonly ConcurrentDictionary<Guid, KernelRecoveryState> _recoveries = new();

    public bool IsDurable => true;

    public int ContinuationCount => _states.Count;

    public int RecoveryCount => _recoveries.Count;

    public ValueTask<KernelContinuationState?> ReadAsync(
        Guid tokenId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_states.TryGetValue(tokenId, out var state) ? state : null);
    }

    public ValueTask<bool> TryCreateAsync(
        KernelContinuationState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_states.TryAdd(state.TokenId, state));
    }

    public ValueTask<bool> TryUpdateAsync(
        Guid tokenId,
        KernelContinuationState expected,
        KernelContinuationState replacement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_states.TryUpdate(tokenId, replacement, expected));
    }

    public ValueTask<bool> TryDeleteAsync(
        Guid tokenId,
        KernelContinuationState expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            ((ICollection<KeyValuePair<Guid, KernelContinuationState>>)_states)
                .Remove(new KeyValuePair<Guid, KernelContinuationState>(tokenId, expected)));
    }

    public ValueTask<KernelRecoveryState?> ReadRecoveryAsync(
        Guid recoveryId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_recoveries.TryGetValue(recoveryId, out var state) ? state : null);
    }

    public ValueTask<bool> TryCreateRecoveryAsync(
        KernelRecoveryState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_recoveries.TryAdd(state.Reference.RecoveryId, state));
    }

    public ValueTask<bool> TryUpdateRecoveryAsync(
        Guid recoveryId,
        KernelRecoveryState expected,
        KernelRecoveryState replacement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_recoveries.TryUpdate(recoveryId, replacement, expected));
    }

    public ValueTask<bool> TryDeleteRecoveryAsync(
        Guid recoveryId,
        KernelRecoveryState expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            ((ICollection<KeyValuePair<Guid, KernelRecoveryState>>)_recoveries)
                .Remove(new KeyValuePair<Guid, KernelRecoveryState>(recoveryId, expected)));
    }
}
