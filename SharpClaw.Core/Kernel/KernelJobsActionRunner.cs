using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

/// <summary>Runs one typed Jobs family through the existing action dispatcher.</summary>
public sealed class KernelJobsActionRunner(
    KernelGraph graph,
    KernelActionDispatcher dispatcher)
{
    public async ValueTask<JobDocument> RunAsync<TFamily>(
        SharpClawActionKey key,
        JobDocument job,
        Func<JobDocument, CancellationToken, ValueTask<JobDocument>> terminal,
        KernelActionExecutionContext executionContext,
        long expectedRevision = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(executionContext);

        var contract = KernelJobsActionCatalog.For<TFamily>(key);
        var input = new KernelJobOperationInput<TFamily>(job);
        var beforeCompleted = 0;
        var before = await dispatcher.RunRequiredWithContextAsync(
            executionContext,
            contract.Before,
            new JobCheckpoint<KernelJobOperationInput<TFamily>>(
                job.Id,
                job.ActiveAttemptId,
                job.InvocationId,
                job.IdempotencyKey,
                job.Status,
                null,
                JobSafePoint.BeforeTerminal,
                input,
                expectedRevision),
            (effective, _) =>
            {
                Interlocked.Exchange(ref beforeCompleted, 1);
                return ValueTask.FromResult(effective);
            },
            graph.ActionSnapshot,
            cancellationToken);

        if (Volatile.Read(ref beforeCompleted) == 0)
            throw new KernelActionExecutionException(
                $"Jobs action '{key.Value}.before' completed without running its terminal.");

        var rootCompleted = 0;
        var result = await dispatcher.RunRequiredWithContextAsync(
            executionContext,
            contract.Action,
            before.Value,
            async (effective, ct) =>
            {
                Interlocked.Exchange(ref rootCompleted, 1);
                var updated = await terminal(effective.Job, ct);
                return new KernelJobOperationResult<TFamily>(updated, null, effective.Progress);
            },
            graph.ActionSnapshot,
            cancellationToken);

        if (Volatile.Read(ref rootCompleted) == 0)
            throw new KernelActionExecutionException(
                $"Jobs action '{key.Value}' completed without running its terminal.");

        var afterCompleted = 0;
        var after = await dispatcher.RunRequiredWithContextAsync(
            executionContext,
            contract.After,
            new JobCheckpoint<KernelJobOperationResult<TFamily>>(
                result.Job.Id,
                result.Job.ActiveAttemptId,
                result.Job.InvocationId,
                result.Job.IdempotencyKey,
                result.Job.Status,
                result.Job.Status,
                JobSafePoint.AfterTerminal,
                result,
                expectedRevision),
            (effective, _) =>
            {
                Interlocked.Exchange(ref afterCompleted, 1);
                return ValueTask.FromResult(effective);
            },
            graph.ActionSnapshot,
            cancellationToken);

        if (Volatile.Read(ref afterCompleted) == 0)
            throw new KernelActionExecutionException(
                $"Jobs action '{key.Value}.after' completed without running its terminal.");

        return after.Value.Job;
    }
}
