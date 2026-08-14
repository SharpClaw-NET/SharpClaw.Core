using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelCanonicalJobsTests
{
    [Fact]
    public void Canonical_jobs_module_compiles_without_host_domains()
    {
        var graph = CreateGraph();

        Assert.All(
            SharpClawActionCatalog.Jobs,
            key => Assert.True(graph.ContainsAction(key), $"Missing action '{key.Value}'."));
        Assert.Equal(
            [KernelJobsStorage.Jobs],
            graph.Modules.Storage.Select(contract => contract.StorageName).ToArray());
        Assert.DoesNotContain(typeof(KernelJobsCoordinator).Assembly.GetTypes(),
            type => type.Namespace?.Contains("SharpClaw.Application", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Jobs_control_keys_cannot_be_registered_as_workload_handlers()
    {
        var graph = CreateGraph();

        var exception = Assert.Throws<ArgumentException>(() => new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(new InMemoryJobsGateway()),
            [new ControlKeyHandler()]));

        Assert.Contains("cannot identify a workload handler", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submission_enforces_idempotency_and_job_owner_authority()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var coordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [new ReadHandler()]);
        var owner = CreateContext("owner");
        var sameKey = Guid.NewGuid();
        var submission = new JobSubmission<ReadRequest>(
            new SharpClawActionKey("tool.fetch"),
            new ReadRequest("same"),
            owner.Caller,
            owner.Features,
            IdempotencyKey: sameKey);

        var first = await coordinator.SubmitAsync(submission, owner);
        var second = await coordinator.SubmitAsync(submission, owner);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, gateway.Count(KernelJobsStorage.Jobs));

        var other = CreateContext("other");
        await Assert.ThrowsAsync<KernelCapabilityException>(async () =>
            await coordinator.GetAsync(first.Id, other));
        Assert.Empty(await coordinator.ListAsync(other));
    }

    [Fact]
    public async Task Concurrent_submission_uses_one_atomic_idempotency_record()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var context = CreateContext("atomic-owner");
        var coordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [new ReadHandler()]);
        var key = Guid.NewGuid();
        var submission = new JobSubmission<ReadRequest>(
            new SharpClawActionKey("tool.fetch"),
            new ReadRequest("atomic"),
            context.Caller,
            context.Features,
            IdempotencyKey: key);

        var jobs = await Task.WhenAll(
            coordinator.SubmitAsync(submission, context).AsTask(),
            coordinator.SubmitAsync(submission, context).AsTask());

        Assert.Equal(jobs[0].Id, jobs[1].Id);
        Assert.Equal(1, gateway.Count(KernelJobsStorage.Jobs));
    }

    [Fact]
    public async Task Concurrent_dispatch_claims_one_attempt_and_does_not_fail_the_winner()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var handler = new BlockingHandler();
        var coordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [handler]);
        var context = CreateContext("dispatcher");
        var job = await coordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.fetch"),
                new ReadRequest("concurrent"),
                context.Caller,
                context.Features),
            context);

        var firstTask = coordinator.DispatchAsync<ReadResult>(job.Id, context).AsTask();
        var firstOrStarted = await Task.WhenAny(
            firstTask,
            handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        if (firstOrStarted == firstTask)
            await firstTask;
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await coordinator.DispatchAsync<ReadResult>(job.Id, context);
        handler.Release.TrySetResult(true);
        var first = await firstTask;

        Assert.Equal(ActionOutcomeKind.Deferred, second.Outcome);
        Assert.Equal(ActionOutcomeKind.Completed, first.Outcome);
        Assert.Equal(1, gateway.CountAttempts());
        Assert.Equal(1, gateway.CountResults());
        Assert.Equal(1, gateway.ClaimCount);
        Assert.Equal(JobStatus.Completed, first.Job.Status);
    }

    [Fact]
    public async Task Concurrent_coordinators_use_one_storage_claim_for_one_job()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway
        {
            ClaimBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var firstHandler = new BlockingHandler();
        var secondHandler = new BlockingHandler();
        var firstCoordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [firstHandler]);
        var secondCoordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [secondHandler]);
        var context = CreateContext("cross-coordinator-owner");
        var job = await firstCoordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.fetch"),
                new ReadRequest("cross-coordinator"),
                context.Caller,
                context.Features),
            context);

        var firstDispatch = firstCoordinator.DispatchAsync<ReadResult>(job.Id, context).AsTask();
        var secondDispatch = secondCoordinator.DispatchAsync<ReadResult>(job.Id, context).AsTask();
        await gateway.WaitForClaimWaitersAsync(2);
        gateway.ClaimBarrier!.TrySetResult(true);

        var firstOrSecond = await Task.WhenAny(
            firstHandler.Started.Task,
            secondHandler.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
        if (firstOrSecond == firstHandler.Started.Task)
            firstHandler.Release.TrySetResult(true);
        else
            secondHandler.Release.TrySetResult(true);

        var results = await Task.WhenAll(firstDispatch, secondDispatch);

        Assert.Equal(1, results.Count(result => result.Outcome == ActionOutcomeKind.Completed));
        Assert.Equal(1, results.Count(result => result.Outcome == ActionOutcomeKind.Deferred));
        Assert.Equal(2, gateway.ClaimCount);
        Assert.Equal(1, gateway.CountAttempts());
        Assert.Equal(1, gateway.CountResults());
    }

    [Fact]
    public async Task Pause_fences_and_cancels_the_active_handler_before_resume()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var handler = new BlockingHandler();
        var context = CreateContext("control-owner");
        var coordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [handler]);
        var job = await coordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.fetch"),
                new ReadRequest("control"),
                context.Caller,
                context.Features),
            context);

        var dispatch = coordinator.DispatchAsync<ReadResult>(job.Id, context).AsTask();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var paused = await coordinator.PauseAsync(job.Id, context);
        var dispatchResult = await dispatch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(JobStatus.Paused, paused.Status);
        Assert.Equal(JobStatus.Paused, (await coordinator.GetAsync(job.Id, context))!.Status);
        Assert.NotEqual(ActionOutcomeKind.Completed, dispatchResult.Outcome);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((await coordinator.GetAsync(job.Id, context))!.ActiveAttemptId is null)
                break;
            await Task.Delay(20);
        }
        Assert.Null((await coordinator.GetAsync(job.Id, context))!.ActiveAttemptId);
        Assert.True(gateway.RecoverCount > 0, string.Join(" | ", gateway.CommitLog));

        Assert.Equal(JobStatus.Queued, (await coordinator.ResumeAsync(job.Id, context)).Status);
    }

    [Fact]
    public async Task Resume_does_not_start_a_second_handler_while_the_fenced_handler_is_still_running()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var handler = new NonCooperativeHandler();
        var context = CreateContext("non-cooperative-owner");
        var coordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [handler]);
        var job = await coordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.fetch"),
                new ReadRequest("non-cooperative"),
                context.Caller,
                context.Features),
            context);

        var first = coordinator.DispatchAsync<ReadResult>(job.Id, context).AsTask();
        await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(JobStatus.Paused, (await coordinator.PauseAsync(job.Id, context)).Status);
        var firstOutcome = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(ActionOutcomeKind.Completed, firstOutcome.Outcome);
        Assert.Equal(JobStatus.Paused, firstOutcome.Job.Status);

        var resumeException = await Assert.ThrowsAsync<KernelActionExecutionException>(
            () => coordinator.ResumeAsync(job.Id, context).AsTask());
        Assert.Contains("JOBS_ACTIVE_ATTEMPT", resumeException.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.InvocationCount);

        handler.ReleaseFirst.TrySetResult(true);
        await handler.FirstFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((await coordinator.GetAsync(job.Id, context))!.ActiveAttemptId is null)
                break;
            await Task.Delay(20);
        }
        Assert.True(
            (await coordinator.GetAsync(job.Id, context))!.ActiveAttemptId is null,
            $"Claims={gateway.ClaimCount}; Renews={gateway.RenewCount}; " +
            $"Recovers={gateway.RecoverCount}; {string.Join(" | ", gateway.CommitLog)}");
        Assert.Equal(JobStatus.Queued, (await coordinator.ResumeAsync(job.Id, context)).Status);

        JobExecutionResult<ReadResult>? completed = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            completed = await coordinator.DispatchAsync<ReadResult>(job.Id, context);
            if (completed.Outcome != ActionOutcomeKind.Deferred)
                break;
            await Task.Delay(20);
        }

        Assert.NotNull(completed);
        Assert.Equal(ActionOutcomeKind.Completed, completed!.Outcome);
        Assert.Equal(2, handler.InvocationCount);
        Assert.Equal(JobStatus.Completed, completed.Job.Status);
    }

    [Fact]
    public async Task Two_coordinators_cannot_resume_a_paused_job_while_the_old_attempt_is_running()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var handler = new NonCooperativeHandler();
        var firstCoordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [handler]);
        var secondCoordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [handler]);
        var context = CreateContext("cross-coordinator-control-owner");
        var job = await firstCoordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.fetch"),
                new ReadRequest("cross-coordinator-control"),
                context.Caller,
                context.Features),
            context);

        var first = firstCoordinator.DispatchAsync<ReadResult>(job.Id, context).AsTask();
        await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var paused = await firstCoordinator.PauseAsync(job.Id, context);
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JobStatus.Paused, paused.Status);
        Assert.Equal(JobStatus.Paused, firstResult.Job.Status);
        Assert.NotNull((await firstCoordinator.GetAsync(job.Id, context))!.ActiveAttemptId);

        var resumeException = await Assert.ThrowsAsync<KernelActionExecutionException>(
            () => secondCoordinator.ResumeAsync(job.Id, context).AsTask());
        Assert.Contains("JOBS_ACTIVE_ATTEMPT", resumeException.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.InvocationCount);

        handler.ReleaseFirst.TrySetResult(true);
        await handler.FirstFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((await firstCoordinator.GetAsync(job.Id, context))!.ActiveAttemptId is null)
                break;
            await Task.Delay(20);
        }

        Assert.True(
            (await firstCoordinator.GetAsync(job.Id, context))!.ActiveAttemptId is null,
            $"Claims={gateway.ClaimCount}; Renews={gateway.RenewCount}; " +
            $"Recovers={gateway.RecoverCount}; {string.Join(" | ", gateway.CommitLog)}");
        Assert.Equal(JobStatus.Queued, (await secondCoordinator.ResumeAsync(job.Id, context)).Status);
        var second = await secondCoordinator.DispatchAsync<ReadResult>(job.Id, context);

        Assert.Equal(ActionOutcomeKind.Completed, second.Outcome);
        Assert.Equal(2, handler.InvocationCount);
    }

    [Fact]
    public async Task Running_claim_is_renewed_and_recovered_before_control_transition()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway(TimeSpan.FromSeconds(6));
        var handler = new BlockingHandler();
        var context = CreateContext("claim-owner");
        var coordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [handler]);
        var job = await coordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.fetch"),
                new ReadRequest("claim"),
                context.Caller,
                context.Features),
            context);

        var dispatch = coordinator.DispatchAsync<ReadResult>(job.Id, context).AsTask();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(2_500));

        Assert.True(gateway.RenewCount > 0);
        Assert.Equal(JobStatus.Paused, (await coordinator.PauseAsync(job.Id, context)).Status);
        handler.Release.TrySetResult(true);
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gateway.RecoverCount > 0);
    }

    [Fact]
    public async Task Recovery_does_not_mark_a_job_uncertain_while_another_coordinator_holds_the_claim()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var handler = new BlockingHandler();
        var firstCoordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [handler]);
        var secondCoordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [new ReadHandler()]);
        var context = CreateContext("recovery-owner");
        var job = await firstCoordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.fetch"),
                new ReadRequest("live-claim"),
                context.Caller,
                context.Features),
            context);

        var dispatch = firstCoordinator.DispatchAsync<ReadResult>(job.Id, context).AsTask();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var recovered = await secondCoordinator.RecoverAsync(job.Id, context);

        Assert.Equal(JobStatus.Running, recovered.Status);
        handler.Release.TrySetResult(true);
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stop_fences_the_active_handler_and_preserves_cancelled_state()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var handler = new BlockingHandler();
        var context = CreateContext("stop-owner");
        var coordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [handler]);
        var job = await coordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.fetch"),
                new ReadRequest("stop"),
                context.Caller,
                context.Features),
            context);

        var dispatch = coordinator.DispatchAsync<ReadResult>(job.Id, context).AsTask();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = await coordinator.StopAsync(job.Id, context);
        var dispatchResult = await dispatch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(JobStatus.Cancelled, stopped.Status);
        Assert.Equal(JobStatus.Cancelled, (await coordinator.GetAsync(job.Id, context))!.Status);
        Assert.NotEqual(ActionOutcomeKind.Completed, dispatchResult.Outcome);
        Assert.True(gateway.RecoverCount > 0);
    }

    [Fact]
    public async Task Lifecycle_reads_recovery_controls_and_event_delivery_use_core_paths()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var context = CreateContext("lifecycle-user");
        var coordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [new ReadHandler()]);
        var job = await coordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.fetch"),
                new ReadRequest("lifecycle"),
                context.Caller,
                context.Features,
                IdempotencyKey: Guid.NewGuid()),
            context);

        Assert.Equal(job.Id, (await coordinator.GetAsync(job.Id, context))!.Id);
        Assert.Single(await coordinator.ListAsync(context));
        Assert.Equal(JobStatus.Paused, (await coordinator.PauseAsync(job.Id, context)).Status);
        Assert.Equal(JobStatus.Queued, (await coordinator.ResumeAsync(job.Id, context)).Status);
        Assert.Equal(JobStatus.Queued, (await coordinator.RecoverAsync(job.Id, context)).Status);

        await coordinator.ReportProgressAsync(
            new JobProgress(job.Id, null, "queued", "queued", 0),
            context);
        Assert.Single(await coordinator.ReadProgressAsync(job.Id, context));
        Assert.Empty(await coordinator.ReadAttemptsAsync(job.Id, context));
        Assert.Null(await coordinator.ReadArtifactAsync(job.Id, context));
        await coordinator.DeliverEventAsync(
            new JobProgress(job.Id, null, "ignored", "delivered"),
            context);
        Assert.Equal(2, gateway.CountProgress());

        Assert.Equal(JobStatus.Cancelled, (await coordinator.StopAsync(job.Id, context)).Status);
        Assert.True(await coordinator.DeleteAsync(job.Id, context));
        Assert.Empty(await coordinator.ListAsync(context));
        Assert.Equal(0, gateway.CountAttempts());
        Assert.Equal(0, gateway.CountResults());
        Assert.Equal(0, gateway.CountProgress());
    }

    [Fact]
    public async Task Typed_handlers_use_one_core_dispatcher_and_keep_request_authority()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var store = new KernelJobsStore(gateway);
        var coordinator = new KernelJobsCoordinator(
            graph,
            dispatcher,
            store,
            [new ReadHandler(), new ValidateHandler()]);
        var caller = new RequestPrincipal("jobs-user", Roles: new HashSet<string>(["operator"]));
        var features = ExtensionFeatureSet.Empty;
        var context = new KernelActionExecutionContext(
            caller,
            features,
            Guid.NewGuid(),
            Guid.NewGuid());
        var readIdempotencyKey = Guid.NewGuid();
        var validateIdempotencyKey = Guid.NewGuid();

        var readJob = await coordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.fetch"),
                new ReadRequest("read"),
                caller,
                features,
                IdempotencyKey: readIdempotencyKey),
            context);
        var validateJob = await coordinator.SubmitAsync(
            new JobSubmission<ValidateRequest>(
                new SharpClawActionKey("tool.validate"),
                new ValidateRequest("validate"),
                caller,
                features,
                IdempotencyKey: validateIdempotencyKey),
            context);

        var read = await coordinator.DispatchAsync<ReadResult>(readJob.Id, context);
        var validate = await coordinator.DispatchAsync<ValidateResult>(validateJob.Id, context);

        Assert.True(
            read.Outcome == ActionOutcomeKind.Completed,
            read.Error?.Message ?? read.Outcome.ToString());
        Assert.True(
            validate.Outcome == ActionOutcomeKind.Completed,
            validate.Error?.Message ?? validate.Outcome.ToString());
        Assert.Equal("read:jobs-user", read.Result!.Value);
        Assert.Equal("validate:jobs-user", validate.Result!.Value);
        Assert.Equal(JobStatus.Completed, read.Job.Status);
        Assert.Equal(JobStatus.Completed, validate.Job.Status);
        Assert.Equal(context.Caller.SubjectId, read.Job.Caller.SubjectId);
        Assert.Equal(context.Caller.SubjectId, validate.Job.Caller.SubjectId);
        Assert.Equal(readIdempotencyKey, read.Job.IdempotencyKey);
        Assert.Equal(validateIdempotencyKey, validate.Job.IdempotencyKey);
        Assert.Equal(2, gateway.Count(KernelJobsStorage.Jobs));
        Assert.Equal(2, gateway.CountResults());
        Assert.Equal(2, gateway.ExecutionCommitCount);
    }

    [Fact]
    public async Task Progress_cancellation_and_recovery_keep_job_state_in_Core_storage()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var store = new KernelJobsStore(gateway);
        var coordinator = new KernelJobsCoordinator(graph, dispatcher, store, [new ReadHandler()]);
        var context = CreateContext("jobs-user");
        var job = await coordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.fetch"),
                new ReadRequest("pending"),
                context.Caller,
                context.Features),
            context);

        var progress = await coordinator.ReportProgressAsync(
            new JobProgress(job.Id, null, "queued", "The job is queued", 0),
            context);
        var cancelled = await coordinator.CancelAsync(job.Id, context);
        var recovered = await coordinator.RecoverAsync(job.Id, context);

        Assert.Equal(JobStatus.Queued, progress.Status);
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.Equal(JobStatus.Cancelled, recovered.Status);
        Assert.Equal(1, gateway.CountProgress());
    }

    [Fact]
    public async Task Jobs_history_is_bounded_before_the_aggregate_reaches_storage_limit()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var store = new KernelJobsStore(gateway);
        var coordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            store,
            [new ReadHandler()]);
        var context = CreateContext("bounded-history-owner");
        var job = await coordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.fetch"),
                new ReadRequest("bounded-history"),
                context.Caller,
                context.Features),
            context);

        for (var index = 0; index < 200; index++)
        {
            await coordinator.ReportProgressAsync(
                new JobProgress(job.Id, null, "progress", $"progress-{index}", index),
                context);
        }

        for (var index = 0; index < 40; index++)
        {
            await store.SaveAttemptAsync(
                new JobAttemptDocument(
                    Guid.NewGuid(),
                    job.Id,
                    job.InvocationId,
                    job.IdempotencyKey,
                    index + 1,
                    JobExecutionSafety.Idempotent,
                    null,
                    DateTimeOffset.UtcNow,
                    null,
                    null));
        }

        Assert.Equal(32, gateway.CountAttempts());
        Assert.Equal(128, gateway.CountProgress());
        Assert.InRange(gateway.MaxStoredDocumentBytes, 1, 60_000);
        Assert.Equal(JobStatus.Cancelled, (await coordinator.StopAsync(job.Id, context)).Status);
    }

    [Fact]
    public async Task Submission_cannot_replace_host_caller_or_features()
    {
        var graph = CreateGraph();
        var coordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(new InMemoryJobsGateway()),
            [new ReadHandler()]);
        var context = CreateContext("trusted-user");
        var forgedCaller = new RequestPrincipal("forged-user");

        var exception = await Assert.ThrowsAsync<KernelCapabilityException>(async () =>
            await coordinator.SubmitAsync(
                new JobSubmission<ReadRequest>(
                    new SharpClawActionKey("tool.fetch"),
                    new ReadRequest("value"),
                    forgedCaller,
                    context.Features),
                context));

        Assert.Contains("must match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handler_failure_is_persisted_without_a_result_payload()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var coordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [new FailingHandler()]);
        var context = CreateContext("jobs-user");
        var job = await coordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.fail"),
                new ReadRequest("fail"),
                context.Caller,
                context.Features),
            context);

        var result = await coordinator.DispatchAsync<ReadResult>(job.Id, context);

        Assert.Equal(ActionOutcomeKind.Failed, result.Outcome);
        Assert.Equal(JobStatus.Failed, result.Job.Status);
        Assert.Null(result.Job.Result);
        Assert.Equal(0, gateway.CountResults());
    }

    [Fact]
    public async Task Receipted_handler_persists_receipt_and_result_under_the_active_claim()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var coordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [new ReceiptedHandler()]);
        var context = CreateContext("receipted-user");
        var job = await coordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.receipted"),
                new ReadRequest("receipt"),
                context.Caller,
                context.Features),
            context);

        var result = await coordinator.DispatchAsync<ReadResult>(job.Id, context);
        var attempts = await coordinator.ReadAttemptsAsync(job.Id, context);

        Assert.Equal(ActionOutcomeKind.Completed, result.Outcome);
        Assert.Equal(JobStatus.Completed, result.Job.Status);
        Assert.Single(attempts);
        Assert.Null(attempts[0].ReceiptId);
        Assert.Equal(1, gateway.CountResults());
        Assert.Equal(1, gateway.ExecutionCommitCount);
    }

    [Fact]
    public async Task Receipted_retry_is_rejected_without_external_reconciliation_authority()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var handler = new ReceiptedHandler();
        var store = new KernelJobsStore(gateway);
        var coordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            store,
            [handler]);
        var context = CreateContext("receipted-retry-owner");
        var job = await coordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.receipted"),
                new ReadRequest("receipt-no-retry"),
                context.Caller,
                context.Features),
            context);

        await coordinator.DispatchAsync<ReadResult>(job.Id, context);
        var attemptRecord = (await store.ListAttemptRecordsAsync(job.Id)).Single();
        await store.SaveAttemptAsync(
            attemptRecord.Value! with { ReceiptId = "external-provider-receipt" },
            attemptRecord.Revision);
        var failedRecord = await store.GetJobAsync(job.Id);
        await store.SaveJobAsync(
            failedRecord!.Value! with
            {
                Status = JobStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ActiveAttemptId = null,
                Result = null,
                Error = new ExecutionError("JOBS_SIMULATED_CRASH", "The test simulates a crash after receipt persistence."),
            },
            failedRecord.Revision);
        var executionCommitsBeforeRetry = gateway.ExecutionCommitCount;

        var retryException = await Assert.ThrowsAsync<KernelActionExecutionException>(
            () => coordinator.RetryAsync<ReadResult>(job.Id, context).AsTask());
        Assert.Contains("reconciliation authority", retryException.Message, StringComparison.Ordinal);
        Assert.Equal(executionCommitsBeforeRetry, gateway.ExecutionCommitCount);
    }

    [Fact]
    public async Task Cancelled_dispatch_persists_cancelled_state_before_handler_execution()
    {
        var graph = CreateGraph();
        var gateway = new InMemoryJobsGateway();
        var handler = new ReadHandler();
        var coordinator = new KernelJobsCoordinator(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            new KernelJobsStore(gateway),
            [handler]);
        var context = CreateContext("jobs-user");
        var job = await coordinator.SubmitAsync(
            new JobSubmission<ReadRequest>(
                new SharpClawActionKey("tool.fetch"),
                new ReadRequest("cancel"),
                context.Caller,
                context.Features),
            context);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await coordinator.DispatchAsync<ReadResult>(job.Id, context, cancellation.Token);

        Assert.Equal(ActionOutcomeKind.Cancelled, result.Outcome);
        Assert.Equal(JobStatus.Cancelled, result.Job.Status);
    }

    private static KernelActionExecutionContext CreateContext(string subject) =>
        new(
            new RequestPrincipal(subject, Roles: new HashSet<string>(["operator"])),
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());

    private static KernelGraph CreateGraph()
    {
        var registry = new KernelModuleRegistry();
        var jobs = new KernelJobsActionModule();
        var workload = new WorkloadModule();
        registry.Add(jobs);
        registry.Add(workload);
        return registry.Compile(
            null,
            new KernelGraphCompileOptions
            {
                ActionModuleCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
                {
                    [jobs.Identity.Id] = jobs.Grants,
                    [workload.Identity.Id] = workload.Grants,
                },
                SensitiveActionApprovals = jobs.Approvals,
            });
    }

    private sealed record ReadRequest(string Value);

    private sealed record ReadResult(string Value);

    private sealed class ReadHandler : IJobHandler<ReadRequest, ReadResult>
    {
        public SharpClawActionKey ActionKey { get; } = new("tool.fetch");

        public JobExecutionSafety Safety => JobExecutionSafety.Pure;

        public IJobPayloadCodec<ReadRequest> InputCodec { get; } =
            new JsonJobPayloadCodec<ReadRequest>("test.read.request");

        public IJobPayloadCodec<ReadResult> ResultCodec { get; } =
            new JsonJobPayloadCodec<ReadResult>("test.read.result");

        public ValueTask<ReadResult> ExecuteAsync(
            JobExecutionContext context,
            ReadRequest input,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ReadResult($"{input.Value}:{context.Caller.SubjectId}"));
    }

    private sealed record ValidateRequest(string Value);

    private sealed record ValidateResult(string Value);

    private sealed class ValidateHandler : IJobHandler<ValidateRequest, ValidateResult>
    {
        public SharpClawActionKey ActionKey { get; } = new("tool.validate");

        public JobExecutionSafety Safety => JobExecutionSafety.Pure;

        public IJobPayloadCodec<ValidateRequest> InputCodec { get; } =
            new JsonJobPayloadCodec<ValidateRequest>("test.validate.request");

        public IJobPayloadCodec<ValidateResult> ResultCodec { get; } =
            new JsonJobPayloadCodec<ValidateResult>("test.validate.result");

        public ValueTask<ValidateResult> ExecuteAsync(
            JobExecutionContext context,
            ValidateRequest input,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ValidateResult($"{input.Value}:{context.Caller.SubjectId}"));
    }

    private sealed class FailingHandler : IJobHandler<ReadRequest, ReadResult>
    {
        public SharpClawActionKey ActionKey { get; } = new("tool.fail");

        public JobExecutionSafety Safety => JobExecutionSafety.Receipted;

        public IJobPayloadCodec<ReadRequest> InputCodec { get; } =
            new JsonJobPayloadCodec<ReadRequest>("test.failure.request");

        public IJobPayloadCodec<ReadResult> ResultCodec { get; } =
            new JsonJobPayloadCodec<ReadResult>("test.failure.result");

        public ValueTask<ReadResult> ExecuteAsync(
            JobExecutionContext context,
            ReadRequest input,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The test handler failed.");
    }

    private sealed class ReceiptedHandler : IJobHandler<ReadRequest, ReadResult>
    {
        public SharpClawActionKey ActionKey { get; } = new("tool.receipted");

        public JobExecutionSafety Safety => JobExecutionSafety.Receipted;

        public IJobPayloadCodec<ReadRequest> InputCodec { get; } =
            new JsonJobPayloadCodec<ReadRequest>("test.receipted.request");

        public IJobPayloadCodec<ReadResult> ResultCodec { get; } =
            new JsonJobPayloadCodec<ReadResult>("test.receipted.result");

        public ValueTask<ReadResult> ExecuteAsync(
            JobExecutionContext context,
            ReadRequest input,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ReadResult($"{input.Value}:{context.Caller.SubjectId}"));
    }

    private sealed class ControlKeyHandler : IJobHandler<ReadRequest, ReadResult>
    {
        public SharpClawActionKey ActionKey { get; } = new("jobs.read");

        public JobExecutionSafety Safety => JobExecutionSafety.Pure;

        public IJobPayloadCodec<ReadRequest> InputCodec { get; } =
            new JsonJobPayloadCodec<ReadRequest>("test.control.request");

        public IJobPayloadCodec<ReadResult> ResultCodec { get; } =
            new JsonJobPayloadCodec<ReadResult>("test.control.result");

        public ValueTask<ReadResult> ExecuteAsync(
            JobExecutionContext context,
            ReadRequest input,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ReadResult(input.Value));
    }

    private sealed class BlockingHandler : IJobHandler<ReadRequest, ReadResult>
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SharpClawActionKey ActionKey { get; } = new("tool.fetch");

        public JobExecutionSafety Safety => JobExecutionSafety.Idempotent;

        public IJobPayloadCodec<ReadRequest> InputCodec { get; } =
            new JsonJobPayloadCodec<ReadRequest>("test.blocking.request");

        public IJobPayloadCodec<ReadResult> ResultCodec { get; } =
            new JsonJobPayloadCodec<ReadResult>("test.blocking.result");

        public async ValueTask<ReadResult> ExecuteAsync(
            JobExecutionContext context,
            ReadRequest input,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return new ReadResult(input.Value);
        }
    }

    private sealed class NonCooperativeHandler : IJobHandler<ReadRequest, ReadResult>
    {
        private int _invocationCount;

        public TaskCompletionSource<bool> FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> FirstFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public SharpClawActionKey ActionKey { get; } = new("tool.fetch");

        public JobExecutionSafety Safety => JobExecutionSafety.Idempotent;

        public IJobPayloadCodec<ReadRequest> InputCodec { get; } =
            new JsonJobPayloadCodec<ReadRequest>("test.non-cooperative.request");

        public IJobPayloadCodec<ReadResult> ResultCodec { get; } =
            new JsonJobPayloadCodec<ReadResult>("test.non-cooperative.result");

        public async ValueTask<ReadResult> ExecuteAsync(
            JobExecutionContext context,
            ReadRequest input,
            CancellationToken cancellationToken)
        {
            var invocation = Interlocked.Increment(ref _invocationCount);
            if (invocation == 1)
            {
                FirstStarted.TrySetResult(true);
                await ReleaseFirst.Task;
                FirstFinished.TrySetResult(true);
            }

            return new ReadResult($"{input.Value}:{invocation}");
        }
    }

    private sealed class WorkloadModule : ISharpClawModule
    {
        private const ActionInterceptionCapabilities WorkloadCapabilities =
            ActionInterceptionCapabilities.Inspect |
            ActionInterceptionCapabilities.ReplaceInput |
            ActionInterceptionCapabilities.ReplaceResult |
            ActionInterceptionCapabilities.Wrap;

        public ModuleIdentity Identity { get; } =
            new("test.workloads", "Test workloads", "tests");

        public IReadOnlyDictionary<string, ActionInterceptionCapabilities> Grants { get; } =
            new Dictionary<string, ActionInterceptionCapabilities>(StringComparer.Ordinal)
            {
                ["tool.fetch"] = WorkloadCapabilities,
                ["tool.validate"] = WorkloadCapabilities,
                ["tool.fail"] = WorkloadCapabilities,
                ["tool.receipted"] = WorkloadCapabilities,
            };

        public void Configure(ISharpClawModuleBuilder builder)
        {
            foreach (var key in Grants.Keys)
            {
                builder.Actions.Add(
                    new ActionDescriptor<KernelActionEnvelope, object>(
                        new SharpClawActionKey(key),
                        1,
                        "tool",
                        WorkloadCapabilities,
                        false,
                        false,
                        new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, key),
                        null,
                        TimeSpan.FromSeconds(10)));
            }
        }
    }

    private sealed class InMemoryJobsGateway : IModuleStorageGateway
    {
        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
        private readonly TimeSpan _leaseDuration;
        private readonly Dictionary<(string Storage, string Key), StoredRecord> _records = [];
        private readonly Dictionary<string, ModuleStorageMutationAndOutboxResult> _commits =
            new(StringComparer.Ordinal);
        private readonly Dictionary<(string Storage, string Key), ClaimState> _claims = [];
        private readonly object _sync = new();
        private int _atomicCommitCount;
        private int _claimCount;
        private int _renewCount;
        private int _recoverCount;
        private int _executionCommitCount;
        private int _maxStoredDocumentBytes;
        private readonly List<string> _commitLog = [];

        public TaskCompletionSource<bool>? ClaimBarrier { get; set; }

        private int _claimWaiters;

        public InMemoryJobsGateway(TimeSpan? leaseDuration = null)
        {
            _leaseDuration = leaseDuration ?? TimeSpan.FromMinutes(5);
        }

        public int Count(string storageName)
        {
            lock (_sync)
                return _records.Keys.Count(key => key.Storage == storageName);
        }

        public int CountAttempts() => CountAggregateItems("attempts");

        public int CountResults() => CountAggregateValues("result");

        public int CountProgress() => CountAggregateItems("progress");

        public int AtomicCommitCount => Volatile.Read(ref _atomicCommitCount);

        public int ClaimCount => Volatile.Read(ref _claimCount);

        public int RenewCount => Volatile.Read(ref _renewCount);

        public int RecoverCount => Volatile.Read(ref _recoverCount);

        public int ExecutionCommitCount => Volatile.Read(ref _executionCommitCount);

        public int MaxStoredDocumentBytes => Volatile.Read(ref _maxStoredDocumentBytes);

        public IReadOnlyList<string> CommitLog
        {
            get
            {
                lock (_sync)
                    return _commitLog.ToArray();
            }
        }

        public async Task WaitForClaimWaitersAsync(int expected)
        {
            for (var attempt = 0; attempt < 250; attempt++)
            {
                if (Volatile.Read(ref _claimWaiters) >= expected)
                    return;
                await Task.Delay(20);
            }

            throw new TimeoutException($"Expected {expected} storage claim waiters.");
        }

        public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() =>
            KernelJobsStorage.Contracts;

        private int CountAggregateItems(string propertyName)
        {
            lock (_sync)
            {
                return _records.Values.Sum(record =>
                    record.Value.TryGetProperty(propertyName, out var values) &&
                    values.ValueKind == JsonValueKind.Array
                        ? values.GetArrayLength()
                        : 0);
            }
        }

        private int CountAggregateValues(string propertyName)
        {
            lock (_sync)
            {
                return _records.Values.Count(record =>
                    record.Value.TryGetProperty(propertyName, out var value) &&
                    value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));
            }
        }

        public Task<JsonElement> InvokeAsync(
            string moduleId,
            string storageName,
            string operation,
            JsonElement parameters,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var response = operation switch
            {
                ModuleStorageOperations.Get => Get(storageName, parameters),
                ModuleStorageOperations.List or ModuleStorageOperations.Query => List(storageName, parameters),
                ModuleStorageOperations.Upsert => Upsert(storageName, parameters),
                ModuleStorageOperations.Delete => Delete(storageName, parameters),
                _ => throw new NotSupportedException(operation),
            };
            return Task.FromResult(response);
        }

        public Task<ModuleStorageMutationAndOutboxResult> CommitMutationAndOutboxAsync(
            string moduleId,
            string storageName,
            ModuleStorageMutationAndOutboxRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _atomicCommitCount);
            lock (_sync)
            {
                if (_commits.TryGetValue(request.Commit.IdempotencyKey, out var committed))
                    return Task.FromResult(committed with { AlreadyCommitted = true });

                var revisions = new List<ModuleStorageRevision>();
                foreach (var mutation in request.Mutations)
                {
                    var storageKey = (storageName, mutation.Key);
                    var hasPrevious = _records.TryGetValue(storageKey, out var previous);
                    var actualRevision = hasPrevious ? previous!.Revision : 0;
                    if (mutation.ExpectedRevision is not null &&
                        mutation.ExpectedRevision.Value != actualRevision)
                    {
                        throw RevisionConflict(mutation.Key, mutation.ExpectedRevision, actualRevision);
                    }

                    _commitLog.Add(
                        $"{mutation.Operation}:{mutation.Key}:expected={mutation.ExpectedRevision}:actual={actualRevision}:authority={mutation.Authority}");
                    ValidateAuthority(storageKey, mutation.Authority, actualRevision);
                    var revision = actualRevision + 1;
                    if (mutation.Operation == ModuleStorageOperations.Delete)
                    {
                        _records.Remove(storageKey);
                    }
                    else
                    {
                        if (mutation.Value is not { } value)
                            throw new InvalidOperationException("An atomic Jobs upsert requires a value.");
                        if (value.TryGetProperty("job", out var aggregateJob) &&
                            aggregateJob.TryGetProperty("status", out var status) &&
                            ((status.ValueKind == JsonValueKind.String &&
                              string.Equals(status.GetString(), JobStatus.Completed.ToString(), StringComparison.Ordinal)) ||
                             (status.ValueKind == JsonValueKind.Number &&
                              status.TryGetInt32(out var statusValue) &&
                              statusValue == (int)JobStatus.Completed)) &&
                            value.TryGetProperty("result", out var aggregateResult) &&
                            aggregateResult.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                        {
                            Interlocked.Increment(ref _executionCommitCount);
                        }
                        _maxStoredDocumentBytes = Math.Max(
                            _maxStoredDocumentBytes,
                            Encoding.UTF8.GetByteCount(value.GetRawText()));
                        _records[storageKey] = new StoredRecord(
                            value.Clone(),
                            revision,
                            mutation.Indexes is null
                                ? null
                                : JsonSerializer.SerializeToElement(mutation.Indexes));
                    }

                    if (_claims.TryGetValue(storageKey, out var claim))
                    {
                        _claims[storageKey] = claim with
                        {
                            Authority = claim.Authority with { Revision = revision },
                        };
                    }
                    revisions.Add(new ModuleStorageRevision(mutation.Key, revision));
                }

                var result = new ModuleStorageMutationAndOutboxResult(
                    request.Commit,
                    revisions,
                    [],
                    revisions.Count == 0 ? 0 : revisions[^1].Revision);
                _commits[request.Commit.IdempotencyKey] = result;
                return Task.FromResult(result);
            }
        }

        public Task<ModuleStorageClaimResult<T>> ClaimAsync<T>(
            string moduleId,
            string storageName,
            ModuleStorageClaimRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _claimCount);
            if (ClaimBarrier is { } barrier)
            {
                Interlocked.Increment(ref _claimWaiters);
                return WaitForClaimBarrierAsync<T>(
                    moduleId,
                    storageName,
                    request,
                    barrier,
                    ct);
            }

            return ClaimCore<T>(storageName, request);
        }

        private async Task<ModuleStorageClaimResult<T>> WaitForClaimBarrierAsync<T>(
            string moduleId,
            string storageName,
            ModuleStorageClaimRequest request,
            TaskCompletionSource<bool> barrier,
            CancellationToken ct)
        {
            await barrier.Task.WaitAsync(ct);
            return await ClaimCore<T>(storageName, request);
        }

        private Task<ModuleStorageClaimResult<T>> ClaimCore<T>(
            string storageName,
            ModuleStorageClaimRequest request)
        {
            lock (_sync)
            {
                var candidate = _records
                    .Where(pair => pair.Key.Storage == storageName)
                    .Where(pair => MatchesFilters(pair.Value.Indexes, request.Filters))
                    .Select(pair => pair)
                    .FirstOrDefault();
                if (candidate.Value is null)
                {
                    var emptyAuthority = NewAuthority(0, 1);
                    return Task.FromResult(
                        new ModuleStorageClaimResult<T>([], emptyAuthority));
                }

                if (request.ExpectedRevision is not null &&
                    candidate.Value.Revision != request.ExpectedRevision.Value)
                {
                    throw RevisionConflict(
                        candidate.Key.Key,
                        request.ExpectedRevision,
                        candidate.Value.Revision);
                }

                if (_claims.TryGetValue(candidate.Key, out var existingClaim) &&
                    existingClaim.Authority.IsValidAt(DateTimeOffset.UtcNow))
                {
                    throw new ModuleStorageContractException(new ModuleStorageContractFailure(
                        ModuleStorageErrors.StaleClaim,
                        "The Jobs aggregate already has a live storage claim.",
                        candidate.Key.Key));
                }

                var value = JsonSerializer.SerializeToElement(request.Patch);
                var revision = candidate.Value.Revision + 1;
                _records[candidate.Key] = new StoredRecord(
                    value,
                    revision,
                    request.Indexes is null
                        ? candidate.Value.Indexes
                        : JsonSerializer.SerializeToElement(request.Indexes));
                var authority = NewAuthority(revision, existingClaim?.Authority.Generation + 1 ?? 1);
                _claims[candidate.Key] = new ClaimState(authority);
                var typed = value.Deserialize<T>(JsonOptions)!;
                var record = new ModuleStorageClaimRecord<T>(
                    candidate.Key.Key,
                    typed,
                    revision,
                    authority,
                    _records[candidate.Key].Indexes);
                return Task.FromResult(
                    new ModuleStorageClaimResult<T>([record], authority));
            }
        }

        public Task<ModuleStorageClaimRenewalResult> RenewClaimAsync(
            string moduleId,
            string storageName,
            ModuleStorageClaimRenewalRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _renewCount);
            lock (_sync)
            {
                var match = _claims.FirstOrDefault(pair =>
                    pair.Key.Storage == storageName &&
                    pair.Value.Authority.HostToken == request.HostToken &&
                    pair.Value.Authority.Generation == request.Generation);
                if (match.Value is null ||
                    !match.Value.Authority.IsValidAt(DateTimeOffset.UtcNow) ||
                    !_records.TryGetValue(match.Key, out var record))
                {
                    return Task.FromResult(new ModuleStorageClaimRenewalResult(
                        false,
                        null,
                        ModuleStorageErrors.StaleClaim));
                }

                var authority = match.Value.Authority with
                {
                    LeaseExpiresAt = request.RequestedLeaseExpiresAt,
                    Revision = record.Revision,
                };
                _claims[match.Key] = new ClaimState(authority);
                return Task.FromResult(new ModuleStorageClaimRenewalResult(true, authority));
            }
        }

        public Task<ModuleStorageClaimRecoveryResult> RecoverClaimAsync(
            string moduleId,
            string storageName,
            ModuleStorageClaimRecoveryRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _recoverCount);
            lock (_sync)
            {
                var match = _claims.FirstOrDefault(pair =>
                    pair.Key.Storage == storageName &&
                    pair.Value.Authority.HostToken == request.HostToken &&
                    pair.Value.Authority.Generation == request.Generation);
                if (match.Value is null)
                    return Task.FromResult(new ModuleStorageClaimRecoveryResult(
                        false,
                        null,
                        ModuleStorageErrors.StaleClaim));

                _claims.Remove(match.Key);
                return Task.FromResult(new ModuleStorageClaimRecoveryResult(
                    true,
                    match.Value.Authority,
                    null));
            }
        }

        private JsonElement Get(string storageName, JsonElement parameters)
        {
            var key = parameters.GetProperty("key").GetString()!;
            lock (_sync)
            {
                if (!_records.TryGetValue((storageName, key), out var record))
                    return JsonDocument.Parse("{\"found\":false}").RootElement.Clone();
                return JsonSerializer.SerializeToElement(
                    new
                    {
                        found = true,
                        key,
                        value = record.Value,
                        revision = record.Revision,
                        indexes = record.Indexes,
                    },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
        }

        private JsonElement List(string storageName, JsonElement parameters)
        {
            lock (_sync)
            {
                var candidates = _records
                    .Where(pair => pair.Key.Storage == storageName)
                    .Where(pair => MatchesFilters(pair.Value.Indexes, parameters))
                    .Select(pair => new
                    {
                        key = pair.Key.Key,
                        value = pair.Value.Value,
                        revision = pair.Value.Revision,
                        indexes = pair.Value.Indexes,
                    })
                    .ToArray();
                return JsonSerializer.SerializeToElement(
                    new { records = candidates },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
        }

        private static bool MatchesFilters(JsonElement? indexes, JsonElement parameters)
        {
            if (!parameters.TryGetProperty("filters", out var filters) ||
                filters.ValueKind != JsonValueKind.Array)
            {
                return true;
            }

            foreach (var filter in filters.EnumerateArray())
            {
                var indexName = filter.GetProperty("indexName").GetString()!;
                var comparison = filter.GetProperty("operator").GetString();
                if (!string.Equals(comparison, ModuleStorageComparisonOperators.EqualTo, StringComparison.Ordinal))
                    continue;
                if (indexes is null || !indexes.Value.TryGetProperty(indexName, out var actual))
                    return false;
                var expected = filter.GetProperty("value");
                if (!string.Equals(
                        actual.ToString(),
                        expected.ToString(),
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MatchesFilters(
            JsonElement? indexes,
            IReadOnlyList<ModuleDocumentIndexFilter> filters)
        {
            foreach (var filter in filters)
            {
                if (!string.Equals(filter.Operator, ModuleStorageComparisonOperators.EqualTo, StringComparison.Ordinal))
                    continue;
                if (indexes is null || !indexes.Value.TryGetProperty(filter.IndexName, out var actual))
                    return false;
                if (!string.Equals(actual.ToString(), filter.Value?.ToString(), StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new ReadOnlySetJsonConverterFactory());
            return options;
        }

        private void ValidateAuthority(
            (string Storage, string Key) storageKey,
            ModuleStorageClaimAuthority? authority,
            long actualRevision)
        {
            if (authority is null)
                return;
            if (!_claims.TryGetValue(storageKey, out var claim) ||
                !claim.Authority.Matches(authority) ||
                claim.Authority.Revision != actualRevision)
            {
                throw new ModuleStorageContractException(new ModuleStorageContractFailure(
                    ModuleStorageErrors.FencingRejected,
                    $"The Jobs mutation does not carry the current storage claim. " +
                    $"Expected={authority}, Current={claim?.Authority}, ActualRevision={actualRevision}, " +
                    $"History={string.Join(" | ", _commitLog)}.",
                    storageKey.Key));
            }
        }

        private ModuleStorageClaimAuthority NewAuthority(
            long revision,
            long generation) =>
            new(
                KernelJobsStorage.OwnerModuleId,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.Add(_leaseDuration),
                generation,
                revision);

        private static ModuleStorageContractException RevisionConflict(
            string key,
            long? expectedRevision,
            long actualRevision) =>
            new(new ModuleStorageContractFailure(
                ModuleStorageErrors.RevisionConflict,
                $"The test storage rejected stale revision {expectedRevision} for '{key}'.",
                key,
                expectedRevision,
                actualRevision));

        private JsonElement Upsert(string storageName, JsonElement parameters)
        {
            var key = parameters.GetProperty("key").GetString()!;
            var value = parameters.GetProperty("value").Clone();
            var indexes = parameters.TryGetProperty("indexes", out var indexElement)
                ? indexElement.Clone()
                : (JsonElement?)null;
            lock (_sync)
            {
                var hasPrevious = _records.TryGetValue((storageName, key), out var previous);
                var expectedRevision = parameters.TryGetProperty("expectedRevision", out var expectedElement)
                    ? expectedElement.GetInt64()
                    : (long?)null;
                if (expectedRevision is not null &&
                    (hasPrevious ? previous!.Revision : 0) != expectedRevision.Value)
                {
                    throw new InvalidOperationException(
                        $"The test storage rejected stale revision {expectedRevision.Value} for '{key}'.");
                }

                var revision = hasPrevious
                    ? previous!.Revision + 1
                    : 1;
                _records[(storageName, key)] = new StoredRecord(value, revision, indexes);
                return JsonSerializer.SerializeToElement(new { saved = 1 });
            }
        }

        private JsonElement Delete(string storageName, JsonElement parameters)
        {
            var key = parameters.GetProperty("key").GetString()!;
            lock (_sync)
            {
                if (parameters.TryGetProperty("expectedRevision", out var expectedElement) &&
                    (!_records.TryGetValue((storageName, key), out var existing) ||
                     existing.Revision != expectedElement.GetInt64()))
                {
                    throw new InvalidOperationException(
                        $"The test storage rejected stale delete revision for '{key}'.");
                }
                return JsonSerializer.SerializeToElement(
                    new { deleted = _records.Remove((storageName, key)) });
            }
        }

        private sealed record StoredRecord(JsonElement Value, long Revision, JsonElement? Indexes);

        private sealed record ClaimState(ModuleStorageClaimAuthority Authority);

        private sealed class ReadOnlySetJsonConverterFactory : JsonConverterFactory
        {
            public override bool CanConvert(Type typeToConvert) =>
                typeToConvert.IsGenericType &&
                typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);

            public override JsonConverter CreateConverter(
                Type typeToConvert,
                JsonSerializerOptions options) =>
                (JsonConverter)Activator.CreateInstance(
                    typeof(ReadOnlySetJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
        }

        private sealed class ReadOnlySetJsonConverter<T> : JsonConverter<IReadOnlySet<T>>
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
    }
}
