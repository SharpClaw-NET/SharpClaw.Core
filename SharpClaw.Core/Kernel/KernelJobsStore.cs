using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

/// <summary>Provides the Core Jobs view over the neutral module document store.</summary>
public sealed class KernelJobsStore
{
    private readonly ModuleDocumentStore<JobDocument> _jobs;
    private readonly ModuleDocumentStore<JobAttemptDocument> _attempts;
    private readonly ModuleDocumentStore<JobPayloadEnvelope> _results;
    private readonly ModuleDocumentStore<JobProgress> _progress;

    public KernelJobsStore(
        IModuleStorageGateway gateway,
        string ownerModuleId = KernelJobsStorage.OwnerModuleId)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        if (string.IsNullOrWhiteSpace(ownerModuleId))
            throw new ArgumentException("A Jobs store requires an owner module.", nameof(ownerModuleId));

        _jobs = new ModuleDocumentStore<JobDocument>(
            gateway,
            ownerModuleId,
            KernelJobsStorage.Jobs,
            ownerModuleId);
        _attempts = new ModuleDocumentStore<JobAttemptDocument>(
            gateway,
            ownerModuleId,
            KernelJobsStorage.Attempts,
            ownerModuleId);
        _results = new ModuleDocumentStore<JobPayloadEnvelope>(
            gateway,
            ownerModuleId,
            KernelJobsStorage.Results,
            ownerModuleId);
        _progress = new ModuleDocumentStore<JobProgress>(
            gateway,
            ownerModuleId,
            KernelJobsStorage.Progress,
            ownerModuleId);
    }

    public Task<ModuleDocumentRecord<JobDocument>?> GetJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) =>
        _jobs.GetRecordAsync(JobKey(jobId), cancellationToken);

    public Task<IReadOnlyList<ModuleDocumentRecord<JobDocument>>> ListJobRecordsAsync(
        string? callerSubjectId = null,
        Guid? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var query = _jobs.Query();
        if (!string.IsNullOrWhiteSpace(callerSubjectId))
            query = query.WhereIndex("callerSubject").EqualTo(callerSubjectId);
        if (idempotencyKey is not null)
            query = query.WhereIndex("idempotencyKey").EqualTo(idempotencyKey.Value.ToString("D"));
        return query.OrderByIndex("createdAt").ToRecordsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<ModuleDocumentRecord<JobDocument>>> FindJobRecordsByIdempotencyAsync(
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ListJobRecordsAsync(idempotencyKey: idempotencyKey, cancellationToken: cancellationToken);

    public Task SaveJobAsync(
        JobDocument job,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        _jobs.UpsertAsync(
            JobKey(job.Id),
            job,
            JobIndexes(job),
            cancellationToken,
            expectedRevision);

    public Task<bool> DeleteJobAsync(
        Guid jobId,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        _jobs.DeleteAsync(JobKey(jobId), cancellationToken, expectedRevision);

    public Task SaveAttemptAsync(
        JobAttemptDocument attempt,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        _attempts.UpsertAsync(
            AttemptKey(attempt.AttemptId),
            attempt,
            AttemptIndexes(attempt),
            cancellationToken,
            expectedRevision);

    public Task<ModuleDocumentRecord<JobAttemptDocument>?> GetAttemptAsync(
        Guid attemptId,
        CancellationToken cancellationToken = default) =>
        _attempts.GetRecordAsync(AttemptKey(attemptId), cancellationToken);

    public Task<IReadOnlyList<ModuleDocumentRecord<JobAttemptDocument>>> ListAttemptRecordsAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) =>
        _attempts.Query()
            .WhereIndex("jobId").EqualTo(jobId.ToString("D"))
            .OrderByIndex("startedAt")
            .ToRecordsAsync(cancellationToken);

    public Task SaveResultAsync(
        Guid jobId,
        JobPayloadEnvelope result,
        CancellationToken cancellationToken = default) =>
        _results.UpsertAsync(
            JobKey(jobId),
            result,
            new { jobId = jobId.ToString("D") },
            cancellationToken);

    public Task<ModuleDocumentRecord<JobPayloadEnvelope>?> GetResultAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) =>
        _results.GetRecordAsync(JobKey(jobId), cancellationToken);

    public Task<bool> DeleteResultAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) =>
        _results.DeleteAsync(JobKey(jobId), cancellationToken);

    public Task SaveProgressAsync(
        JobProgress progress,
        CancellationToken cancellationToken = default) =>
        _progress.UpsertAsync(
            ProgressKey(progress),
            progress,
            new
            {
                jobId = progress.JobId.ToString("D"),
                attemptId = progress.AttemptId?.ToString("D"),
                code = progress.Code,
                occurredAt = progress.OccurredAt ?? DateTimeOffset.UtcNow,
            },
            cancellationToken);

    public Task<IReadOnlyList<ModuleDocumentRecord<JobProgress>>> ListProgressRecordsAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) =>
        _progress.Query()
            .WhereIndex("jobId").EqualTo(jobId.ToString("D"))
            .OrderByIndex("occurredAt")
            .ToRecordsAsync(cancellationToken);

    private static string JobKey(Guid jobId) => jobId.ToString("N");

    private static string AttemptKey(Guid attemptId) => attemptId.ToString("N");

    private static string ProgressKey(JobProgress progress) =>
        string.Join(
            ":",
            progress.JobId.ToString("N"),
            progress.AttemptId?.ToString("N") ?? "none",
            (progress.OccurredAt ?? DateTimeOffset.UtcNow).UtcTicks,
            progress.Code);

    private static object JobIndexes(JobDocument job) => new
    {
        jobId = job.Id.ToString("D"),
        actionKey = job.ActionKey.Value,
        status = job.Status.ToString(),
        callerSubject = job.Caller.SubjectId,
        idempotencyKey = job.IdempotencyKey.ToString("D"),
        createdAt = job.CreatedAt,
        attemptId = job.ActiveAttemptId?.ToString("D"),
    };

    private static object AttemptIndexes(JobAttemptDocument attempt) => new
    {
        jobId = attempt.JobId.ToString("D"),
        attemptId = attempt.AttemptId.ToString("D"),
        startedAt = attempt.StartedAt,
        safety = attempt.Safety.ToString(),
    };
}
