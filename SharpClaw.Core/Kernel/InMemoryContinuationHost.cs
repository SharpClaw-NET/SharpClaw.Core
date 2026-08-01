using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

/// <summary>Provides the Core continuation state machine over a neutral store boundary.</summary>
public class StoreBackedContinuationHost : IActionContinuationHost
{
    private readonly IActionContinuationStore _store;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _recoveryLifetime;

    public StoreBackedContinuationHost(
        IActionContinuationStore store,
        TimeSpan? leaseDuration = null,
        TimeSpan? recoveryLifetime = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _leaseDuration = leaseDuration ?? TimeSpan.FromMinutes(5);
        _recoveryLifetime = recoveryLifetime ?? TimeSpan.FromDays(7);
        if (_leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (_recoveryLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(recoveryLifetime));
    }

    public bool SupportsDurableState => _store.IsDurable;

    public async ValueTask<ContinuationToken> CreateAsync(
        KernelContinuationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!SupportsDurableState || !request.Policy.Durable)
            throw new KernelActionExecutionException(
                "A durable continuation requires a durable persistence store and durable action policy.");
        ValidateContinuationRequest(request);

        var now = DateTimeOffset.UtcNow;
        var maximumExpiry = now + request.Policy.MaximumLifetime;
        var expiresAt = request.Defer.ExpiresAt <= maximumExpiry
            ? request.Defer.ExpiresAt
            : maximumExpiry;
        if (expiresAt <= now)
            throw new KernelActionExecutionException("The continuation expiration must be in the future.");

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var token = new ContinuationToken(Guid.NewGuid(), secret);
        var state = new KernelContinuationState(
            token.TokenId,
            HashSecret(secret),
            request,
            ContinuationState.Pending,
            now,
            expiresAt,
            null,
            null,
            null,
            null,
            0,
            1,
            request.Destination ?? new ContinuationDestination("continuation"),
            request.ProtectedInput,
            null);

        if (!await _store.TryCreateAsync(state, cancellationToken))
            throw new KernelActionExecutionException("The continuation token could not be allocated.");

        return token;
    }

    public ValueTask<KernelContinuationState?> GetAsync(
        Guid tokenId,
        CancellationToken cancellationToken) =>
        _store.ReadAsync(tokenId, cancellationToken);

    public async ValueTask<KernelContinuationState?> ClaimAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!ValidClaimRequest(claim, now) || !VerifySecret(secret, await ReadAsync(tokenId, cancellationToken)))
            return null;

        while (await ReadAsync(tokenId, cancellationToken) is { } current)
        {
            if (!IsValidContinuationState(current))
                return null;
            if (!string.Equals(current.Request.ContractHash, claim.ContractHash, StringComparison.Ordinal))
                return null;
            if (current.State is ContinuationState.Completed or ContinuationState.Cancelled or
                ContinuationState.Delivered or ContinuationState.Expired or ContinuationState.Deleted ||
                current.ExpiresAt <= now)
            {
                if (current.ExpiresAt <= now)
                    await ExpireAsync(tokenId, now, cancellationToken);
                return null;
            }

            var leaseExpired = current.LeaseExpiresAt is { } expiry && expiry <= now;
            if (current.State == ContinuationState.CancelRequested)
            {
                if (!leaseExpired)
                    return null;
                var cancelled = current with
                {
                    State = ContinuationState.Cancelled,
                    Revision = current.Revision + 1
                };
                if (await _store.TryUpdateAsync(tokenId, current, cancelled, cancellationToken))
                    return null;
                continue;
            }
            if (current.State == ContinuationState.Claimed && !leaseExpired)
                return null;
            if (current.State is not (ContinuationState.Pending or ContinuationState.Claimed))
                return null;
            if (claim.ExpectedRevision != current.Revision ||
                claim.Generation != current.Generation + 1)
                return null;
            var leaseExpiresAt = claim.LeaseExpiresAt <= now
                ? now + _leaseDuration
                : claim.LeaseExpiresAt;
            leaseExpiresAt = leaseExpiresAt <= current.ExpiresAt
                ? leaseExpiresAt
                : current.ExpiresAt;
            if (leaseExpiresAt <= now)
                return null;

            var next = current with
            {
                State = ContinuationState.Claimed,
                ClaimedAt = now,
                ClaimOwner = claim.Owner,
                LeaseExpiresAt = leaseExpiresAt,
                Generation = current.Generation + 1,
                Revision = current.Revision + 1
            };
            if (await _store.TryUpdateAsync(tokenId, current, next, cancellationToken))
                return next;
        }

        return null;
    }

    public ValueTask<KernelContinuationState?> RenewLeaseAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        MutateClaimAsync(
            tokenId,
            secret,
            claim,
            now,
            state => state with
            {
                LeaseExpiresAt = Min(state.ExpiresAt, claim.LeaseExpiresAt <= now
                    ? now + _leaseDuration
                    : claim.LeaseExpiresAt),
                Revision = state.Revision + 1
            },
            cancellationToken);

    public async ValueTask<KernelContinuationState?> ResumeAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var state = await ReadAsync(tokenId, cancellationToken);
        return IsCurrentClaim(state, secret, claim, now, requireLiveLease: true) ? state : null;
    }

    public async ValueTask<KernelContinuationState?> CompleteAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        string outcome,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outcome))
            return null;
        return await MutateClaimAsync(
            tokenId,
            secret,
            claim,
            now,
            state => state with
            {
                State = ContinuationState.Completed,
                CompletedAt = now,
                CompletedOutcome = outcome,
                Revision = state.Revision + 1
            },
            cancellationToken);
    }

    public async ValueTask<KernelContinuationState?> CancelAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!ValidClaimRequest(claim, now) || !VerifySecret(secret, await ReadAsync(tokenId, cancellationToken)))
            return null;
        while (await ReadAsync(tokenId, cancellationToken) is { } current)
        {
            if (!IsValidContinuationState(current) || current.Request.ContractHash != claim.ContractHash ||
                current.ExpiresAt <= now || current.Revision != claim.ExpectedRevision)
                return null;
            KernelContinuationState? next = current.State switch
            {
                ContinuationState.Pending when claim.Generation == current.Generation + 1 => current with
                {
                    State = ContinuationState.Cancelled,
                    ClaimOwner = claim.Owner,
                    Generation = claim.Generation,
                    Revision = current.Revision + 1
                },
                ContinuationState.Claimed when current.ClaimOwner == claim.Owner &&
                                                   current.Generation == claim.Generation &&
                                                   current.LeaseExpiresAt > now => current with
                                                   {
                                                       State = ContinuationState.CancelRequested,
                                                       Revision = current.Revision + 1
                                                   },
                ContinuationState.CancelRequested when current.ClaimOwner == claim.Owner &&
                                                       current.Generation == claim.Generation => current with
                                                       {
                                                           State = ContinuationState.Cancelled,
                                                           Revision = current.Revision + 1
                                                       },
                _ => null
            };
            if (next is null)
                return null;
            if (await _store.TryUpdateAsync(tokenId, current, next, cancellationToken))
                return next;
        }

        return null;
    }

    public ValueTask<KernelContinuationState?> DeliverAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        MutateClaimAsync(
            tokenId,
            secret,
            claim,
            now,
            state => state.State != ContinuationState.Completed
                ? null
                : state with
                {
                    State = ContinuationState.Delivered,
                    Revision = state.Revision + 1
                },
            cancellationToken,
            requireLiveLease: false,
            allowCompleted: true);

    public ValueTask<KernelContinuationState?> AcknowledgeAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        MutateClaimAsync(
            tokenId,
            secret,
            claim,
            now,
            state => state.State != ContinuationState.Delivered
                ? null
                : state with { Revision = state.Revision + 1 },
            cancellationToken,
            requireLiveLease: false,
            allowCompleted: true);

    public async ValueTask<KernelContinuationState?> ExpireAsync(
        Guid tokenId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        while (await ReadAsync(tokenId, cancellationToken) is { } current)
        {
            if (current.State is ContinuationState.Completed or ContinuationState.Delivered or
                ContinuationState.Cancelled or ContinuationState.Expired or ContinuationState.Deleted ||
                current.ExpiresAt > now)
                return current;
            var next = current with
            {
                State = current.State is ContinuationState.Claimed or ContinuationState.CancelRequested
                    ? ContinuationState.OutcomeUncertain
                    : ContinuationState.Expired,
                Revision = current.Revision + 1
            };
            if (await _store.TryUpdateAsync(tokenId, current, next, cancellationToken))
                return next;
        }

        return null;
    }

    public async ValueTask<bool> DeleteAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var state = await ReadAsync(tokenId, cancellationToken);
        if (!IsCurrentClaim(state, secret, claim, now, requireLiveLease: false, allowExpired: true) ||
            state!.State is ContinuationState.Pending or ContinuationState.Claimed or ContinuationState.Deleted)
            return false;
        var next = state with
        {
            State = ContinuationState.Deleted,
            Revision = state.Revision + 1
        };
        return await _store.TryUpdateAsync(tokenId, state, next, cancellationToken);
    }

    public async ValueTask<KernelRecoveryRecord> RecordUncertaintyAsync(
        KernelUncertaintyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!SupportsDurableState)
            throw new KernelActionExecutionException(
                "An uncertain action outcome requires a durable persistence store.");
        if (request.Policy is { Durable: false })
            throw new KernelActionExecutionException(
                "An uncertain action outcome cannot use a non-durable continuation policy.");
        if (string.IsNullOrWhiteSpace(request.ContractHash))
            throw new KernelActionExecutionException("An uncertain outcome requires a non-empty contract hash.");
        if (request.ActionVersion < 1 || request.IdempotencyKey == Guid.Empty)
            throw new KernelActionExecutionException("An uncertain outcome requires a valid action version and idempotency key.");
        if (string.IsNullOrWhiteSpace(request.ProtectedInput))
            throw new KernelActionExecutionException("An uncertain outcome requires protected input.");
        var recordedAt = DateTimeOffset.UtcNow;
        var recovery = new ActionRecoveryReference(
            Guid.NewGuid(),
            request.ActionKey,
            request.ActionVersion,
            request.IdempotencyKey);
        var uncertainty = new ActionUncertainty(
            request.Code,
            request.Message,
            request.Stage,
            request.ReceiptReference,
            recovery,
            recordedAt);
        var token = new KernelRecoveryToken(
            recovery.RecoveryId,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var destination = request.ResultDestination ??
            new ContinuationDestination("action-recovery", recovery.RecoveryId.ToString("N"));
        if (string.IsNullOrWhiteSpace(destination.Kind))
            throw new KernelActionExecutionException("An uncertain outcome requires a result destination.");
        var policy = request.Policy ?? KernelCapabilities.DurableContinuation;
        if (policy.MaximumLifetime <= TimeSpan.Zero)
            throw new KernelActionExecutionException("An uncertain outcome requires a positive maximum lifetime.");
        var expiresAt = Min(recordedAt + _recoveryLifetime, recordedAt + policy.MaximumLifetime);
        var storedRequest = request with
        {
            ResultDestination = destination,
            Policy = policy
        };
        var state = new KernelRecoveryState(
            recovery,
            storedRequest,
            uncertainty,
            ContinuationState.OutcomeUncertain,
            recordedAt,
            expiresAt,
            HashSecret(token.Secret),
            null,
            null,
            null,
            null,
            0,
            1,
            request.ProtectedInput,
            destination,
            null);
        if (!await _store.TryCreateRecoveryAsync(state, cancellationToken))
            throw new KernelActionExecutionException("The action recovery record could not be stored.");
        return new KernelRecoveryRecord(uncertainty, token, state);
    }

    public ValueTask<KernelRecoveryState?> GetRecoveryAsync(
        Guid recoveryId,
        CancellationToken cancellationToken) =>
        _store.ReadRecoveryAsync(recoveryId, cancellationToken);

    public async ValueTask<KernelRecoveryState?> ClaimRecoveryAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!ValidClaimRequest(claim, now) ||
            !VerifySecret(secret, await ReadRecoveryAsync(recoveryId, cancellationToken)))
            return null;

        while (await ReadRecoveryAsync(recoveryId, cancellationToken) is { } current)
        {
            if (!IsValidRecoveryState(current))
                return null;
            if (!string.Equals(current.Request.ContractHash, claim.ContractHash, StringComparison.Ordinal))
                return null;
            if (current.State is ContinuationState.Completed or ContinuationState.Delivered or
                ContinuationState.Cancelled or ContinuationState.Expired or ContinuationState.Deleted ||
                current.ExpiresAt <= now)
            {
                if (current.ExpiresAt <= now)
                    await ExpireRecoveryAsync(recoveryId, now, cancellationToken);
                return null;
            }

            var leaseExpired = current.LeaseExpiresAt is { } expiry && expiry <= now;
            if (current.State == ContinuationState.CancelRequested)
            {
                if (!leaseExpired)
                    return null;
                var cancelled = current with
                {
                    State = ContinuationState.Cancelled,
                    Revision = current.Revision + 1
                };
                if (await _store.TryUpdateRecoveryAsync(recoveryId, current, cancelled, cancellationToken))
                    return null;
                continue;
            }
            if (current.State != ContinuationState.OutcomeUncertain &&
                !(current.State == ContinuationState.Claimed && leaseExpired))
                return null;
            if (current.State == ContinuationState.Claimed && !leaseExpired)
                return null;
            if (claim.ExpectedRevision != current.Revision ||
                claim.Generation != current.Generation + 1)
                return null;
            var leaseExpiresAt = claim.LeaseExpiresAt <= now
                ? now + _leaseDuration
                : claim.LeaseExpiresAt;
            leaseExpiresAt = leaseExpiresAt <= current.ExpiresAt
                ? leaseExpiresAt
                : current.ExpiresAt;
            if (leaseExpiresAt <= now)
                return null;

            var next = current with
            {
                State = ContinuationState.Claimed,
                ClaimedAt = now,
                ClaimOwner = claim.Owner,
                LeaseExpiresAt = leaseExpiresAt,
                Generation = current.Generation + 1,
                Revision = current.Revision + 1
            };
            if (await _store.TryUpdateRecoveryAsync(recoveryId, current, next, cancellationToken))
                return next;
        }

        return null;
    }

    public ValueTask<KernelRecoveryState?> RenewRecoveryLeaseAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        MutateRecoveryClaimAsync(
            recoveryId,
            secret,
            claim,
            now,
            state => state with
            {
                LeaseExpiresAt = Min(state.ExpiresAt, claim.LeaseExpiresAt <= now
                    ? now + _leaseDuration
                    : claim.LeaseExpiresAt),
                Revision = state.Revision + 1
            },
            cancellationToken);

    public async ValueTask<KernelRecoveryState?> ResumeRecoveryAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var state = await ReadRecoveryAsync(recoveryId, cancellationToken);
        return IsCurrentRecoveryClaim(state, secret, claim, now, requireLiveLease: true)
            ? state
            : null;
    }

    public async ValueTask<KernelRecoveryState?> CompleteRecoveryAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        string outcome,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outcome))
            return null;
        return await MutateRecoveryClaimAsync(
            recoveryId,
            secret,
            claim,
            now,
            state => state with
            {
                State = ContinuationState.Completed,
                CompletedAt = now,
                CompletedOutcome = outcome,
                Revision = state.Revision + 1
            },
            cancellationToken);
    }

    public async ValueTask<KernelRecoveryState?> CancelRecoveryAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!ValidClaimRequest(claim, now) ||
            !VerifySecret(secret, await ReadRecoveryAsync(recoveryId, cancellationToken)))
            return null;
        while (await ReadRecoveryAsync(recoveryId, cancellationToken) is { } current)
        {
            if (!IsValidRecoveryState(current) || current.Request.ContractHash != claim.ContractHash ||
                current.ExpiresAt <= now || current.Revision != claim.ExpectedRevision)
                return null;
            KernelRecoveryState? next = current.State switch
            {
                ContinuationState.OutcomeUncertain when claim.Generation == current.Generation + 1 => current with
                {
                    State = ContinuationState.Cancelled,
                    ClaimOwner = claim.Owner,
                    Generation = claim.Generation,
                    Revision = current.Revision + 1
                },
                ContinuationState.Claimed when current.ClaimOwner == claim.Owner &&
                                                   current.Generation == claim.Generation &&
                                                   current.LeaseExpiresAt > now => current with
                                                   {
                                                       State = ContinuationState.CancelRequested,
                                                       Revision = current.Revision + 1
                                                   },
                ContinuationState.CancelRequested when current.ClaimOwner == claim.Owner &&
                                                       current.Generation == claim.Generation => current with
                                                       {
                                                           State = ContinuationState.Cancelled,
                                                           Revision = current.Revision + 1
                                                       },
                _ => null
            };
            if (next is null)
                return null;
            if (await _store.TryUpdateRecoveryAsync(recoveryId, current, next, cancellationToken))
                return next;
        }

        return null;
    }

    public ValueTask<KernelRecoveryState?> DeliverRecoveryAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        MutateRecoveryClaimAsync(
            recoveryId,
            secret,
            claim,
            now,
            state => state.State != ContinuationState.Completed
                ? null
                : state with { State = ContinuationState.Delivered, Revision = state.Revision + 1 },
            cancellationToken,
            requireLiveLease: false,
            allowCompleted: true);

    public ValueTask<KernelRecoveryState?> AcknowledgeRecoveryAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        MutateRecoveryClaimAsync(
            recoveryId,
            secret,
            claim,
            now,
            state => state.State != ContinuationState.Delivered
                ? null
                : state with { Revision = state.Revision + 1 },
            cancellationToken,
            requireLiveLease: false,
            allowCompleted: true);

    public async ValueTask<KernelRecoveryState?> ExpireRecoveryAsync(
        Guid recoveryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        while (await ReadRecoveryAsync(recoveryId, cancellationToken) is { } current)
        {
            if (current.State is ContinuationState.Completed or ContinuationState.Delivered or
                ContinuationState.Cancelled or ContinuationState.Expired or ContinuationState.Deleted ||
                current.ExpiresAt > now)
                return current;
            var next = current with
            {
                State = current.State is ContinuationState.Claimed or ContinuationState.CancelRequested
                    ? ContinuationState.OutcomeUncertain
                    : ContinuationState.Expired,
                Revision = current.Revision + 1
            };
            if (await _store.TryUpdateRecoveryAsync(recoveryId, current, next, cancellationToken))
                return next;
        }

        return null;
    }

    public async ValueTask<bool> DeleteRecoveryAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var state = await ReadRecoveryAsync(recoveryId, cancellationToken);
        if (!IsCurrentRecoveryClaim(state, secret, claim, now, requireLiveLease: false, allowExpired: true) ||
            state!.State is not (ContinuationState.Completed or ContinuationState.Delivered or
                ContinuationState.Cancelled or ContinuationState.Expired))
            return false;
        var next = state with
        {
            State = ContinuationState.Deleted,
            Revision = state.Revision + 1
        };
        return await _store.TryUpdateRecoveryAsync(recoveryId, state, next, cancellationToken);
    }

    private async ValueTask<KernelRecoveryState?> MutateRecoveryClaimAsync(
        Guid recoveryId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        Func<KernelRecoveryState, KernelRecoveryState?> mutate,
        CancellationToken cancellationToken,
        bool requireLiveLease = true,
        bool allowCompleted = false)
    {
        while (await ReadRecoveryAsync(recoveryId, cancellationToken) is { } current)
        {
            if (!IsCurrentRecoveryClaim(current, secret, claim, now, requireLiveLease) ||
                (!allowCompleted && current.State != ContinuationState.Claimed))
                return null;
            var next = mutate(current);
            if (next is null)
                return null;
            if (await _store.TryUpdateRecoveryAsync(recoveryId, current, next, cancellationToken))
                return next;
        }

        return null;
    }

    private ValueTask<KernelRecoveryState?> ReadRecoveryAsync(
        Guid recoveryId,
        CancellationToken cancellationToken) =>
        _store.ReadRecoveryAsync(recoveryId, cancellationToken);

    private static bool IsCurrentRecoveryClaim(
        KernelRecoveryState? state,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        bool requireLiveLease,
        bool allowExpired = false)
    {
        if (state is null || string.IsNullOrWhiteSpace(secret) ||
            !ValidClaimRequest(claim, now, allowExpired) || !IsValidRecoveryState(state))
            return false;
        if (!VerifySecret(secret, state) || state.ClaimOwner != claim.Owner ||
            state.Generation != claim.Generation || state.Revision != claim.ExpectedRevision ||
            state.Request.ContractHash != claim.ContractHash ||
            (!allowExpired && state.ExpiresAt <= now) || state.State == ContinuationState.Deleted)
            return false;
        return !requireLiveLease || state.LeaseExpiresAt is { } lease && lease > now;
    }

    private static void ValidateContinuationRequest(KernelContinuationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ContractHash))
            throw new KernelActionExecutionException("A continuation requires a non-empty contract hash.");
        if (request.ActionVersion < 1 || request.IdempotencyKey == Guid.Empty)
            throw new KernelActionExecutionException("A continuation requires a valid action version and idempotency key.");
        if (request.Destination is not { } destination || string.IsNullOrWhiteSpace(destination.Kind))
            throw new KernelActionExecutionException("A continuation requires a result destination.");
        if (string.IsNullOrWhiteSpace(request.ProtectedInput))
            throw new KernelActionExecutionException("A continuation requires protected input.");
        if (request.Policy.MaximumLifetime <= TimeSpan.Zero)
            throw new KernelActionExecutionException("A continuation requires a positive maximum lifetime.");
    }

    private static bool VerifySecret(string secret, KernelRecoveryState? state)
    {
        if (state is null || string.IsNullOrWhiteSpace(secret))
            return false;
        try
        {
            var expected = Convert.FromHexString(state.TokenHash);
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async ValueTask<KernelContinuationState?> MutateClaimAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        Func<KernelContinuationState, KernelContinuationState?> mutate,
        CancellationToken cancellationToken,
        bool requireLiveLease = true,
        bool allowCompleted = false)
    {
        while (await ReadAsync(tokenId, cancellationToken) is { } current)
        {
            if (!IsCurrentClaim(current, secret, claim, now, requireLiveLease) ||
                (!allowCompleted && current.State != ContinuationState.Claimed))
                return null;
            var next = mutate(current);
            if (next is null)
                return null;
            if (await _store.TryUpdateAsync(tokenId, current, next, cancellationToken))
                return next;
        }

        return null;
    }

    private ValueTask<KernelContinuationState?> ReadAsync(
        Guid tokenId,
        CancellationToken cancellationToken) =>
        _store.ReadAsync(tokenId, cancellationToken);

    private static bool ValidClaimRequest(
        KernelContinuationClaim claim,
        DateTimeOffset now,
        bool allowExpired = false) =>
        !string.IsNullOrWhiteSpace(claim.ContractHash) &&
        !string.IsNullOrWhiteSpace(claim.Owner) &&
        claim.Generation > 0 &&
        claim.ExpectedRevision > 0 &&
        (allowExpired || claim.LeaseExpiresAt == default || claim.LeaseExpiresAt > now);

    private static bool IsCurrentClaim(
        KernelContinuationState? state,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        bool requireLiveLease,
        bool allowExpired = false)
    {
        if (state is null || string.IsNullOrWhiteSpace(secret) ||
            !ValidClaimRequest(claim, now, allowExpired) || !IsValidContinuationState(state))
            return false;
        if (!VerifySecret(secret, state) || state.ClaimOwner != claim.Owner ||
            state.Generation != claim.Generation || state.Revision != claim.ExpectedRevision ||
            state.Request.ContractHash != claim.ContractHash ||
            (!allowExpired && state.ExpiresAt <= now) || state.State == ContinuationState.Deleted)
            return false;
        return !requireLiveLease || state.LeaseExpiresAt is { } lease && lease > now;
    }

    private static bool VerifySecret(string secret, KernelContinuationState? state)
    {
        if (state is null || string.IsNullOrWhiteSpace(secret))
            return false;
        try
        {
            var expected = Convert.FromHexString(state.TokenHash);
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsValidContinuationState(KernelContinuationState state) =>
        state.TokenId != Guid.Empty &&
        state.TokenHash.Length == 64 &&
        state.Request.ActionVersion > 0 &&
        state.Request.IdempotencyKey != Guid.Empty &&
        !string.IsNullOrWhiteSpace(state.Request.ContractHash) &&
        state.Request.Destination is { } destination &&
        !string.IsNullOrWhiteSpace(destination.Kind) &&
        state.ResultDestination == destination &&
        !string.IsNullOrWhiteSpace(state.Request.ProtectedInput) &&
        state.ProtectedInput == state.Request.ProtectedInput &&
        (state.State is not (ContinuationState.Completed or ContinuationState.Delivered) ||
         !string.IsNullOrWhiteSpace(state.CompletedOutcome));

    private static bool IsValidRecoveryState(KernelRecoveryState state) =>
        state.Reference.RecoveryId != Guid.Empty &&
        state.TokenHash.Length == 64 &&
        state.Request.ActionVersion > 0 &&
        state.Request.IdempotencyKey != Guid.Empty &&
        !string.IsNullOrWhiteSpace(state.Request.ContractHash) &&
        state.Request.ResultDestination is { } destination &&
        !string.IsNullOrWhiteSpace(destination.Kind) &&
        state.ResultDestination == destination &&
        !string.IsNullOrWhiteSpace(state.Request.ProtectedInput) &&
        state.ProtectedInput == state.Request.ProtectedInput &&
        (state.State is not (ContinuationState.Completed or ContinuationState.Delivered) ||
         !string.IsNullOrWhiteSpace(state.CompletedOutcome));

    private static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;
}

/// <summary>Provides process-local state and never claims durable storage.</summary>
public sealed class InMemoryContinuationHost : StoreBackedContinuationHost
{
    public InMemoryContinuationHost(TimeSpan? leaseDuration = null)
        : base(new InMemoryContinuationStore(), leaseDuration)
    {
    }
}

/// <summary>Provides a process-local persistence implementation for tests and local hosts.</summary>
public sealed class InMemoryContinuationStore : IActionContinuationStore
{
    private readonly ConcurrentDictionary<Guid, KernelContinuationState> _states = new();
    private readonly ConcurrentDictionary<Guid, KernelRecoveryState> _recoveries = new();

    public bool IsDurable => false;

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
