using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

/// <summary>Coordinates canonical Jobs lifecycle state through the Core action graph.</summary>
public sealed class KernelJobsCoordinator
{
    private readonly KernelGraph _graph;
    private readonly KernelActionDispatcher _dispatcher;
    private readonly KernelJobsActionRunner _actionRunner;
    private readonly KernelJobsStore _store;
    private readonly IReadOnlyDictionary<string, IJobHandler> _handlers;

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
            if (!map.TryAdd(handler.ActionKey.Value, handler))
                throw new ArgumentException(
                    $"The Jobs handler '{handler.ActionKey.Value}' is registered more than once.",
                    nameof(handlers));
        }

        _handlers = map;
        _actionRunner = new KernelJobsActionRunner(_graph, _dispatcher);
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
        var now = DateTimeOffset.UtcNow;
        var job = new JobDocument(
            Guid.NewGuid(),
            Guid.NewGuid(),
            submission.IdempotencyKey ?? executionContext.IdempotencyKey,
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
        return await RunFamilyAsync<KernelJobsOperationFamilies.QueuePersist>(
            new SharpClawActionKey("jobs.queue.persist"),
            job,
            async (current, ct) =>
            {
                var queued = current with { Status = JobStatus.Queued };
                await SaveJobAsync(queued, executionContext, ct);
                return queued;
            },
            executionContext,
            cancellationToken);
    }

    public async ValueTask<JobExecutionResult<TResult>> DispatchAsync<TResult>(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        var record = await GetJobAsync(jobId, executionContext, CancellationToken.None);
        if (record?.Value is not { } existing)
            throw new KernelActionExecutionException($"Jobs record '{jobId:D}' was not found.");

        var handler = RequireHandler<TResult>(existing.ActionKey);
        if (existing.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
            return await ReadCompletedResultAsync<TResult>(existing, handler, executionContext, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            var cancelled = await CancelAsync(existing.Id, executionContext, CancellationToken.None);
            return new JobExecutionResult<TResult>(
                cancelled,
                default,
                ActionOutcomeKind.Cancelled,
                new ExecutionError("JOBS_CANCELLED", "The Jobs dispatch was cancelled.", true));
        }

        var attempt = new JobAttemptDocument(
            Guid.NewGuid(),
            existing.Id,
            existing.InvocationId,
            existing.IdempotencyKey,
            1,
            handler.Safety,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            null);
        var job = existing;
        try
        {
            job = await RunFamilyAsync<KernelJobsOperationFamilies.Start>(
                new SharpClawActionKey("jobs.start"),
                existing,
                async (current, ct) =>
                {
                    var started = current with
                    {
                        Status = JobStatus.Running,
                        StartedAt = current.StartedAt ?? DateTimeOffset.UtcNow,
                        ActiveAttemptId = attempt.AttemptId,
                    };
                    await SaveAttemptAsync(attempt, executionContext, ct);
                    await SaveJobAsync(started, executionContext, ct);
                    return started;
                },
                executionContext,
                cancellationToken);

            job = await RunFamilyAsync<KernelJobsOperationFamilies.InterruptionCheck>(
                new SharpClawActionKey("jobs.interruption.check"),
                job,
                (current, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(current);
                },
                executionContext,
                cancellationToken);

            JobPayloadEnvelope? output = null;
            job = await RunFamilyAsync<KernelJobsOperationFamilies.HandlerInvoke>(
                new SharpClawActionKey("jobs.handler.invoke"),
                job,
                async (current, ct) =>
                {
                    var handlerContext = new JobExecutionContext(
                        current,
                        attempt,
                        current.Caller,
                        current.Features);
                    output = await handler.ExecuteAsync(handlerContext, current.Input, ct);
                    await SaveResultAsync(current.Id, output, executionContext, ct);
                    var resultReference = new JobResultReference(
                        output.ContractName,
                        output.SchemaVersion,
                        current.Id.ToString("N"),
                        "application/json",
                        output.Value.Length,
                        null);
                    return current with { Result = resultReference };
                },
                executionContext,
                cancellationToken);

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
                    await SaveAttemptAsync(
                        attempt with { FinishedAt = completed.CompletedAt },
                        executionContext,
                        ct);
                    await SaveJobAsync(completed, executionContext, ct);
                    return completed;
                },
                executionContext,
                cancellationToken);

            if (output is null)
                throw new KernelActionExecutionException(
                    $"Jobs handler '{handler.ActionKey.Value}' completed without a result payload.");

            return new JobExecutionResult<TResult>(
                job,
                (TResult)handler.DecodeResult(output),
                ActionOutcomeKind.Completed);
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

        return await RunFamilyAsync<KernelJobsOperationFamilies.ProgressReport>(
            new SharpClawActionKey("jobs.progress.report"),
            job,
            async (current, ct) =>
            {
                await SaveProgressAsync(progress, executionContext, ct);
                return current;
            },
            executionContext,
            cancellationToken);
    }

    public async ValueTask<JobDocument> CancelAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        var record = await GetJobAsync(jobId, executionContext, cancellationToken);
        if (record?.Value is not { } job)
            throw new KernelActionExecutionException($"Jobs record '{jobId:D}' was not found.");
        if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
            return job;

        var requested = await RunFamilyAsync<KernelJobsOperationFamilies.CancelRequest>(
            new SharpClawActionKey("jobs.cancel.request"),
            job,
            static (current, _) => ValueTask.FromResult(current with { Status = JobStatus.Paused }),
            executionContext,
            cancellationToken);
        return await RunFamilyAsync<KernelJobsOperationFamilies.CancelApply>(
            new SharpClawActionKey("jobs.cancel.apply"),
            requested,
            async (current, ct) =>
            {
                var cancelled = current with
                {
                    Status = JobStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    OutcomeCertainty = ActionOutcomeCertainty.Certain,
                };
                await SaveJobAsync(cancelled, executionContext, ct);
                return cancelled;
            },
            executionContext,
            cancellationToken);
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

        return await RunFamilyAsync<KernelJobsOperationFamilies.Recovery>(
            new SharpClawActionKey("jobs.recovery"),
            job,
            static (current, _) => ValueTask.FromResult(current),
            executionContext,
            cancellationToken);
    }

    public async ValueTask<JobDocument?> GetAsync(
        Guid jobId,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        var record = await GetJobAsync(jobId, executionContext, cancellationToken);
        return record?.Value;
    }

    public async ValueTask<IReadOnlyList<JobDocument>> ListAsync(
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        var records = await RunStorageResultAsync<StorageListRequest, IReadOnlyList<ModuleDocumentRecord<JobDocument>>>(
            new SharpClawActionKey("storage.list"),
            new StorageListRequest(),
            (_, ct) => _store.ListJobRecordsAsync(ct),
            executionContext,
            cancellationToken);
        return records.Where(record => record.Value is not null).Select(record => record.Value!).ToArray();
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
            cancellationToken);
        return deleted;
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
            return await RunFamilyAsync<KernelJobsOperationFamilies.Fail>(
                new SharpClawActionKey("jobs.fail"),
                job,
                async (current, ct) =>
                {
                    var failed = current with
                    {
                        Status = JobStatus.Failed,
                        CompletedAt = DateTimeOffset.UtcNow,
                        OutcomeCertainty = ActionOutcomeCertainty.Certain,
                        Error = error,
                    };
                    await SaveJobAsync(failed, executionContext, ct);
                    return failed;
                },
                executionContext,
                CancellationToken.None);
        }
        catch (Exception finalizeException)
        {
            throw new KernelActionExecutionException(
                $"Jobs failure finalization failed after '{error.Code}': {finalizeException.Message}");
        }
    }

    private ValueTask<JobDocument> FinalizeCancellationAsync(
        JobDocument job,
        KernelActionExecutionContext executionContext) =>
        CancelAsync(job.Id, executionContext, CancellationToken.None);

    private async ValueTask<JobDocument> FinalizeUncertaintyAsync(
        JobDocument job,
        KernelActionExecutionContext executionContext,
        ActionUncertainty uncertainty)
    {
        return await RunFamilyAsync<KernelJobsOperationFamilies.ExternalEffectUncertain>(
            new SharpClawActionKey("jobs.external_effect.uncertain"),
            job,
            async (current, ct) =>
            {
                var uncertain = current with
                {
                    Status = JobStatus.OutcomeUncertain,
                    OutcomeCertainty = ActionOutcomeCertainty.Uncertain,
                    Error = new ExecutionError(uncertainty.Code, uncertainty.Message),
                };
                await SaveJobAsync(uncertain, executionContext, ct);
                return uncertain;
            },
            executionContext,
            CancellationToken.None);
    }

    private async ValueTask<JobDocument> FinalizeHeldAsync(
        JobDocument job,
        KernelActionExecutionContext executionContext)
    {
        return await RunFamilyAsync<KernelJobsOperationFamilies.HoldEvaluate>(
            new SharpClawActionKey("jobs.hold.evaluate"),
            job,
            async (current, ct) =>
            {
                var held = current with { Status = JobStatus.Held };
                await SaveJobAsync(held, executionContext, ct);
                return held;
            },
            executionContext,
            CancellationToken.None);
    }

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

    private ValueTask SaveJobAsync(
        JobDocument job,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken) =>
        RunStorageMutationAsync(
            new StorageSaveJobRequest(job),
            (request, ct) => _store.SaveJobAsync(request.Job, cancellationToken: ct),
            executionContext,
            cancellationToken);

    private ValueTask SaveAttemptAsync(
        JobAttemptDocument attempt,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken) =>
        RunStorageMutationAsync(
            new StorageSaveAttemptRequest(attempt),
            (request, ct) => _store.SaveAttemptAsync(request.Attempt, cancellationToken: ct),
            executionContext,
            cancellationToken);

    private ValueTask SaveResultAsync(
        Guid jobId,
        JobPayloadEnvelope result,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken) =>
        RunStorageMutationAsync(
            new StorageSaveResultRequest(jobId, result),
            (request, ct) => _store.SaveResultAsync(request.JobId, request.Result, ct),
            executionContext,
            cancellationToken);

    private ValueTask SaveProgressAsync(
        JobProgress progress,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken) =>
        RunStorageMutationAsync(
            new StorageSaveProgressRequest(progress),
            (request, ct) => _store.SaveProgressAsync(request.Progress, ct),
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
        CancellationToken cancellationToken)
    {
        return await _actionRunner.RunAsync<TFamily>(
            key,
            job,
            terminal,
            executionContext,
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
            (effective, ct) => _store.DeleteJobAsync(effective.JobId, cancellationToken: ct),
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
        if (!SharpClawActionCatalog.Jobs.Contains(key))
            throw new KernelActionExecutionException(
                $"Jobs action '{key.Value}' is not a canonical Jobs root.");
        if (!_handlers.TryGetValue(key.Value, out var handler))
            throw new KernelActionExecutionException(
                $"No Jobs handler is registered for '{key.Value}'.");
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
    private sealed record StorageGetResultRequest(Guid JobId);
    private sealed record StorageListRequest;
    private sealed record StorageSaveJobRequest(JobDocument Job);
    private sealed record StorageSaveAttemptRequest(JobAttemptDocument Attempt);
    private sealed record StorageSaveResultRequest(Guid JobId, JobPayloadEnvelope Result);
    private sealed record StorageSaveProgressRequest(JobProgress Progress);
    private sealed record StorageDeleteRequest(Guid JobId);
}
