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
    private readonly IKernelContinuationReceiptResolver _receiptResolver;
    private readonly TimeSpan _retentionPeriod;

    public StoreBackedContinuationHost(
        IActionContinuationStore store,
        TimeSpan? leaseDuration = null,
        TimeSpan? recoveryLifetime = null,
        IKernelContinuationReceiptResolver? receiptResolver = null,
        TimeSpan? retentionPeriod = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _leaseDuration = leaseDuration ?? TimeSpan.FromMinutes(5);
        _recoveryLifetime = recoveryLifetime ?? TimeSpan.FromDays(7);
        _receiptResolver = receiptResolver ?? new NullKernelContinuationReceiptResolver();
        _retentionPeriod = retentionPeriod ?? TimeSpan.FromDays(7);
        if (_leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (_recoveryLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(recoveryLifetime));
        if (_retentionPeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retentionPeriod));
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
            null,
            ContinuationExecutionStage.BeforeTerminal,
            ActionOutcomeCertainty.Certain,
            null,
            expiresAt + _retentionPeriod,
            null,
            null,
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
            if (current.State is ContinuationState.Claimed or ContinuationState.CancelRequested)
            {
                if (!leaseExpired)
                    return null;
                await ExpireAsync(tokenId, now, cancellationToken);
                return null;
            }
            if (current.State != ContinuationState.Pending)
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
        return state?.RecoveryReference is null &&
               IsCurrentClaim(state, secret, claim, now, requireLiveLease: true)
            ? state
            : null;
    }

    public ValueTask<KernelContinuationState?> SetExecutionStateAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        KernelContinuationExecutionUpdate update,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        return MutateClaimAsync(
            tokenId,
            secret,
            claim,
            now,
            state => !CanApplyExecutionUpdate(state, update)
                ? null
                : state with
                {
                    ExecutionStage = update.Stage,
                    OutcomeCertainty = update.Certainty,
                    ReceiptReference = update.ReceiptReference ?? state.ReceiptReference,
                    CompletedOutcome = update.PersistedOutcome ?? state.CompletedOutcome,
                    Revision = state.Revision + 1
                },
            cancellationToken,
            allowCancelRequested: true);
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
            state => state.OutcomeCertainty != ActionOutcomeCertainty.Certain ||
                     state.ExecutionStage is not (
                         ContinuationExecutionStage.TerminalReceipted or
                         ContinuationExecutionStage.OutcomePersisted) ||
                     state.ExecutionStage == ContinuationExecutionStage.OutcomePersisted &&
                     !string.Equals(state.CompletedOutcome, outcome, StringComparison.Ordinal)
                ? null
                : state with
                {
                    State = ContinuationState.Completed,
                    CompletedAt = now,
                    CompletedOutcome = outcome,
                    ExecutionStage = ContinuationExecutionStage.OutcomePersisted,
                    RetainUntil = now + _retentionPeriod,
                    Revision = state.Revision + 1
                },
            cancellationToken,
            allowCancelRequested: true);
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
            if (!IsValidContinuationState(current) || current.RecoveryReference is not null ||
                current.Request.ContractHash != claim.ContractHash ||
                current.ExpiresAt <= now || current.Revision != claim.ExpectedRevision)
                return null;
            KernelContinuationState? next = current.State switch
            {
                ContinuationState.Pending when claim.Generation == current.Generation + 1 &&
                                                   IsCertainPreTerminalSafePoint(current) =>
                    CreateTerminalState(
                        current with
                        {
                            ClaimedAt = now,
                            ClaimOwner = claim.Owner,
                            LeaseExpiresAt = claim.LeaseExpiresAt <= now
                                ? now + _leaseDuration
                                : claim.LeaseExpiresAt,
                            Generation = claim.Generation,
                            CancellationRequestedAt = now
                        },
                        ContinuationState.Cancelled,
                        now),
                ContinuationState.Claimed when current.ClaimOwner == claim.Owner &&
                                                   current.Generation == claim.Generation &&
                                                   current.LeaseExpiresAt > now => current with
                                                   {
                                                       State = ContinuationState.CancelRequested,
                                                       CancellationRequestedAt = now,
                                                       Revision = current.Revision + 1
                                                   },
                ContinuationState.CancelRequested when current.ClaimOwner == claim.Owner &&
                                                       current.Generation == claim.Generation &&
                                                       current.LeaseExpiresAt > now &&
                                                       IsCertainPreTerminalSafePoint(current) =>
                    CreateTerminalState(current, ContinuationState.Cancelled, now),
                _ => null
            };
            if (current.State == ContinuationState.CancelRequested &&
                current.ClaimOwner == claim.Owner &&
                current.Generation == claim.Generation &&
                current.LeaseExpiresAt > now &&
                !IsCertainPreTerminalSafePoint(current))
                return current;
            if (next is null)
                return null;
            if (await _store.TryUpdateAsync(tokenId, current, next, cancellationToken))
                return next;
        }

        return null;
    }

    public async ValueTask<KernelContinuationState?> ClaimContinuationDeliveryAsync(
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
            if (!IsValidContinuationState(current) ||
                !IsTerminalDeliveryState(current.State) ||
                current.ExecutionStage is not (
                    ContinuationExecutionStage.OutcomePersisted or
                    ContinuationExecutionStage.DeliveryStarted) ||
                current.OutcomeCertainty != ActionOutcomeCertainty.Certain ||
                string.IsNullOrWhiteSpace(current.CompletedOutcome) ||
                !string.Equals(current.Request.ContractHash, claim.ContractHash, StringComparison.Ordinal) ||
                claim.ExpectedRevision != current.Revision ||
                claim.Generation != current.Generation + 1 ||
                current.LeaseExpiresAt is { } activeLease && activeLease > now)
                return null;

            var leaseExpiresAt = claim.LeaseExpiresAt <= now
                ? now + _leaseDuration
                : claim.LeaseExpiresAt;
            if (leaseExpiresAt <= now)
                return null;

            var next = current with
            {
                ClaimedAt = now,
                ClaimOwner = claim.Owner,
                LeaseExpiresAt = leaseExpiresAt,
                Generation = current.Generation + 1,
                Revision = current.Revision + 1,
                ExecutionStage = ContinuationExecutionStage.OutcomePersisted,
                DeliveryAcknowledgedAt = null
            };
            if (await _store.TryUpdateAsync(tokenId, current, next, cancellationToken))
                return next;
        }

        return null;
    }

    public ValueTask<KernelContinuationState?> BeginDeliveryAsync(
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
            state => !IsTerminalDeliveryState(state.State) ||
                     state.ExecutionStage != ContinuationExecutionStage.OutcomePersisted ||
                     state.OutcomeCertainty != ActionOutcomeCertainty.Certain ||
                     string.IsNullOrWhiteSpace(state.CompletedOutcome) ||
                     state.DeliveryAcknowledgedAt is not null
                ? null
                : state with
                {
                    ExecutionStage = ContinuationExecutionStage.DeliveryStarted,
                    DeliveryAcknowledgedAt = null,
                    Revision = state.Revision + 1
                },
            cancellationToken,
            requireLiveLease: true,
            allowCompleted: true,
            allowRecovery: true,
            allowExpired: true);

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
            state => !IsTerminalDeliveryState(state.State) ||
                     state.ExecutionStage != ContinuationExecutionStage.DeliveryStarted ||
                     state.OutcomeCertainty != ActionOutcomeCertainty.Certain ||
                     string.IsNullOrWhiteSpace(state.CompletedOutcome) ||
                     state.DeliveryAcknowledgedAt is not null
                ? null
                : state with
                {
                    State = ContinuationState.Delivered,
                    DeliveryAcknowledgedAt = now,
                    Revision = state.Revision + 1
                },
            cancellationToken,
            requireLiveLease: true,
            allowCompleted: true,
            allowRecovery: true,
            allowExpired: true);

    public async ValueTask<KernelContinuationState?> ExpireAsync(
        Guid tokenId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        while (await ReadAsync(tokenId, cancellationToken) is { } current)
        {
            if (!IsValidContinuationState(current))
                return null;
            if (current.State is ContinuationState.Delivered or ContinuationState.Deleted or
                ContinuationState.OutcomeUncertain)
                return current;

            if (IsTerminalDeliveryState(current.State))
            {
                var deliveryLeaseExpired = current.LeaseExpiresAt is { } deliveryLease &&
                                           deliveryLease <= now;
                if (!deliveryLeaseExpired)
                    return current;
                var recoverable = current with
                {
                    ClaimedAt = null,
                    ClaimOwner = null,
                    LeaseExpiresAt = null,
                    ExecutionStage = ContinuationExecutionStage.OutcomePersisted,
                    DeliveryAcknowledgedAt = null,
                    Revision = current.Revision + 1
                };
                if (await _store.TryUpdateAsync(tokenId, current, recoverable, cancellationToken))
                    return recoverable;
                continue;
            }

            var claimExpired = current.State is ContinuationState.Claimed or ContinuationState.CancelRequested &&
                               current.LeaseExpiresAt is { } leaseExpiry && leaseExpiry <= now;
            var continuationExpired = current.ExpiresAt <= now;
            if (!claimExpired && !continuationExpired)
                return current;

            KernelContinuationState next;
            if (current.State is ContinuationState.Claimed or ContinuationState.CancelRequested)
            {
                next = current with
                {
                    State = ContinuationState.OutcomeUncertain,
                    ClaimOwner = null,
                    LeaseExpiresAt = null,
                    OutcomeCertainty = CertaintyForExpiredClaim(current.ExecutionStage),
                    RecoveryReference = current.RecoveryReference ?? new ActionRecoveryReference(
                        Guid.NewGuid(),
                        current.Request.ActionKey,
                        current.Request.ActionVersion,
                        current.Request.IdempotencyKey),
                    RetainUntil = now + _retentionPeriod,
                    Revision = current.Revision + 1
                };
            }
            else
            {
                next = CreateTerminalState(current, ContinuationState.Expired, now);
            }
            if (await _store.TryUpdateAsync(tokenId, current, next, cancellationToken))
                return next;
        }

        return null;
    }

    public async ValueTask<KernelContinuationState?> ClaimContinuationRecoveryAsync(
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
            if (!IsValidContinuationState(current) ||
                current.State != ContinuationState.OutcomeUncertain ||
                current.RecoveryReference is null ||
                current.RetainUntil <= now ||
                !string.Equals(current.Request.ContractHash, claim.ContractHash, StringComparison.Ordinal) ||
                claim.ExpectedRevision != current.Revision ||
                claim.Generation != current.Generation + 1)
                return null;

            var leaseExpiresAt = claim.LeaseExpiresAt <= now
                ? now + _leaseDuration
                : claim.LeaseExpiresAt;
            leaseExpiresAt = Min(current.RetainUntil, leaseExpiresAt);
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

    public async ValueTask<KernelContinuationState?> RecoverContinuationAsync(
        Guid tokenId,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        while (await ReadAsync(tokenId, cancellationToken) is { } current)
        {
            if (!IsCurrentContinuationRecoveryClaim(current, secret, claim, now))
                return null;

            var receipt = current.ExecutionStage is
                ContinuationExecutionStage.TerminalStarted or
                ContinuationExecutionStage.TerminalReceipted
                    ? await FindValidReceiptAsync(current, now, cancellationToken)
                    : null;
            var next = ResolveContinuationRecovery(current, receipt, now);
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
            state!.State != ContinuationState.Delivered ||
            state.DeliveryAcknowledgedAt is null ||
            state.RetainUntil == default ||
            now < state.RetainUntil ||
            state.RecoveryReference is not null && state.OutcomeCertainty == ActionOutcomeCertainty.Uncertain)
            return false;
        var next = state with
        {
            State = ContinuationState.Deleted,
            Request = state.Request with { ProtectedInput = null },
            ProtectedInput = null,
            CompletedOutcome = null,
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

    private static bool CanApplyExecutionUpdate(
        KernelContinuationState state,
        KernelContinuationExecutionUpdate update)
    {
        if (state.RecoveryReference is not null)
            return false;
        return update.Stage switch
        {
            ContinuationExecutionStage.TerminalStarted =>
                state.ExecutionStage == ContinuationExecutionStage.BeforeTerminal &&
                update.Certainty == ActionOutcomeCertainty.Uncertain &&
                string.IsNullOrWhiteSpace(update.ReceiptReference) &&
                string.IsNullOrWhiteSpace(update.PersistedOutcome),
            ContinuationExecutionStage.TerminalReceipted =>
                state.ExecutionStage == ContinuationExecutionStage.TerminalStarted &&
                update.Certainty == ActionOutcomeCertainty.Certain &&
                !string.IsNullOrWhiteSpace(update.ReceiptReference) &&
                string.IsNullOrWhiteSpace(update.PersistedOutcome),
            ContinuationExecutionStage.OutcomePersisted =>
                (state.ExecutionStage is ContinuationExecutionStage.TerminalStarted or
                    ContinuationExecutionStage.TerminalReceipted) &&
                update.Certainty == ActionOutcomeCertainty.Certain &&
                !string.IsNullOrWhiteSpace(update.PersistedOutcome),
            _ => false
        };
    }

    private static ActionOutcomeCertainty CertaintyForExpiredClaim(
        ContinuationExecutionStage stage) =>
        stage == ContinuationExecutionStage.TerminalStarted
            ? ActionOutcomeCertainty.Uncertain
            : ActionOutcomeCertainty.Certain;

    private static bool IsCurrentContinuationRecoveryClaim(
        KernelContinuationState state,
        string secret,
        KernelContinuationClaim claim,
        DateTimeOffset now) =>
        state.State == ContinuationState.Claimed &&
        state.RecoveryReference is not null &&
        state.RetainUntil > now &&
        VerifySecret(secret, state) &&
        state.ClaimOwner == claim.Owner &&
        state.Generation == claim.Generation &&
        state.Revision == claim.ExpectedRevision &&
        string.Equals(state.Request.ContractHash, claim.ContractHash, StringComparison.Ordinal) &&
        state.LeaseExpiresAt is { } lease &&
        lease > now;

    private async ValueTask<KernelContinuationReceipt?> FindValidReceiptAsync(
        KernelContinuationState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var recovery = state.RecoveryReference!;
        var request = new KernelContinuationReceiptRequest(
            state.TokenId,
            recovery,
            state.Request.ActionKey,
            state.Request.ActionVersion,
            state.Request.IdempotencyKey,
            state.Request.ContractHash,
            state.ExecutionStage,
            state.ReceiptReference);
        var receipt = await _receiptResolver.FindAsync(request, cancellationToken);
        return IsValidReceipt(request, receipt, now) ? receipt : null;
    }

    private static bool IsValidReceipt(
        KernelContinuationReceiptRequest request,
        KernelContinuationReceipt? receipt,
        DateTimeOffset now) =>
        receipt is not null &&
        receipt.TokenId == request.TokenId &&
        receipt.RecoveryId == request.RecoveryReference.RecoveryId &&
        receipt.ActionKey == request.ActionKey &&
        receipt.ActionVersion == request.ActionVersion &&
        receipt.IdempotencyKey == request.IdempotencyKey &&
        string.Equals(receipt.ContractHash, request.ContractHash, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(receipt.ReceiptReference) &&
        (string.IsNullOrWhiteSpace(request.ReceiptReference) ||
         string.Equals(receipt.ReceiptReference, request.ReceiptReference, StringComparison.Ordinal)) &&
        !string.IsNullOrWhiteSpace(receipt.Outcome) &&
        receipt.ObservedAt <= now;

    private KernelContinuationState ResolveContinuationRecovery(
        KernelContinuationState current,
        KernelContinuationReceipt? receipt,
        DateTimeOffset now)
    {
        if (current.ExecutionStage == ContinuationExecutionStage.BeforeTerminal &&
            current.CancellationRequestedAt is not null)
        {
            return CreateTerminalState(current, ContinuationState.Cancelled, now);
        }

        if (current.ExecutionStage == ContinuationExecutionStage.BeforeTerminal && current.ExpiresAt > now)
        {
            return current with
            {
                State = ContinuationState.Pending,
                ClaimedAt = null,
                ClaimOwner = null,
                LeaseExpiresAt = null,
                OutcomeCertainty = ActionOutcomeCertainty.Certain,
                RecoveryReference = null,
                Revision = current.Revision + 1
            };
        }

        if (current.ExecutionStage == ContinuationExecutionStage.BeforeTerminal)
            return CreateTerminalState(current, ContinuationState.Expired, now);

        var persistedOutcome = receipt?.Outcome ?? current.CompletedOutcome;
        if (receipt is not null ||
            (current.ExecutionStage is ContinuationExecutionStage.OutcomePersisted or
                ContinuationExecutionStage.DeliveryStarted &&
             !string.IsNullOrWhiteSpace(persistedOutcome)))
        {
            return current with
            {
                State = ContinuationState.Completed,
                CompletedAt = current.CompletedAt ?? now,
                CompletedOutcome = persistedOutcome,
                ExecutionStage = ContinuationExecutionStage.OutcomePersisted,
                OutcomeCertainty = ActionOutcomeCertainty.Certain,
                ReceiptReference = receipt?.ReceiptReference ?? current.ReceiptReference,
                RetainUntil = now + _retentionPeriod,
                Revision = current.Revision + 1
            };
        }

        return current with
        {
            State = ContinuationState.OutcomeUncertain,
            ClaimOwner = null,
            LeaseExpiresAt = null,
            OutcomeCertainty = ActionOutcomeCertainty.Uncertain,
            Revision = current.Revision + 1
        };
    }

    private KernelContinuationState CreateTerminalState(
        KernelContinuationState current,
        ContinuationState terminalState,
        DateTimeOffset now) =>
        current with
        {
            State = terminalState,
            CompletedAt = now,
            CompletedOutcome = terminalState switch
            {
                ContinuationState.Cancelled => "{\"kind\":\"cancelled\"}",
                ContinuationState.Expired => "{\"kind\":\"expired\"}",
                _ => throw new ArgumentOutOfRangeException(nameof(terminalState), terminalState, null)
            },
            ExecutionStage = ContinuationExecutionStage.OutcomePersisted,
            OutcomeCertainty = ActionOutcomeCertainty.Certain,
            RecoveryReference = null,
            RetainUntil = now + _retentionPeriod,
            DeliveryAcknowledgedAt = null,
            Revision = current.Revision + 1
        };

    private static bool IsTerminalDeliveryState(ContinuationState state) =>
        state is ContinuationState.Completed or ContinuationState.Cancelled or ContinuationState.Expired;

    private static bool IsCertainPreTerminalSafePoint(KernelContinuationState state) =>
        state.ExecutionStage == ContinuationExecutionStage.BeforeTerminal &&
        state.OutcomeCertainty == ActionOutcomeCertainty.Certain &&
        string.IsNullOrWhiteSpace(state.ReceiptReference) &&
        string.IsNullOrWhiteSpace(state.CompletedOutcome);

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
        bool allowCompleted = false,
        bool allowCancelRequested = false,
        bool allowRecovery = false,
        bool allowExpired = false)
    {
        while (await ReadAsync(tokenId, cancellationToken) is { } current)
        {
            if (!IsCurrentClaim(current, secret, claim, now, requireLiveLease, allowExpired) ||
                (!allowRecovery && current.RecoveryReference is not null) ||
                (!allowCompleted && current.State != ContinuationState.Claimed &&
                 !(allowCancelRequested && current.State == ContinuationState.CancelRequested)))
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

    private static bool IsValidContinuationState(KernelContinuationState state)
    {
        var deleted = state.State == ContinuationState.Deleted;
        var recovery = state.RecoveryReference;
        return state.TokenId != Guid.Empty &&
               state.TokenHash.Length == 64 &&
               state.Request.ActionVersion > 0 &&
               state.Request.IdempotencyKey != Guid.Empty &&
               !string.IsNullOrWhiteSpace(state.Request.ContractHash) &&
               state.Request.Destination is { } destination &&
               !string.IsNullOrWhiteSpace(destination.Kind) &&
               state.ResultDestination == destination &&
               state.RetainUntil > state.CreatedAt &&
               (!deleted
                   ? !string.IsNullOrWhiteSpace(state.Request.ProtectedInput) &&
                     state.ProtectedInput == state.Request.ProtectedInput
                   : state.Request.ProtectedInput is null &&
                     state.ProtectedInput is null &&
                     state.CompletedOutcome is null &&
                     state.DeliveryAcknowledgedAt is not null) &&
               (state.State is not (
                    ContinuationState.Completed or
                    ContinuationState.Cancelled or
                    ContinuationState.Delivered or
                    ContinuationState.Expired) ||
                 !string.IsNullOrWhiteSpace(state.CompletedOutcome)) &&
               (state.ExecutionStage is not (
                    ContinuationExecutionStage.OutcomePersisted or
                    ContinuationExecutionStage.DeliveryStarted) ||
                 deleted ||
                 !string.IsNullOrWhiteSpace(state.CompletedOutcome)) &&
               (state.State is ContinuationState.Delivered or ContinuationState.Deleted
                    ? state.DeliveryAcknowledgedAt is not null
                    : state.DeliveryAcknowledgedAt is null) &&
               (state.State != ContinuationState.OutcomeUncertain || recovery is not null) &&
               (state.State != ContinuationState.CancelRequested || state.CancellationRequestedAt is not null) &&
               (recovery is null ||
                recovery.ActionKey == state.Request.ActionKey &&
                recovery.ActionVersion == state.Request.ActionVersion &&
                recovery.IdempotencyKey == state.Request.IdempotencyKey);
    }

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

internal sealed class NullKernelContinuationReceiptResolver : IKernelContinuationReceiptResolver
{
    public ValueTask<KernelContinuationReceipt?> FindAsync(
        KernelContinuationReceiptRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<KernelContinuationReceipt?>(null);
    }
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
