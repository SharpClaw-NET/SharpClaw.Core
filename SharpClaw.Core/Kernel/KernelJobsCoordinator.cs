using System.Collections.Concurrent;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

/// <summary>Coordinates canonical Jobs lifecycle state through the Core action graph.</summary>
public sealed class KernelJobsCoordinator
{
    private const string ClaimUnavailablePrefix = "JOBS_CLAIM_UNAVAILABLE:";
    private readonly KernelGraph _graph;
    private readonly KernelActionDispatcher _dispatcher;
    private readonly KernelJobsActionRunner _actionRunner;
    private readonly KernelJobsStore _store;
    private readonly IReadOnlyDictionary<string, IJobHandler> _handlers;
    private readonly ConcurrentDictionary<Guid, ActiveJobExecution> _activeExecutions = new();
    private readonly ConcurrentDictionary<Guid, ModuleStorageClaimAuthority> _claims = new();

    public KernelJobsCoordinator(
        KernelGraph graph,
        KernelActionDispatcher dispatcher,
        KernelJobsStore store,
        IEnumerable<IJobHandler> handlers)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(handlers);

        var map = new Dictionary<string, IJobHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (SharpClawActionCatalog.Jobs.Contains(handler.ActionKey))
                throw new ArgumentException(
                    $"The Jobs control key '{handler.ActionKey.Value}' cannot identify a workload handler.",
                    nameof(handlers));
            if (!_graph.ContainsAction(handler.ActionKey))
                throw new ArgumentException(
                    $"The workload action '{handler.ActionKey.Value}' is not registered in the compiled graph.",
                    nameof(handlers));
            if (!map.TryAdd(handler.ActionKey.Value, handler))
                throw new ArgumentException(
                    $"The Jobs handler '{handler.ActionKey.Value}' is registered more than once.",
                    nameof(handlers));
        }

        _handlers = map;
        _actionRunner = new KernelJobsActionRunner(_graph, _dispatcher);
    }

    private sealed class ActiveJobExecution
    {
        public CancellationTokenSource ControlCancellation { get; } = new();

        public CancellationTokenSource LeaseCancellation { get; } = new();

        public ModuleStorageClaimAuthority? Claim { get; set; }

        public Task? RenewalTask { get; set; }

        public Task? InFlightTask { get; set; }

        public JobAttemptDocument? Attempt { get; set; }

        public KernelActionExecutionContext? ExecutionContext { get; set; }

        public bool ControlRequested { get; set; }

        public JobStatus? RequestedStatus { get; set; }

        public bool ControlTransitionCompleted { get; set; }

        public TaskCompletionSource<bool> ControlTransitionSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CleanupStarted;
    }

    public async ValueTask<JobDocument> SubmitAsync<TInput>(
        JobSubmission<TInput> submission,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(executionContext);
        if (!SamePrincipal(submission.Caller, executionContext.Caller) ||
            !SameFeatures(submission.Features, executionContext.Features))
        {
            throw new KernelCapabilityException(
                "Jobs submission caller and features must match the host execution context.");
        }
        var handler = RequireInputHandler<TInput>(submission.ActionKey);
        var input = handler.EncodeInput(submission.Input!);
        var idempotencyKey = submission.IdempotencyKey ?? executionContext.IdempotencyKey;
        var existing = await FindIdempotentSubmissionAsync(
            idempotencyKey,
            submission,
            handler,
            input,
            executionContext,
            cancellationToken);
        if (existing is not null)
            return existing;
        var now = DateTimeOffset.UtcNow;
        var job = new JobDocument(
            Guid.NewGuid(),
            Guid.NewGuid(),
            idempotencyKey,
            submission.ConversationId,
            submission.ActionKey,
            executionContext.Caller,
            executionContext.Features,
            JobStatus.Pending,
            submission.Holds ?? [],
            now,
            null,
            null,
            null,
            ActionOutcomeCertainty.Certain,
            input);

        job = await RunFamilyAsync<KernelJobsOperationFamilies.Submit>(
            new SharpClawActionKey("jobs.submit"),
            job,
            static (current, _) => ValueTask.FromResult(current),
            executionContext,
            cancellationToken);
        job = await RunFamilyAsync<KernelJobsOperationFamilies.Validate>(
            new SharpClawActionKey("jobs.validate"),
            job,
            (current, _) =>
            {
                ValidateHandler(handler, current.Input);
                return ValueTask.FromResult(current);
            },
            executionContext,
            cancellationToken);
        job = await RunFamilyAsync<KernelJobsOperationFamilies.IdentityCreate>(
            new SharpClawActionKey("jobs.identity.create"),
            job,
            static (current, _) => ValueTask.FromResult(current),
            executionContext,
            cancellationToken);
        try
        {
            return await RunFamilyAsync<KernelJobsOperationFamilies.QueuePersist>(
                new SharpClawActionKey("jobs.queue.persist"),
                job,
                (current, ct) => TransitionJobAsync(
                    current,
                    current with { Status = JobStatus.Queued },
                    executionContext,
                    0,
                    ct),
                executionContext,
                cancellationToken);
        }
        catch (Exception exception) when (IsRevisionConflict(exception))
        {
            var raced = await FindIdempotentSubmissionAsync(
                idempotencyKey,
                submission,
                handler,
                input,
                executionContext,
                cancellationToken);
            if (raced is not null)
                return raced;
            throw;
        }
    }

    public async ValueTask<JobExecutionResult<TResult>> DispatchAsync<TResult>(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        var dispatchRecord = await GetJobAsync(jobId, executionContext, CancellationToken.None);
        if (dispatchRecord?.Value is not { } dispatchJob)
            throw new KernelActionExecutionException($"Jobs record '{jobId:D}' was not found.");
        EnsureOwner(dispatchJob, executionContext);
        if (cancellationToken.IsCancellationRequested)
        {
            var cancelled = await CancelAsync(jobId, executionContext, CancellationToken.None);
            return new JobExecutionResult<TResult>(
                cancelled,
                default,
                ActionOutcomeKind.Cancelled,
                new ExecutionError("JOBS_CANCELLED", "The Jobs dispatch was cancelled.", true));
        }

        JobExecutionResult<TResult>? dispatchResult = null;
        var dispatchCompleted = 0;
        await RunFamilyAsync<KernelJobsOperationFamilies.Dispatch>(
            new SharpClawActionKey("jobs.dispatch"),
            dispatchJob,
            async (current, ct) =>
            {
                dispatchResult = await DispatchCoreAsync<TResult>(jobId, executionContext, ct);
                Interlocked.Exchange(ref dispatchCompleted, 1);
                return dispatchResult.Job;
            },
            executionContext,
            cancellationToken,
            dispatchRecord.Revision);

        if (Volatile.Read(ref dispatchCompleted) == 0 || dispatchResult is null)
            throw new KernelActionExecutionException(
                $"Jobs dispatch '{jobId:D}' completed without executing its lifecycle terminal.");
        return dispatchResult;
    }

    private async ValueTask<JobExecutionResult<TResult>> DispatchCoreAsync<TResult>(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        var record = await GetJobAsync(jobId, executionContext, CancellationToken.None);
        if (record?.Value is not { } existing)
            throw new KernelActionExecutionException($"Jobs record '{jobId:D}' was not found.");
        EnsureOwner(existing, executionContext);

        var handler = RequireHandler<TResult>(existing.ActionKey);
        if (existing.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Expired)
            return await ReadCompletedResultAsync<TResult>(existing, handler, executionContext, cancellationToken);
        if (existing.Status is JobStatus.Held or JobStatus.Paused or JobStatus.OutcomeUncertain or JobStatus.Running)
            return new JobExecutionResult<TResult>(
                existing,
                default,
                existing.Status == JobStatus.OutcomeUncertain
                    ? ActionOutcomeKind.Uncertain
                    : ActionOutcomeKind.Deferred,
                new ExecutionError("JOBS_NOT_QUEUED", $"Jobs record '{jobId:D}' is in state '{existing.Status}'."));
        if (cancellationToken.IsCancellationRequested)
        {
            var cancelled = await CancelAsync(existing.Id, executionContext, CancellationToken.None);
            return new JobExecutionResult<TResult>(
                cancelled,
                default,
                ActionOutcomeKind.Cancelled,
                new ExecutionError("JOBS_CANCELLED", "The Jobs dispatch was cancelled.", true));
        }

        if (!_activeExecutions.TryAdd(existing.Id, new ActiveJobExecution()))
        {
            return new JobExecutionResult<TResult>(
                existing,
                default,
                ActionOutcomeKind.Deferred,
                new ExecutionError(
                    "JOBS_ALREADY_RUNNING",
                    "Another dispatcher owns the active Jobs execution."));
        }

        var active = _activeExecutions[existing.Id];
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            active.ControlCancellation.Token);
        cancellationToken = linkedCancellation.Token;

        JobAttemptDocument? attempt = null;
        var job = existing;
        var jobRevision = record.Revision;
        long? attemptRevision = null;
        var startClaimed = false;
        JobPayloadEnvelope? output = null;
        try
        {
            var attemptNumber = await NextAttemptNumberAsync(existing.Id, executionContext, cancellationToken);
            attempt = new JobAttemptDocument(
                Guid.NewGuid(),
                existing.Id,
                existing.InvocationId,
                existing.IdempotencyKey,
                attemptNumber,
                handler.Safety,
                null,
                DateTimeOffset.UtcNow,
                null,
                null);
            job = await RunFamilyAsync<KernelJobsOperationFamilies.Start>(
                new SharpClawActionKey("jobs.start"),
                existing,
                async (current, ct) =>
                {
                    var started = current with
                    {
                        Status = JobStatus.Running,
                        StartedAt = current.StartedAt ?? DateTimeOffset.UtcNow,
                        ActiveAttemptId = attempt!.AttemptId,
                    };
                    var claimed = await _store.ClaimJobAsync(
                        started,
                        attempt!,
                        jobRevision,
                        ct);
                    active.Claim = claimed.Authority;
                    active.Attempt = attempt;
                    active.ExecutionContext = executionContext;
                    _claims[jobId] = claimed.Authority;
                    active.RenewalTask = RenewClaimLoopAsync(
                        jobId,
                        active,
                        active.LeaseCancellation.Token);
                    return claimed.Job.Value!;
                },
                executionContext,
                cancellationToken,
                jobRevision);
            startClaimed = true;

            var startedRecord = await GetJobAsync(job.Id, executionContext, CancellationToken.None);
            if (startedRecord?.Value is not { } startedJob)
                throw new KernelActionExecutionException(
                    $"Jobs record '{job.Id:D}' disappeared after its start transition.");
            job = startedJob;
            jobRevision = startedRecord.Revision;

            var startedAttempt = await GetAttemptAsync(attempt!.AttemptId, executionContext, CancellationToken.None);
            if (startedAttempt is null)
                throw new KernelActionExecutionException(
                    $"Jobs attempt '{attempt!.AttemptId:D}' disappeared after its start transition.");
            attemptRevision = startedAttempt.Revision;

            job = await RunFamilyAsync<KernelJobsOperationFamilies.InterruptionCheck>(
                new SharpClawActionKey("jobs.interruption.check"),
                job,
                (current, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(current);
                },
                executionContext,
                cancellationToken,
                jobRevision);

            async ValueTask<JobDocument> ExecuteExternalCallAsync(
                JobDocument current,
                CancellationToken ct)
            {
                var handlerContext = new JobExecutionContext(
                    current,
                    attempt!,
                    current.Caller,
                    current.Features);
                var handlerTask = handler.ExecuteAsync(handlerContext, current.Input, ct).AsTask();
                active.InFlightTask = handlerTask;
                try
                {
                    output = await handlerTask;
                    return current;
                }
                finally
                {
                    active.InFlightTask = null;
                }
            }

            job = await RunFamilyAsync<KernelJobsOperationFamilies.HandlerInvoke>(
                new SharpClawActionKey("jobs.handler.invoke"),
                job,
                async (current, ct) =>
                {
                    if (handler.Safety == JobExecutionSafety.Receipted)
                    {
                        current = await RunFamilyAsync<KernelJobsOperationFamilies.ExternalEffectPrepare>(
                            new SharpClawActionKey("jobs.external_effect.prepare"),
                            current,
                            static (prepared, _) => ValueTask.FromResult(prepared),
                            executionContext,
                            ct,
                            jobRevision);
                    }

                    current = handler.Safety == JobExecutionSafety.NonIdempotent
                        ? await RunFamilyAsync<KernelJobsOperationFamilies.IrreversibleEffect>(
                            new SharpClawActionKey("jobs.irreversible_effect"),
                            current,
                            ExecuteExternalCallAsync,
                            executionContext,
                            ct,
                            jobRevision)
                        : await RunFamilyAsync<KernelJobsOperationFamilies.ExternalCall>(
                            new SharpClawActionKey("jobs.external_call"),
                            current,
                            ExecuteExternalCallAsync,
                            executionContext,
                            ct,
                            jobRevision);

                    if (output is null)
                        throw new KernelActionExecutionException(
                            $"Jobs handler '{handler.ActionKey.Value}' completed without a result payload.");
                    if (handler.Safety == JobExecutionSafety.Receipted)
                    {
                        current = await RunFamilyAsync<KernelJobsOperationFamilies.ExternalEffectReceipt>(
                            new SharpClawActionKey("jobs.external_effect.receipt"),
                            current,
                            async (receipted, receiptCt) =>
                            {
                                var receipt = attempt!;
                                await SaveAttemptAsync(
                                    receipt,
                                    executionContext,
                                    receiptCt,
                                    attemptRevision,
                                    active.Claim);
                                attempt = receipt;
                                if (!await RenewActiveClaimAsync(receipt.JobId, receiptCt))
                                    throw new KernelActionExecutionException(
                                        $"Jobs attempt '{receipt.AttemptId:D}' lost its storage claim after receipt persistence.");
                                var receiptRecord = await GetAttemptAsync(
                                    receipt.AttemptId,
                                    executionContext,
                                    CancellationToken.None);
                                attemptRevision = receiptRecord?.Revision ?? attemptRevision;
                                var jobRecord = await GetJobAsync(
                                    receipt.JobId,
                                    executionContext,
                                    CancellationToken.None);
                                if (jobRecord is not null)
                                {
                                    jobRevision = jobRecord.Revision;
                                    job = jobRecord.Value!;
                                }
                                return receipted;
                            },
                            executionContext,
                            ct,
                            jobRevision);
                    }

                    var resultReference = new JobResultReference(
                        output.ContractName,
                        output.SchemaVersion,
                        current.Id.ToString("N"),
                        "application/json",
                        output.Value.Length,
                        null);
                    current = current with { Result = resultReference };
                    return await RunFamilyAsync<KernelJobsOperationFamilies.ArtifactSeal>(
                        new SharpClawActionKey("jobs.artifact.seal"),
                        current,
                        static (sealedJob, _) => ValueTask.FromResult(sealedJob),
                        executionContext,
                        ct,
                        jobRevision);
                },
                executionContext,
                cancellationToken,
                jobRevision);

            job = await RunFamilyAsync<KernelJobsOperationFamilies.Complete>(
                new SharpClawActionKey("jobs.complete"),
                job,
                async (current, ct) =>
                {
                    var completed = current with
                    {
                        Status = JobStatus.Completed,
                        CompletedAt = DateTimeOffset.UtcNow,
                        OutcomeCertainty = ActionOutcomeCertainty.Certain,
                    };
                    if (output is null)
                        throw new KernelActionExecutionException(
                            $"Jobs handler '{handler.ActionKey.Value}' completed without a result payload.");
                    if (!await RenewActiveClaimAsync(current.Id, ct))
                        throw new KernelActionExecutionException(
                            $"Jobs execution '{current.Id:D}' lost its storage claim before completion.");
                    await _store.CommitExecutionAsync(
                        completed,
                        attempt! with { FinishedAt = completed.CompletedAt },
                        output,
                        jobRevision,
                        active.Claim,
                        ct);
                    var completedRecord = await GetJobAsync(
                        completed.Id,
                        executionContext,
                        CancellationToken.None);
                    return completedRecord?.Value ?? completed;
                },
                executionContext,
                cancellationToken,
                jobRevision);

            if (output is null)
                throw new KernelActionExecutionException(
                    $"Jobs handler '{handler.ActionKey.Value}' completed without a result payload.");

            return new JobExecutionResult<TResult>(
                job,
                (TResult)handler.DecodeResult(output),
                ActionOutcomeKind.Completed);
        }
        catch (Exception exception) when (!startClaimed && IsRevisionConflict(exception))
        {
            var competingRecord = await GetJobAsync(jobId, executionContext, CancellationToken.None);
            if (competingRecord?.Value is { } competingJob &&
                attempt is not null &&
                competingJob.ActiveAttemptId != attempt.AttemptId &&
                competingJob.Status is JobStatus.Running or JobStatus.Completed)
            {
                return new JobExecutionResult<TResult>(
                    competingJob,
                    default,
                    ActionOutcomeKind.Deferred,
                    new ExecutionError(
                        "JOBS_ALREADY_RUNNING",
                        "Another dispatcher owns the active Jobs attempt."));
            }
            throw new KernelActionExecutionException(
                $"Jobs start could not claim '{jobId:D}' because its revision changed: {exception.Message}");
        }
        catch (KernelActionCancelledException exception)
        {
            var cancelled = await FinalizeCancellationAsync(job, executionContext);
            return new JobExecutionResult<TResult>(
                cancelled,
                default,
                ActionOutcomeKind.Cancelled,
                exception.Error);
        }
        catch (ActionOutcomeUncertainException exception)
        {
            var uncertain = await FinalizeUncertaintyAsync(job, executionContext, exception.Uncertainty);
            return new JobExecutionResult<TResult>(
                uncertain,
                default,
                ActionOutcomeKind.Uncertain,
                new ExecutionError("JOBS_OUTCOME_UNCERTAIN", exception.Message),
                exception.Uncertainty);
        }
        catch (KernelActionDeferredException exception)
        {
            var held = await FinalizeHeldAsync(job, executionContext);
            return new JobExecutionResult<TResult>(
                held,
                default,
                ActionOutcomeKind.Deferred,
                new ExecutionError("JOBS_DEFERRED", exception.Message));
        }
        catch (OperationCanceledException exception)
        {
            var cancelled = await FinalizeCancellationAsync(job, executionContext);
            return new JobExecutionResult<TResult>(
                cancelled,
                default,
                ActionOutcomeKind.Cancelled,
                new ExecutionError("JOBS_CANCELLED", exception.Message, true));
        }
        catch (KernelActionFailedException exception)
        {
            var failed = await FinalizeFailureAsync(job, executionContext, exception.Error);
            return new JobExecutionResult<TResult>(
                failed,
                default,
                ActionOutcomeKind.Failed,
                exception.Error);
        }
        catch (Exception exception)
        {
            var error = new ExecutionError("JOBS_FAILED", exception.Message);
            var failed = await FinalizeFailureAsync(job, executionContext, error);
            return new JobExecutionResult<TResult>(failed, default, ActionOutcomeKind.Failed, error);
        }
        finally
        {
            active.ControlCancellation.Cancel();
            if (active.InFlightTask is { } inFlight && !inFlight.IsCompleted)
            {
                _ = FinishInFlightExecutionAsync(
                    jobId,
                    active,
                    inFlight,
                    attempt,
                    executionContext);
            }
            else if (active.ControlRequested && !active.ControlTransitionCompleted)
            {
                // The control operation still owns the claim and must commit its state.
            }
            else
            {
                await CleanupActiveExecutionAsync(jobId, active, attempt, executionContext);
            }
        }
    }

    public async ValueTask<JobDocument> ReportProgressAsync(
        JobProgress progress,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(executionContext);
        var record = await GetJobAsync(progress.JobId, executionContext, cancellationToken);
        if (record?.Value is not { } job)
            throw new KernelActionExecutionException($"Jobs record '{progress.JobId:D}' was not found.");
        EnsureOwner(job, executionContext);

        return await RunFamilyAsync<KernelJobsOperationFamilies.ProgressReport>(
            new SharpClawActionKey("jobs.progress.report"),
            job,
            async (current, ct) =>
            {
                await SaveProgressAsync(progress, executionContext, ct);
                return current;
            },
            executionContext,
            cancellationToken,
            record.Revision);
    }

    public async ValueTask<JobDocument> CancelAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        var cancelRecord = await GetJobAsync(jobId, executionContext, CancellationToken.None);
        if (cancelRecord?.Value is not { } cancelJob)
            throw new KernelActionExecutionException($"Jobs record '{jobId:D}' was not found.");
        EnsureOwner(cancelJob, executionContext);

        JobDocument? cancelled = null;
        var cancelCompleted = 0;
        await RunFamilyAsync<KernelJobsOperationFamilies.Cancel>(
            new SharpClawActionKey("jobs.cancel"),
            cancelJob,
            async (current, ct) =>
            {
                cancelled = await CancelCoreAsync(jobId, executionContext, ct);
                Interlocked.Exchange(ref cancelCompleted, 1);
                return cancelled;
            },
            executionContext,
            cancellationToken,
            cancelRecord.Revision);
        if (Volatile.Read(ref cancelCompleted) == 0 || cancelled is null)
            throw new KernelActionExecutionException(
                $"Jobs cancellation '{jobId:D}' completed without executing its lifecycle terminal.");
        return cancelled;
    }

    private async ValueTask<JobDocument> CancelCoreAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        await FenceActiveExecutionAsync(jobId, JobStatus.Cancelled);
        var record = await GetJobAsync(jobId, executionContext, cancellationToken);
        if (record?.Value is not { } job)
            throw new KernelActionExecutionException($"Jobs record '{jobId:D}' was not found.");
        EnsureOwner(job, executionContext);
        if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Expired)
            return job;

        var controlAuthority = GetActiveControlClaim(jobId);
        var controlledExecution = _activeExecutions.TryGetValue(jobId, out var activeExecution)
            ? activeExecution
            : null;
        if (job.Status == JobStatus.Running && controlAuthority is null)
        {
            record = await AcquireControlClaimAsync(record, executionContext, cancellationToken);
            controlAuthority = _claims.TryGetValue(jobId, out var cancelAuthority)
                ? cancelAuthority
                : null;
        }
        if (record.Value is not { } current)
            throw new KernelActionExecutionException($"Jobs record '{jobId:D}' was not found.");
        try
        {
            var requested = await RunFamilyAsync<KernelJobsOperationFamilies.CancelRequest>(
                new SharpClawActionKey("jobs.cancel.request"),
                current,
                static (current, _) => ValueTask.FromResult(current with { Status = JobStatus.Paused }),
                executionContext,
                cancellationToken,
                record.Revision);
            var cancelled = await RunFamilyAsync<KernelJobsOperationFamilies.CancelApply>(
                new SharpClawActionKey("jobs.cancel.apply"),
                requested,
                (current, ct) => TransitionJobAsync(
                    current,
                    current with
                    {
                        Status = JobStatus.Cancelled,
                        CompletedAt = DateTimeOffset.UtcNow,
                        OutcomeCertainty = ActionOutcomeCertainty.Certain,
                    },
                    executionContext,
                    record.Revision,
                    ct,
                    controlAuthority),
                executionContext,
                cancellationToken,
                record.Revision);
            await CompleteControlTransitionAsync(jobId, controlledExecution);
            return cancelled;
        }
        catch
        {
            await CompleteControlTransitionAfterFailureAsync(jobId, controlledExecution);
            throw;
        }
        finally
        {
            await ReleaseControlClaimAsync(jobId, controlAuthority);
        }
    }

    public async ValueTask<JobDocument> RecoverAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        var record = await GetJobAsync(jobId, executionContext, cancellationToken);
        if (record?.Value is not { } job)
            throw new KernelActionExecutionException($"Jobs record '{jobId:D}' was not found.");
        EnsureOwner(job, executionContext);

        return await RunFamilyAsync<KernelJobsOperationFamilies.Recovery>(
            new SharpClawActionKey("jobs.recovery"),
            job,
            async (current, ct) =>
            {
                return await RunFamilyAsync<KernelJobsOperationFamilies.RecoveryScan>(
                    new SharpClawActionKey("jobs.recovery.scan"),
                    current,
                    async (scanned, scanCt) =>
                    {
                        if (scanned.Status == JobStatus.OutcomeUncertain ||
                            !HasRecoverableControlledAttempt(scanned))
                            return scanned;

                        if (await RenewActiveClaimAsync(scanned.Id, scanCt))
                        {
                            var renewed = await GetJobAsync(
                                scanned.Id,
                                executionContext,
                                CancellationToken.None);
                            return renewed?.Value ?? scanned;
                        }

                        ModuleDocumentRecord<JobDocument>? recoveryRecord;
                        try
                        {
                            var fresh = await GetJobAsync(
                                scanned.Id,
                                executionContext,
                                CancellationToken.None)
                                ?? throw new KernelActionExecutionException(
                                    $"Jobs record '{scanned.Id:D}' disappeared during recovery.");
                            recoveryRecord = fresh.Value!.Status == JobStatus.Running
                                ? await AcquireControlClaimAsync(fresh, executionContext, scanCt)
                                : await AcquireExpiredControlledClaimAsync(fresh, scanCt);
                        }
                        catch (KernelActionExecutionException exception)
                            when (exception.Message.StartsWith(
                                ClaimUnavailablePrefix,
                                StringComparison.Ordinal))
                        {
                            return scanned;
                        }
                        var recoveryAuthority = _claims.TryGetValue(scanned.Id, out var claim)
                            ? claim
                            : null;
                        try
                        {
                            var recovered = recoveryRecord!.Value!;
                            var recoveredJob = recovered.Status == JobStatus.Running
                                ? recovered with
                                {
                                    Status = JobStatus.OutcomeUncertain,
                                    OutcomeCertainty = ActionOutcomeCertainty.Uncertain,
                                    Error = new ExecutionError(
                                        "JOBS_RECOVERY_UNCERTAIN",
                                        "The active Jobs claim was recovered without a live execution."),
                                }
                                : recovered with
                                {
                                    ActiveAttemptId = null,
                                };
                            return await RunFamilyAsync<KernelJobsOperationFamilies.RecoveryClassify>(
                                new SharpClawActionKey("jobs.recovery.classify"),
                                recovered,
                                async (classified, classifyCt) => await TransitionJobAsync(
                                    classified,
                                    recoveredJob,
                                    executionContext,
                                    recoveryRecord.Revision,
                                    classifyCt,
                                    recoveryAuthority),
                                executionContext,
                                scanCt,
                                recoveryRecord.Revision);
                        }
                        finally
                        {
                            await ReleaseControlClaimAsync(scanned.Id, recoveryAuthority);
                        }
                    },
                    executionContext,
                    ct,
                    record.Revision);
            },
            executionContext,
            cancellationToken,
            record.Revision);
    }
    public async ValueTask<JobDocument?> GetAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        var record = await GetJobAsync(jobId, executionContext, cancellationToken);
        if (record?.Value is not { } job)
            return null;
        EnsureOwner(job, executionContext);
        return await RunFamilyAsync<KernelJobsOperationFamilies.Read>(
            new SharpClawActionKey("jobs.read"),
            job,
            static (current, _) => ValueTask.FromResult(current),
            executionContext,
            cancellationToken,
            record.Revision);
    }

    public async ValueTask<IReadOnlyList<JobDocument>> ListAsync(
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        var records = await RunStorageResultAsync<StorageListRequest, IReadOnlyList<ModuleDocumentRecord<JobDocument>>>(
            new SharpClawActionKey("storage.query"),
            new StorageListRequest(executionContext.Caller.SubjectId),
            (request, ct) => _store.ListJobRecordsAsync(request.CallerSubjectId, cancellationToken: ct),
            executionContext,
            cancellationToken);
        var visible = new List<JobDocument>();
        foreach (var record in records)
        {
            if (record.Value is not { } job || !IsOwner(job, executionContext))
                continue;
            var listed = await RunFamilyAsync<KernelJobsOperationFamilies.List>(
                new SharpClawActionKey("jobs.list"),
                job,
                static (current, _) => ValueTask.FromResult(current),
                executionContext,
                cancellationToken,
                record.Revision);
            visible.Add(listed);
        }
        return visible;
    }

    public async ValueTask<bool> DeleteAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        var record = await GetJobAsync(jobId, executionContext, cancellationToken);
        if (record?.Value is not { } job)
            return false;
        EnsureOwner(job, executionContext);
        if (job.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Expired))
            throw new KernelActionExecutionException(
                $"Jobs record '{jobId:D}' cannot be deleted in state '{job.Status}'.");

        var deleted = false;
        await RunFamilyAsync<KernelJobsOperationFamilies.Delete>(
            new SharpClawActionKey("jobs.delete"),
            job,
            async (current, ct) =>
            {
                deleted = await DeleteJobAsync(current.Id, executionContext, ct);
                return current;
            },
            executionContext,
            cancellationToken,
            record.Revision);
        return deleted;
    }

    public ValueTask<JobDocument> PauseAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default) =>
        ChangeStatusAsync<KernelJobsOperationFamilies.Pause>(
            jobId,
            JobStatus.Paused,
            "jobs.pause",
            executionContext,
            cancellationToken);

    public ValueTask<JobDocument> StopAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default) =>
        ChangeStatusAsync<KernelJobsOperationFamilies.Stop>(
            jobId,
            JobStatus.Cancelled,
            "jobs.stop",
            executionContext,
            cancellationToken,
            new ExecutionError("JOBS_STOPPED", "The Jobs operation was stopped.", true));

    public ValueTask<JobDocument> ResumeAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default) =>
        ChangeStatusAsync<KernelJobsOperationFamilies.Resume>(
            jobId,
            JobStatus.Queued,
            "jobs.resume",
            executionContext,
            cancellationToken,
            clearError: true);

    public ValueTask<JobDocument> ResolveHoldAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default) =>
        ChangeStatusAsync<KernelJobsOperationFamilies.HoldResolve>(
            jobId,
            JobStatus.Queued,
            "jobs.hold.resolve",
            executionContext,
            cancellationToken,
            clearError: true);

    public async ValueTask<JobDocument> RetryAsync<TResult>(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        var record = await GetJobAsync(jobId, executionContext, cancellationToken);
        if (record?.Value is not { } job)
            throw new KernelActionExecutionException($"Jobs record '{jobId:D}' was not found.");
        EnsureOwner(job, executionContext);
        var handler = RequireHandler<TResult>(job.ActionKey);
        if (job.Status is not (JobStatus.Failed or JobStatus.OutcomeUncertain or JobStatus.Paused))
            throw new KernelActionExecutionException(
                $"Jobs record '{jobId:D}' cannot be retried in state '{job.Status}'.");
        if (job.ActiveAttemptId is not null)
            throw new KernelActionExecutionException(
                $"JOBS_ACTIVE_ATTEMPT: Jobs record '{jobId:D}' has an active attempt and " +
                "cannot be retried before that attempt is fenced and released.");
        if (handler.Safety is JobExecutionSafety.NonIdempotent or JobExecutionSafety.Receipted)
            throw new KernelActionExecutionException(
                $"Jobs action '{handler.ActionKey.Value}' has no Core-owned external-effect " +
                "reconciliation authority for retry.");

        var evaluated = await RunFamilyAsync<KernelJobsOperationFamilies.RetryEvaluate>(
            new SharpClawActionKey("jobs.retry.evaluate"),
            job,
            (current, _) =>
            {
                return ValueTask.FromResult(current);
            },
            executionContext,
            cancellationToken,
            record.Revision);
        var scheduled = await RunFamilyAsync<KernelJobsOperationFamilies.RetrySchedule>(
            new SharpClawActionKey("jobs.retry.schedule"),
            evaluated,
            static (current, _) => ValueTask.FromResult(current),
            executionContext,
            cancellationToken,
            record.Revision);
        return await RunFamilyAsync<KernelJobsOperationFamilies.Retry>(
            new SharpClawActionKey("jobs.retry"),
            scheduled,
            (current, ct) => TransitionJobAsync(
                current,
                current with
                {
                    Status = JobStatus.Queued,
                    CompletedAt = null,
                    Error = null,
                    OutcomeCertainty = ActionOutcomeCertainty.Certain,
                    ActiveAttemptId = null,
                    Result = null,
                },
                executionContext,
                record.Revision,
                ct),
            executionContext,
            cancellationToken,
            record.Revision);
    }

    public async ValueTask<IReadOnlyList<JobProgress>> ReadProgressAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        var job = await RequireOwnedJobAsync(jobId, executionContext, cancellationToken);
        var ownedJob = job.Value!;
        IReadOnlyList<ModuleDocumentRecord<JobProgress>>? rows = null;
        await RunFamilyAsync<KernelJobsOperationFamilies.LogsRead>(
            new SharpClawActionKey("jobs.logs.read"),
            ownedJob,
            async (_, ct) =>
            {
                rows = await RunStorageResultAsync<StorageProgressListRequest, IReadOnlyList<ModuleDocumentRecord<JobProgress>>>(
                    new SharpClawActionKey("storage.query"),
                    new StorageProgressListRequest(ownedJob.Id),
                    (request, storageCt) => _store.ListProgressRecordsAsync(request.JobId, storageCt),
                    executionContext,
                    ct);
                return ownedJob;
            },
            executionContext,
            cancellationToken,
            job.Revision);
        return rows?.Where(row => row.Value is not null).Select(row => row.Value!).ToArray() ?? [];
    }

    public async ValueTask<IReadOnlyList<JobAttemptDocument>> ReadAttemptsAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        var job = await RequireOwnedJobAsync(jobId, executionContext, cancellationToken);
        var ownedJob = job.Value!;
        IReadOnlyList<ModuleDocumentRecord<JobAttemptDocument>>? rows = null;
        await RunFamilyAsync<KernelJobsOperationFamilies.AuditRead>(
            new SharpClawActionKey("jobs.audit.read"),
            ownedJob,
            async (_, ct) =>
            {
                rows = await RunStorageResultAsync<StorageAttemptListRequest, IReadOnlyList<ModuleDocumentRecord<JobAttemptDocument>>>(
                    new SharpClawActionKey("storage.query"),
                    new StorageAttemptListRequest(ownedJob.Id),
                    (request, storageCt) => _store.ListAttemptRecordsAsync(request.JobId, storageCt),
                    executionContext,
                    ct);
                return ownedJob;
            },
            executionContext,
            cancellationToken,
            job.Revision);
        return rows?.Where(row => row.Value is not null).Select(row => row.Value!).ToArray() ?? [];
    }

    public async ValueTask<JobPayloadEnvelope?> ReadArtifactAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        var job = await RequireOwnedJobAsync(jobId, executionContext, cancellationToken);
        var ownedJob = job.Value!;
        ModuleDocumentRecord<JobPayloadEnvelope>? result = null;
        await RunFamilyAsync<KernelJobsOperationFamilies.ArtifactRead>(
            new SharpClawActionKey("jobs.artifact.read"),
            ownedJob,
            async (_, ct) =>
            {
                result = await RunStorageResultAsync<StorageGetResultRequest, ModuleDocumentRecord<JobPayloadEnvelope>?>(
                    new SharpClawActionKey("storage.get"),
                    new StorageGetResultRequest(ownedJob.Id),
                    (request, storageCt) => _store.GetResultAsync(request.JobId, storageCt),
                    executionContext,
                    ct);
                return ownedJob;
            },
            executionContext,
            cancellationToken,
            job.Revision);
        return result?.Value;
    }

    public async ValueTask<JobDocument> DeliverEventAsync(
        JobProgress progress,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var job = await RequireOwnedJobAsync(progress.JobId, executionContext, cancellationToken);
        var ownedJob = job.Value!;
        return await RunFamilyAsync<KernelJobsOperationFamilies.EventDeliver>(
            new SharpClawActionKey("jobs.event.deliver"),
            ownedJob,
            async (current, ct) =>
            {
                await SaveProgressAsync(progress with { Code = "event.delivered" }, executionContext, ct);
                return current;
            },
            executionContext,
            cancellationToken,
            job.Revision);
    }

    private async ValueTask<JobExecutionResult<TResult>> ReadCompletedResultAsync<TResult>(
        JobDocument job,
        IJobHandler handler,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        if (job.Status != JobStatus.Completed || job.Result is null)
            return new JobExecutionResult<TResult>(
                job,
                default,
                job.Status == JobStatus.Cancelled ? ActionOutcomeKind.Cancelled : ActionOutcomeKind.Failed);

        var result = await RunStorageResultAsync<StorageGetResultRequest, ModuleDocumentRecord<JobPayloadEnvelope>?>(
            new SharpClawActionKey("storage.get"),
            new StorageGetResultRequest(job.Id),
            (request, ct) => _store.GetResultAsync(request.JobId, ct),
            executionContext,
            cancellationToken);
        return new JobExecutionResult<TResult>(
            job,
            result?.Value is null ? default : (TResult)handler.DecodeResult(result.Value),
            ActionOutcomeKind.Completed);
    }

    private async ValueTask<JobDocument> FinalizeFailureAsync(
        JobDocument job,
        KernelActionExecutionContext executionContext,
        ExecutionError error)
    {
        try
        {
            var record = await GetJobAsync(job.Id, executionContext, CancellationToken.None);
            if (record?.Value is not { } currentJob)
                throw new KernelActionExecutionException(
                    $"Jobs record '{job.Id:D}' disappeared during failure finalization.");
            if (currentJob.Status != JobStatus.Running ||
                currentJob.ActiveAttemptId != job.ActiveAttemptId ||
                IsControlRequested(job.Id))
            {
                return currentJob;
            }
            return await RunFamilyAsync<KernelJobsOperationFamilies.Fail>(
                new SharpClawActionKey("jobs.fail"),
                currentJob,
                (current, ct) => TransitionJobAsync(
                    current,
                    current with
                    {
                        Status = JobStatus.Failed,
                        CompletedAt = DateTimeOffset.UtcNow,
                        OutcomeCertainty = ActionOutcomeCertainty.Certain,
                        Error = error,
                    },
                    executionContext,
                    record.Revision,
                    ct),
                executionContext,
                CancellationToken.None,
                record.Revision);
        }
        catch (Exception finalizeException)
        {
            throw new KernelActionExecutionException(
                $"Jobs failure finalization failed after '{error.Code}': {finalizeException.Message}");
        }
    }

    private async ValueTask<JobDocument> FinalizeCancellationAsync(
        JobDocument job,
        KernelActionExecutionContext executionContext)
    {
        var current = await GetJobAsync(job.Id, executionContext, CancellationToken.None);
        if (current?.Value is not { } currentJob)
            throw new KernelActionExecutionException(
                $"Jobs record '{job.Id:D}' disappeared during cancellation finalization.");
        if (IsControlRequested(job.Id))
            return currentJob;
        if (currentJob.Status is not (JobStatus.Pending or JobStatus.Queued or JobStatus.Running))
            return currentJob;
        return await CancelAsync(job.Id, executionContext, CancellationToken.None);
    }

    private async ValueTask<JobDocument> FinalizeUncertaintyAsync(
        JobDocument job,
        KernelActionExecutionContext executionContext,
        ActionUncertainty uncertainty)
    {
        var record = await GetJobAsync(job.Id, executionContext, CancellationToken.None);
        if (record?.Value is not { } currentJob)
            throw new KernelActionExecutionException(
                $"Jobs record '{job.Id:D}' disappeared during uncertainty finalization.");
        if (IsControlRequested(job.Id))
            return currentJob;
        return await RunFamilyAsync<KernelJobsOperationFamilies.ExternalEffectUncertain>(
            new SharpClawActionKey("jobs.external_effect.uncertain"),
            currentJob,
            (current, ct) => TransitionJobAsync(
                current,
                current with
                {
                    Status = JobStatus.OutcomeUncertain,
                    OutcomeCertainty = ActionOutcomeCertainty.Uncertain,
                    Error = new ExecutionError(uncertainty.Code, uncertainty.Message),
                },
                executionContext,
                record.Revision,
                ct),
            executionContext,
            CancellationToken.None,
            record.Revision);
    }

    private async ValueTask<JobDocument> FinalizeHeldAsync(
        JobDocument job,
        KernelActionExecutionContext executionContext)
    {
        var record = await GetJobAsync(job.Id, executionContext, CancellationToken.None);
        if (record?.Value is not { } currentJob)
            throw new KernelActionExecutionException(
                $"Jobs record '{job.Id:D}' disappeared during hold finalization.");
        if (IsControlRequested(job.Id))
            return currentJob;
        return await RunFamilyAsync<KernelJobsOperationFamilies.HoldEvaluate>(
            new SharpClawActionKey("jobs.hold.evaluate"),
            currentJob,
            (current, ct) => TransitionJobAsync(
                current,
                current with { Status = JobStatus.Held },
                executionContext,
                record.Revision,
                ct),
            executionContext,
            CancellationToken.None,
            record.Revision);
    }

    private async ValueTask<JobDocument> TransitionJobAsync(
        JobDocument current,
        JobDocument next,
        KernelActionExecutionContext executionContext,
        long expectedRevision,
        CancellationToken cancellationToken,
        ModuleStorageClaimAuthority? authority = null,
        bool allowUnclaimedControlWrite = false)
    {
        var prepared = await RunFamilyAsync<KernelJobsOperationFamilies.StateTransitionPrepare>(
            new SharpClawActionKey("jobs.state.transition.prepare"),
            current,
            static (value, _) => ValueTask.FromResult(value),
            executionContext,
            cancellationToken,
            expectedRevision);
        var transitioned = await RunFamilyAsync<KernelJobsOperationFamilies.StateTransition>(
            new SharpClawActionKey("jobs.state.transition"),
            prepared,
            (_, _) => ValueTask.FromResult(next),
            executionContext,
            cancellationToken,
            expectedRevision);
        try
        {
            return await RunFamilyAsync<KernelJobsOperationFamilies.StateTransitionCommit>(
                new SharpClawActionKey("jobs.state.transition.commit"),
                transitioned,
                async (value, ct) =>
                {
                    await SaveJobAsync(
                        value,
                        executionContext,
                        ct,
                        expectedRevision,
                        authority,
                        allowUnclaimedControlWrite);
                    return value;
                },
                executionContext,
                cancellationToken,
                expectedRevision);
        }
        catch
        {
            try
            {
                await RunFamilyAsync<KernelJobsOperationFamilies.StateTransitionRollback>(
                    new SharpClawActionKey("jobs.state.transition.rollback"),
                    current,
                    static (value, _) => ValueTask.FromResult(value),
                    executionContext,
                    CancellationToken.None,
                    expectedRevision);
            }
            catch
            {
                // The original transition failure remains authoritative.
            }
            throw;
        }
    }

    private async ValueTask<JobDocument> ChangeStatusAsync<TFamily>(
        Guid jobId,
        JobStatus status,
        string key,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken,
        ExecutionError? error = null,
        bool clearError = false)
    {
        var record = await RequireOwnedJobAsync(jobId, executionContext, cancellationToken);
        if (record.Value is not { } ownedJob)
            throw new KernelActionExecutionException($"Jobs record '{jobId:D}' was not found.");

        if (status is JobStatus.Paused or JobStatus.Cancelled)
            await FenceActiveExecutionAsync(jobId, status);

        record = await RequireOwnedJobAsync(jobId, executionContext, cancellationToken);
        ownedJob = record.Value!;
        if (ownedJob.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Expired)
            return ownedJob;

        if (status is JobStatus.Queued && ownedJob.ActiveAttemptId is not null)
            throw new KernelActionExecutionException(
                $"JOBS_ACTIVE_ATTEMPT: Jobs record '{jobId:D}' cannot resume while attempt " +
                $"'{ownedJob.ActiveAttemptId:D}' still has durable execution authority.");

        var controlAuthority = GetActiveControlClaim(jobId);
        var controlledExecution = _activeExecutions.TryGetValue(jobId, out var activeExecution)
            ? activeExecution
            : null;
        if (ownedJob.Status == JobStatus.Running &&
            (status is JobStatus.Paused or JobStatus.Cancelled) &&
            controlAuthority is null)
        {
            record = await AcquireControlClaimAsync(record, executionContext, cancellationToken);
            controlAuthority = _claims.TryGetValue(jobId, out var acquiredAuthority)
                ? acquiredAuthority
                : null;
        }

        ownedJob = record.Value!;
        if (!IsAllowedStatusTransition(ownedJob.Status, status))
            throw new KernelActionExecutionException(
                $"Jobs record '{jobId:D}' cannot change from '{ownedJob.Status}' to '{status}'.");

        try
        {
            var transitioned = await RunFamilyAsync<TFamily>(
                new SharpClawActionKey(key),
                ownedJob,
                (current, ct) => TransitionJobAsync(
                    current,
                    current with
                    {
                        Status = status,
                        CompletedAt = status == JobStatus.Cancelled ? DateTimeOffset.UtcNow : null,
                        Error = clearError ? null : error ?? current.Error,
                        OutcomeCertainty = status == JobStatus.OutcomeUncertain
                            ? ActionOutcomeCertainty.Uncertain
                            : ActionOutcomeCertainty.Certain,
                    },
                    executionContext,
                    record.Revision,
                    ct,
                    controlAuthority,
                    allowUnclaimedControlWrite: true),
                executionContext,
                cancellationToken,
                record.Revision);
            if (status is (JobStatus.Paused or JobStatus.Cancelled))
            {
                await CompleteControlTransitionAsync(jobId, controlledExecution);
            }
            return transitioned;
        }
        catch
        {
            await CompleteControlTransitionAfterFailureAsync(jobId, controlledExecution);
            throw;
        }
        finally
        {
            await ReleaseControlClaimAsync(jobId, controlAuthority);
        }
    }

    private static bool IsAllowedStatusTransition(JobStatus current, JobStatus next) =>
        next switch
        {
            JobStatus.Paused => current is JobStatus.Pending or JobStatus.Queued or JobStatus.Running,
            JobStatus.Cancelled => current is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Expired),
            JobStatus.Queued => current is JobStatus.Paused or JobStatus.Held,
            _ => true,
        };

    private ValueTask FenceActiveExecutionAsync(
        Guid jobId,
        JobStatus? requestedStatus = null)
    {
        if (!_activeExecutions.TryGetValue(jobId, out var active))
            return ValueTask.CompletedTask;

        active.ControlRequested = true;
        active.RequestedStatus = requestedStatus;
        active.ControlCancellation.Cancel();
        return ValueTask.CompletedTask;
    }

    private async ValueTask ReleaseControlClaimAsync(
        Guid jobId,
        ModuleStorageClaimAuthority? authority)
    {
        if (authority is null)
            return;
        if (_activeExecutions.TryGetValue(jobId, out var active) &&
            active.Claim is { } activeClaim &&
            activeClaim.Matches(authority))
        {
            return;
        }
        if (!_claims.TryGetValue(jobId, out var current) || !current.Matches(authority))
            return;
        try
        {
            await _store.RecoverJobClaimAsync(
                authority,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }
        catch (ModuleStorageContractException)
        {
            // Storage already fenced or released this control claim.
        }
        if (_claims.TryGetValue(jobId, out var remaining) && remaining.Matches(authority))
            _claims.TryRemove(jobId, out _);
    }

    private async Task FinishInFlightExecutionAsync(
        Guid jobId,
        ActiveJobExecution active,
        Task inFlight,
        JobAttemptDocument? attempt,
        KernelActionExecutionContext executionContext)
    {
        var cleanupSucceeded = false;
        try
        {
            while (!inFlight.IsCompleted && active.Claim is not null)
            {
                var claim = active.Claim;
                var remaining = claim.LeaseExpiresAt - DateTimeOffset.UtcNow;
                var delay = TimeSpan.FromTicks(Math.Clamp(
                    remaining.Ticks / 3,
                    TimeSpan.FromMilliseconds(50).Ticks,
                    TimeSpan.FromSeconds(30).Ticks));
                var completed = await Task.WhenAny(
                    inFlight,
                    Task.Delay(delay, CancellationToken.None));
                if (ReferenceEquals(completed, inFlight))
                    break;
                if (!await RenewActiveClaimAsync(jobId, CancellationToken.None))
                    break;
            }
            try
            {
                await inFlight;
            }
            catch
            {
                // The dispatch path already reported the authoritative outcome.
            }
            if (active.ControlRequested && !active.ControlTransitionCompleted)
                await active.ControlTransitionSignal.Task;
            cleanupSucceeded = true;
        }
        finally
        {
            if (cleanupSucceeded)
            {
                await CleanupActiveExecutionAsync(jobId, active, attempt, executionContext);
            }
        }
    }

    private async ValueTask ReleaseActiveClaimAsync(
        Guid jobId,
        ActiveJobExecution active)
    {
        if (active.Claim is not { } claim)
            return;
        try
        {
            await _store.RecoverJobClaimAsync(
                claim,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }
        catch (ModuleStorageContractException)
        {
            // Storage already fenced or released this execution claim.
        }
        if (_claims.TryGetValue(jobId, out var current) && current.Matches(claim))
            _claims.TryRemove(jobId, out _);
    }

    private async ValueTask ClearControlledAttemptAsync(
        Guid jobId,
        JobAttemptDocument? attempt,
        ActiveJobExecution active,
        KernelActionExecutionContext executionContext)
    {
        if (attempt is null || active.Claim is null)
            return;

        var record = await GetJobAsync(jobId, executionContext, CancellationToken.None);
        if (record?.Value is not { } current ||
            current.ActiveAttemptId != attempt.AttemptId ||
            current.Status is JobStatus.Pending or JobStatus.Queued or JobStatus.Running or
            JobStatus.Held or JobStatus.Completed)
        {
            return;
        }

        await SaveJobAsync(
            current with { ActiveAttemptId = null },
            executionContext,
            CancellationToken.None,
            record.Revision,
            active.Claim);
    }

    private async ValueTask CompleteControlTransitionAsync(
        Guid jobId,
        ActiveJobExecution? expectedActive = null)
    {
        if (expectedActive is null &&
            !_activeExecutions.TryGetValue(jobId, out expectedActive))
        {
            return;
        }

        if (expectedActive is null)
            return;
        var active = expectedActive;

        active.ControlTransitionCompleted = true;
        active.ControlTransitionSignal.TrySetResult(true);
        if (active.InFlightTask is { } inFlight && !inFlight.IsCompleted)
            return;

        await CleanupActiveExecutionAsync(
            jobId,
            active,
            active.Attempt,
            active.ExecutionContext
                ?? throw new KernelActionExecutionException(
                    $"Jobs execution '{jobId:D}' has no request context for cleanup."));
    }

    private async ValueTask CompleteControlTransitionAfterFailureAsync(
        Guid jobId,
        ActiveJobExecution? active)
    {
        if (active is null)
            return;

        try
        {
            await CompleteControlTransitionAsync(jobId, active);
        }
        catch
        {
            active.ControlTransitionCompleted = true;
            active.ControlTransitionSignal.TrySetResult(true);
        }
    }

    private async ValueTask CleanupActiveExecutionAsync(
        Guid jobId,
        ActiveJobExecution active,
        JobAttemptDocument? attempt,
        KernelActionExecutionContext executionContext)
    {
        if (active.Claim is not null &&
            !await RenewActiveClaimAsync(jobId, CancellationToken.None))
        {
            return;
        }

        if (Interlocked.Exchange(ref active.CleanupStarted, 1) != 0)
        {
            return;
        }

        try
        {
            await ClearControlledAttemptAsync(jobId, attempt, active, executionContext);
        }
        finally
        {
            active.LeaseCancellation.Cancel();
            if (active.RenewalTask is not null)
            {
                try
                {
                    await active.RenewalTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            try
            {
                await ReleaseActiveClaimAsync(jobId, active);
            }
            finally
            {
                RemoveActiveExecution(jobId, active);
            }
        }
    }

    private void RemoveActiveExecution(Guid jobId, ActiveJobExecution active)
    {
        if (_activeExecutions.TryGetValue(jobId, out var current) && ReferenceEquals(current, active))
            _activeExecutions.TryRemove(jobId, out _);
    }

    private static bool HasRecoverableControlledAttempt(JobDocument job) =>
        job.ActiveAttemptId is not null &&
        job.Status is JobStatus.Running or JobStatus.Paused or JobStatus.Held or
            JobStatus.Cancelled or JobStatus.Queued or JobStatus.Completed;

    private async ValueTask<ModuleDocumentRecord<JobDocument>> AcquireExpiredControlledClaimAsync(
        ModuleDocumentRecord<JobDocument> record,
        CancellationToken cancellationToken)
    {
        if (record.Value is not { } job || !HasRecoverableControlledAttempt(job) ||
            job.Status == JobStatus.Running)
        {
            return record;
        }

        try
        {
            var claimed = await _store.ClaimExistingJobAsync(
                job,
                record.Revision,
                cancellationToken);
            _claims[job.Id] = claimed.Authority;
            return claimed.Job;
        }
        catch (ModuleStorageContractException exception)
            when (exception.Failure.Code is ModuleStorageErrors.StaleClaim or ModuleStorageErrors.RevisionConflict)
        {
            throw new KernelActionExecutionException(
                $"{ClaimUnavailablePrefix} Jobs record '{job.Id:D}' is still controlled by another active execution.");
        }
    }

    private async ValueTask<ModuleDocumentRecord<JobDocument>> AcquireControlClaimAsync(
        ModuleDocumentRecord<JobDocument> record,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        if (record.Value is not { } job || job.Status != JobStatus.Running)
            return record;
        if (job.ActiveAttemptId is not { } attemptId)
            throw new KernelActionExecutionException(
                $"Running Jobs record '{job.Id:D}' has no active attempt.");

        var attempts = await ListAttemptRecordsAsync(job.Id, executionContext, cancellationToken);
        var attempt = attempts.FirstOrDefault(item => item.Value?.AttemptId == attemptId)?.Value;
        if (attempt is null)
            throw new KernelActionExecutionException(
                $"Running Jobs record '{job.Id:D}' has no durable active attempt.");

        try
        {
            var claimed = await _store.ClaimJobAsync(
                job,
                attempt,
                record.Revision,
                cancellationToken);
            _claims[job.Id] = claimed.Authority;
            return claimed.Job;
        }
        catch (ModuleStorageContractException exception)
            when (exception.Failure.Code is ModuleStorageErrors.StaleClaim or ModuleStorageErrors.RevisionConflict)
        {
            throw new KernelActionExecutionException(
                $"{ClaimUnavailablePrefix} Jobs record '{job.Id:D}' is still controlled by another active execution.");
        }
    }

    private async ValueTask<bool> RenewActiveClaimAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        if (!_activeExecutions.TryGetValue(jobId, out var active) || active.Claim is not { } claim)
            return false;

        try
        {
            var renewed = await _store.RenewJobClaimAsync(
                claim,
                DateTimeOffset.UtcNow.AddMinutes(5),
                cancellationToken);
            active.Claim = renewed;
            _claims[jobId] = renewed;
            return true;
        }
        catch (ModuleStorageContractException)
        {
            active.ControlRequested = true;
            active.ControlCancellation.Cancel();
            _claims.TryRemove(jobId, out _);
            return false;
        }
    }

    private async Task RenewClaimLoopAsync(
        Guid jobId,
        ActiveJobExecution active,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var claim = active.Claim;
            if (claim is null)
                return;

            var remaining = claim.LeaseExpiresAt - DateTimeOffset.UtcNow;
            var delayTicks = Math.Clamp(
                remaining.Ticks / 3,
                TimeSpan.FromSeconds(1).Ticks,
                TimeSpan.FromSeconds(30).Ticks);
            await Task.Delay(TimeSpan.FromTicks(delayTicks), cancellationToken);
            if (!await RenewActiveClaimAsync(jobId, cancellationToken))
                return;
        }
    }

    private async ValueTask<ModuleStorageClaimAuthority?> RenewClaimForWriteAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        if (_activeExecutions.TryGetValue(jobId, out var active))
        {
            if (active.ControlRequested)
                throw new KernelActionExecutionException(
                    $"Jobs execution '{jobId:D}' no longer owns its storage claim.");
            if (active.Claim is not { } activeClaim)
                return null;

            var renewedActive = await _store.RenewJobClaimAsync(
                activeClaim,
                DateTimeOffset.UtcNow.AddMinutes(5),
                cancellationToken);
            active.Claim = renewedActive;
            _claims[jobId] = renewedActive;
            return renewedActive;
        }

        if (!_claims.TryGetValue(jobId, out var claim))
            return null;

        var renewed = await _store.RenewJobClaimAsync(
            claim,
            DateTimeOffset.UtcNow.AddMinutes(5),
            cancellationToken);
        _claims[jobId] = renewed;
        return renewed;
    }

    private bool IsControlRequested(Guid jobId) =>
        _activeExecutions.TryGetValue(jobId, out var active) && active.ControlRequested;

    private ModuleStorageClaimAuthority? GetActiveControlClaim(Guid jobId) =>
        _activeExecutions.TryGetValue(jobId, out var active) &&
        active.ControlRequested
            ? active.Claim
            : null;

    private async ValueTask<ModuleDocumentRecord<JobDocument>> RequireOwnedJobAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        var record = await GetJobAsync(jobId, executionContext, cancellationToken);
        if (record?.Value is not { } job)
            throw new KernelActionExecutionException($"Jobs record '{jobId:D}' was not found.");
        EnsureOwner(job, executionContext);
        return record;
    }

    private ValueTask<IReadOnlyList<ModuleDocumentRecord<JobAttemptDocument>>> ListAttemptRecordsAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken) =>
        RunStorageResultAsync<StorageAttemptListRequest, IReadOnlyList<ModuleDocumentRecord<JobAttemptDocument>>>(
            new SharpClawActionKey("storage.query"),
            new StorageAttemptListRequest(jobId),
            (request, ct) => _store.ListAttemptRecordsAsync(request.JobId, ct),
            executionContext,
            cancellationToken);

    private async ValueTask<int> NextAttemptNumberAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        var attempts = await ListAttemptRecordsAsync(jobId, executionContext, cancellationToken);
        return attempts.Count == 0 ? 1 : attempts.Max(record => record.Value!.AttemptNumber) + 1;
    }

    private async ValueTask<JobDocument?> FindIdempotentSubmissionAsync<TInput>(
        Guid idempotencyKey,
        JobSubmission<TInput> submission,
        IJobHandler handler,
        JobPayloadEnvelope input,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        var records = await RunStorageResultAsync<StorageIdempotencyRequest, IReadOnlyList<ModuleDocumentRecord<JobDocument>>>(
            new SharpClawActionKey("storage.query"),
            new StorageIdempotencyRequest(idempotencyKey),
            (request, ct) => _store.FindJobRecordsByIdempotencyAsync(request.IdempotencyKey, ct),
            executionContext,
            cancellationToken);
        if (records.Count == 0)
            return null;
        if (records.Count > 1)
            throw new KernelActionExecutionException(
                $"Jobs idempotency key '{idempotencyKey:D}' identifies multiple records.");

        var existing = records[0].Value!;
        if (!SamePrincipal(existing.Caller, executionContext.Caller))
            throw new KernelCapabilityException(
                "The Jobs idempotency key belongs to another caller.");
        if (!SameFeatures(existing.Features, executionContext.Features) ||
            existing.ActionKey != submission.ActionKey ||
            existing.ConversationId != submission.ConversationId ||
            !string.Equals(existing.Input.ContractName, input.ContractName, StringComparison.Ordinal) ||
            existing.Input.SchemaVersion != input.SchemaVersion ||
            !string.Equals(existing.Input.Value, input.Value, StringComparison.Ordinal))
        {
            throw new KernelActionExecutionException(
                $"Jobs idempotency key '{idempotencyKey:D}' conflicts with the existing submission.");
        }
        ValidateHandler(handler, existing.Input);
        EnsureOwner(existing, executionContext);
        return existing;
    }

    private static void EnsureOwner(
        JobDocument job,
        KernelActionExecutionContext executionContext)
    {
        if (!IsOwner(job, executionContext))
            throw new KernelCapabilityException(
                $"Jobs record '{job.Id:D}' belongs to another caller.");
    }

    private static bool IsOwner(
        JobDocument job,
        KernelActionExecutionContext executionContext) =>
        SamePrincipal(job.Caller, executionContext.Caller) &&
        SameFeatures(job.Features, executionContext.Features);

    private ValueTask<ModuleDocumentRecord<JobDocument>?> GetJobAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken) =>
        RunStorageResultAsync<StorageGetRequest, ModuleDocumentRecord<JobDocument>?>(
            new SharpClawActionKey("storage.get"),
            new StorageGetRequest(jobId),
            (request, ct) => _store.GetJobAsync(request.JobId, ct),
            executionContext,
            cancellationToken);

    private ValueTask<ModuleDocumentRecord<JobAttemptDocument>?> GetAttemptAsync(
        Guid attemptId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken) =>
        RunStorageResultAsync<StorageGetAttemptRequest, ModuleDocumentRecord<JobAttemptDocument>?>(
            new SharpClawActionKey("storage.get"),
            new StorageGetAttemptRequest(attemptId),
            (request, ct) => _store.GetAttemptAsync(request.AttemptId, ct),
            executionContext,
            cancellationToken);

    private async ValueTask SaveJobAsync(
        JobDocument job,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken,
        long? expectedRevision = null,
        ModuleStorageClaimAuthority? authority = null,
        bool allowUnclaimedControlWrite = false)
    {
        var prepared = await RunFamilyAsync<KernelJobsOperationFamilies.PersistencePrepare>(
            new SharpClawActionKey("jobs.persistence.prepare"),
            job,
            static (current, _) => ValueTask.FromResult(current),
            executionContext,
            cancellationToken,
            expectedRevision ?? 0);
        try
        {
            var persisted = await RunFamilyAsync<KernelJobsOperationFamilies.Persistence>(
                new SharpClawActionKey("jobs.persistence"),
                prepared,
                async (current, ct) =>
                {
                    await RunStorageMutationAsync(
                        new StorageSaveJobRequest(current),
                        async (request, storageCt) => await _store.SaveJobAsync(
                            request.Job,
                            expectedRevision,
                            storageCt,
                            authority ?? (allowUnclaimedControlWrite
                                ? null
                                : await RenewClaimForWriteAsync(request.Job.Id, storageCt))),
                        executionContext,
                        ct);
                    return current;
                },
                executionContext,
                cancellationToken,
                expectedRevision ?? 0);
            _ = await RunFamilyAsync<KernelJobsOperationFamilies.PersistenceCommit>(
                new SharpClawActionKey("jobs.persistence.commit"),
                persisted,
                static (current, _) => ValueTask.FromResult(current),
                executionContext,
                cancellationToken,
                expectedRevision ?? 0);
        }
        catch
        {
            try
            {
                await RunFamilyAsync<KernelJobsOperationFamilies.PersistenceRollback>(
                    new SharpClawActionKey("jobs.persistence.rollback"),
                    prepared,
                    static (current, _) => ValueTask.FromResult(current),
                    executionContext,
                    CancellationToken.None,
                    expectedRevision ?? 0);
            }
            catch
            {
                // The write failure remains authoritative.
            }
            throw;
        }
    }

    private ValueTask SaveAttemptAsync(
        JobAttemptDocument attempt,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken,
        long? expectedRevision = null,
        ModuleStorageClaimAuthority? authority = null) =>
        RunStorageMutationAsync(
            new StorageSaveAttemptRequest(attempt),
            async (request, ct) => await _store.SaveAttemptAsync(
                request.Attempt,
                expectedRevision,
                ct,
                authority ?? await RenewClaimForWriteAsync(request.Attempt.JobId, ct)),
            executionContext,
            cancellationToken);

    private ValueTask SaveProgressAsync(
        JobProgress progress,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken) =>
        RunStorageMutationAsync(
            new StorageSaveProgressRequest(progress),
            async (request, ct) => await _store.SaveProgressAsync(
                request.Progress,
                ct,
                await RenewClaimForWriteAsync(request.Progress.JobId, ct)),
            executionContext,
            cancellationToken);

    private ValueTask<bool> DeleteJobAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken) =>
        DeleteStorageAsync(new StorageDeleteRequest(jobId), executionContext, cancellationToken);

    private async ValueTask<JobDocument> RunFamilyAsync<TFamily>(
        SharpClawActionKey key,
        JobDocument job,
        Func<JobDocument, CancellationToken, ValueTask<JobDocument>> terminal,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken,
        long expectedRevision = 0)
    {
        return await _actionRunner.RunAsync<TFamily>(
            key,
            job,
            terminal,
            executionContext,
            expectedRevision,
            cancellationToken: cancellationToken);
    }

    private async ValueTask RunStorageAsync<TRequest>(
        SharpClawActionKey key,
        TRequest request,
        Func<TRequest, CancellationToken, Task> effect,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        var descriptor = _graph.GetStandardAction(key);
        await _dispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
            executionContext,
            descriptor,
            new KernelActionEnvelope(key, request),
            async (envelope, ct) =>
            {
                if (envelope.Payload is not TRequest effective)
                    throw new KernelActionExecutionException(
                        $"Storage action '{key.Value}' returned an invalid request payload.");
                await effect(effective, ct);
                return true;
            },
            _graph.ActionSnapshot,
            cancellationToken);
    }

    private async ValueTask RunStorageMutationAsync<TRequest>(
        TRequest request,
        Func<TRequest, CancellationToken, Task> effect,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        await RunStorageAsync(
            new SharpClawActionKey("storage.upsert.prepare"),
            request,
            static (_, _) => Task.CompletedTask,
            executionContext,
            cancellationToken);
        await RunStorageAsync(
            new SharpClawActionKey("storage.upsert.commit"),
            request,
            effect,
            executionContext,
            cancellationToken);
    }

    private async ValueTask<bool> DeleteStorageAsync(
        StorageDeleteRequest request,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        await RunStorageAsync(
            new SharpClawActionKey("storage.delete.prepare"),
            request,
            static (_, _) => Task.CompletedTask,
            executionContext,
            cancellationToken);
        return await RunStorageResultAsync<StorageDeleteRequest, bool>(
            new SharpClawActionKey("storage.delete.commit"),
            request,
            async (effective, ct) =>
            {
                await _store.DeleteJobAsync(
                    effective.JobId,
                    cancellationToken: ct,
                    authority: _claims.TryGetValue(effective.JobId, out var authority)
                        ? authority
                        : null);
                return true;
            },
            executionContext,
            cancellationToken);
    }

    private async ValueTask<TResult> RunStorageResultAsync<TRequest, TResult>(
        SharpClawActionKey key,
        TRequest request,
        Func<TRequest, CancellationToken, Task<TResult>> effect,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        var descriptor = _graph.GetStandardAction(key);
        var result = await _dispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
            executionContext,
            descriptor,
            new KernelActionEnvelope(key, request),
            async (envelope, ct) =>
            {
                if (envelope.Payload is not TRequest effective)
                    throw new KernelActionExecutionException(
                        $"Storage action '{key.Value}' returned an invalid request payload.");
                return (object)(await effect(effective, ct))!;
            },
            _graph.ActionSnapshot,
            cancellationToken);
        return result is null ? default! : (TResult)result;
    }

    private IJobHandler RequireHandler<TInput, TResult>(
        SharpClawActionKey key,
        bool allowResultMismatch = false)
    {
        if (SharpClawActionCatalog.Jobs.Contains(key))
            throw new KernelActionExecutionException(
                $"Jobs control key '{key.Value}' cannot identify a workload handler.");
        if (!_graph.ContainsAction(key))
            throw new KernelActionExecutionException(
                $"Jobs workload action '{key.Value}' is not registered in the compiled graph.");
        if (!_handlers.TryGetValue(key.Value, out var handler))
            throw new KernelActionExecutionException(
                $"No Jobs workload handler is registered for '{key.Value}'.");
        if (handler.InputType != typeof(TInput) && !allowResultMismatch)
            throw new KernelActionExecutionException(
                $"Jobs action '{key.Value}' expects input '{handler.InputType.FullName}'.");
        return handler;
    }

    private IJobHandler RequireHandler<TResult>(SharpClawActionKey key)
    {
        var handler = RequireHandler<object, TResult>(key, allowResultMismatch: true);
        if (handler.ResultType != typeof(TResult))
            throw new KernelActionExecutionException(
                $"Jobs action '{key.Value}' returns '{handler.ResultType.FullName}'.");
        return handler;
    }

    private IJobHandler RequireInputHandler<TInput>(SharpClawActionKey key)
    {
        var handler = RequireHandler<object, object>(key, allowResultMismatch: true);
        if (handler.InputType != typeof(TInput))
            throw new KernelActionExecutionException(
                $"Jobs action '{key.Value}' expects input '{handler.InputType.FullName}'.");
        return handler;
    }

    private static void ValidateHandler(IJobHandler handler, JobPayloadEnvelope input)
    {
        if (!string.Equals(handler.InputContractName, input.ContractName, StringComparison.Ordinal) ||
            handler.InputSchemaVersion != input.SchemaVersion)
        {
            throw new KernelActionExecutionException(
                $"Jobs action '{handler.ActionKey.Value}' has an input contract mismatch.");
        }
    }

    private static bool IsRevisionConflict(Exception exception) =>
        exception is ModuleStorageContractException storageException
            ? string.Equals(storageException.Failure.Code, ModuleStorageErrors.RevisionConflict, StringComparison.Ordinal)
            : exception.ToString().Contains("stale revision", StringComparison.OrdinalIgnoreCase) ||
              exception.ToString().Contains(ModuleStorageErrors.RevisionConflict, StringComparison.OrdinalIgnoreCase) ||
              exception.ToString().Contains("aggregate revision", StringComparison.OrdinalIgnoreCase);

    private static bool SamePrincipal(RequestPrincipal left, RequestPrincipal right) =>
        string.Equals(left.SubjectId, right.SubjectId, StringComparison.Ordinal) &&
        string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) &&
        left.IsAuthenticated == right.IsAuthenticated &&
        (left.Roles ?? new HashSet<string>(StringComparer.Ordinal))
            .SetEquals(right.Roles ?? new HashSet<string>(StringComparer.Ordinal));

    private static bool SameFeatures(ExtensionFeatureSet left, ExtensionFeatureSet right) =>
        left.Items.Count == right.Items.Count &&
        left.Items.OrderBy(item => item.ContractName, StringComparer.Ordinal)
            .Zip(
                right.Items.OrderBy(item => item.ContractName, StringComparer.Ordinal),
                (leftItem, rightItem) =>
                    string.Equals(leftItem.ContractName, rightItem.ContractName, StringComparison.Ordinal) &&
                    leftItem.SchemaVersion == rightItem.SchemaVersion &&
                    string.Equals(leftItem.OwnerModuleId, rightItem.OwnerModuleId, StringComparison.Ordinal) &&
                    leftItem.MaxBytes == rightItem.MaxBytes &&
                    leftItem.Value.GetRawText() == rightItem.Value.GetRawText())
            .All(value => value);

    private sealed record StorageGetRequest(Guid JobId);
    private sealed record StorageGetAttemptRequest(Guid AttemptId);
    private sealed record StorageGetResultRequest(Guid JobId);
    private sealed record StorageListRequest(string? CallerSubjectId = null);
    private sealed record StorageIdempotencyRequest(Guid IdempotencyKey);
    private sealed record StorageAttemptListRequest(Guid JobId);
    private sealed record StorageProgressListRequest(Guid JobId);
    private sealed record StorageSaveJobRequest(JobDocument Job);
    private sealed record StorageSaveAttemptRequest(JobAttemptDocument Attempt);
    private sealed record StorageSaveProgressRequest(JobProgress Progress);
    private sealed record StorageDeleteRequest(Guid JobId);
}
