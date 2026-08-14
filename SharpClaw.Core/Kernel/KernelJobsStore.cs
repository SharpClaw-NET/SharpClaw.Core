using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

/// <summary>Stores the complete Jobs aggregate through the neutral module storage contract.</summary>
public sealed class KernelJobsStore
{
    private readonly IModuleStorageGateway _gateway;
    private readonly ModuleDocumentStore<KernelJobsAggregate> _records;
    private readonly string _ownerModuleId;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public KernelJobsStore(
        IModuleStorageGateway gateway,
        string ownerModuleId = KernelJobsStorage.OwnerModuleId)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        if (string.IsNullOrWhiteSpace(ownerModuleId))
            throw new ArgumentException("A Jobs store requires an owner module.", nameof(ownerModuleId));

        _jsonOptions.Converters.Add(new ReadOnlySetJsonConverterFactory());
        _ownerModuleId = ownerModuleId;
        _gateway = gateway;
        _records = new ModuleDocumentStore<KernelJobsAggregate>(
            gateway,
            ownerModuleId,
            KernelJobsStorage.Jobs,
            ownerModuleId);
    }

    public async Task<ModuleDocumentRecord<JobDocument>?> GetJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var record = await FindAggregateRecordByJobIdAsync(jobId, cancellationToken);
        return record is null ? null : ToJobRecord(record);
    }

    public async Task<IReadOnlyList<ModuleDocumentRecord<JobDocument>>> ListJobRecordsAsync(
        string? callerSubjectId = null,
        Guid? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var query = _records.Query();
        if (!string.IsNullOrWhiteSpace(callerSubjectId))
            query = query.WhereIndex("callerSubject").EqualTo(callerSubjectId);
        if (idempotencyKey is not null)
            query = query.WhereIndex("idempotencyKey").EqualTo(idempotencyKey.Value.ToString("D"));

        var records = await query
            .OrderByIndex("createdAt")
            .ToRecordsAsync(cancellationToken);
        return records.Select(ToJobRecord).ToArray();
    }

    public Task<IReadOnlyList<ModuleDocumentRecord<JobDocument>>> FindJobRecordsByIdempotencyAsync(
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ListJobRecordsAsync(idempotencyKey: idempotencyKey, cancellationToken: cancellationToken);

    public async Task SaveJobAsync(
        JobDocument job,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default,
        ModuleStorageClaimAuthority? authority = null)
    {
        var current = await FindAggregateRecordByJobIdAsync(job.Id, cancellationToken);
        var expected = expectedRevision ?? current?.Revision ?? 0;
        var aggregate = (current?.Value ?? KernelJobsAggregate.Empty(job)).WithJob(job);
        await CommitAggregateAsync(
            current?.Key ?? AggregateKey(job.IdempotencyKey),
            aggregate,
            expected,
            ModuleStorageOperations.Upsert,
            authority,
            cancellationToken);
    }

    internal async Task<KernelJobsClaim> ClaimJobAsync(
        JobDocument startedJob,
        JobAttemptDocument attempt,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var current = await FindAggregateRecordByJobIdAsync(startedJob.Id, cancellationToken);
        if (current is null)
            throw RevisionConflict(startedJob.IdempotencyKey.ToString("N"), expectedRevision, null);

        var attempts = current.Value!.Attempts
            .Where(item => item.AttemptId != attempt.AttemptId)
            .Append(attempt)
            .ToArray();
        var aggregate = current.Value with
        {
            Job = startedJob,
            Attempts = attempts,
        };
        var claim = await _records
            .Claim()
            .WhereIndex("jobId").EqualTo(startedJob.Id.ToString("D"))
            .AtRevision(expectedRevision)
            .Take(1)
            .Patch(aggregate, JobIndexes(startedJob))
            .ToRecordsAsync(cancellationToken);

        if (claim.Records.Count != 1 || claim.Records[0].Value is null)
            throw RevisionConflict(current.Key, expectedRevision, current.Revision);

        return new KernelJobsClaim(
            ToJobRecord(claim.Records[0]),
            claim.Authority);
    }

    public async Task<ModuleStorageClaimAuthority> RenewJobClaimAsync(
        ModuleStorageClaimAuthority authority,
        DateTimeOffset requestedLeaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var result = await _gateway.RenewClaimAsync(
            _ownerModuleId,
            KernelJobsStorage.Jobs,
            new ModuleStorageClaimRenewalRequest(
                _ownerModuleId,
                authority.HostToken,
                authority.Generation,
                requestedLeaseExpiresAt),
            cancellationToken);
        if (!result.Renewed || result.Authority is null)
        {
            throw new ModuleStorageContractException(new ModuleStorageContractFailure(
                result.ErrorCode ?? ModuleStorageErrors.StaleClaim,
                "The Jobs storage claim could not be renewed."));
        }

        return result.Authority;
    }

    public async Task<bool> RecoverJobClaimAsync(
        ModuleStorageClaimAuthority authority,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var result = await _gateway.RecoverClaimAsync(
            _ownerModuleId,
            KernelJobsStorage.Jobs,
            new ModuleStorageClaimRecoveryRequest(
                _ownerModuleId,
                authority.HostToken,
                authority.Generation,
                observedAt),
            cancellationToken);
        return result.Recovered;
    }

    public async Task SaveAttemptAsync(
        JobAttemptDocument attempt,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default,
        ModuleStorageClaimAuthority? authority = null)
    {
        var current = await RequireAggregateRecordAsync(attempt.JobId, cancellationToken);
        var expected = expectedRevision ?? current.Revision;
        var attempts = current.Value!.Attempts
            .Where(item => item.AttemptId != attempt.AttemptId)
            .Append(attempt)
            .ToArray();
        await CommitAggregateAsync(
            current.Key,
            current.Value with { Attempts = attempts },
            expected,
            ModuleStorageOperations.Upsert,
            authority,
            cancellationToken);
    }

    public async Task<ModuleDocumentRecord<JobAttemptDocument>?> GetAttemptAsync(
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        var aggregate = await FindAggregateRecordByAttemptIdAsync(attemptId, cancellationToken);
        var attempt = aggregate?.Value?.Attempts.FirstOrDefault(item => item.AttemptId == attemptId);
        return aggregate is null || attempt is null
            ? null
            : new ModuleDocumentRecord<JobAttemptDocument>(
                AttemptKey(attemptId),
                attempt,
                aggregate.Revision,
                aggregate.Indexes);
    }

    public async Task<IReadOnlyList<ModuleDocumentRecord<JobAttemptDocument>>> ListAttemptRecordsAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var aggregate = await FindAggregateRecordByJobIdAsync(jobId, cancellationToken);
        return aggregate?.Value?.Attempts
            .Select(attempt => new ModuleDocumentRecord<JobAttemptDocument>(
                AttemptKey(attempt.AttemptId),
                attempt,
                aggregate.Revision,
                aggregate.Indexes))
            .ToArray() ?? [];
    }

    public async Task CommitExecutionAsync(
        JobDocument completedJob,
        JobAttemptDocument finishedAttempt,
        JobPayloadEnvelope result,
        long expectedRevision,
        ModuleStorageClaimAuthority? authority = null,
        CancellationToken cancellationToken = default)
    {
        var current = await RequireAggregateRecordAsync(completedJob.Id, cancellationToken);
        var attempts = current.Value!.Attempts
            .Where(item => item.AttemptId != finishedAttempt.AttemptId)
            .Append(finishedAttempt)
            .ToArray();
        await CommitAggregateAsync(
            current.Key,
            current.Value with
            {
                Job = completedJob,
                Attempts = attempts,
                Result = result,
            },
            expectedRevision,
            ModuleStorageOperations.Upsert,
            authority,
            cancellationToken);
    }

    public async Task<ModuleDocumentRecord<JobPayloadEnvelope>?> GetResultAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var aggregate = await FindAggregateRecordByJobIdAsync(jobId, cancellationToken);
        return aggregate?.Value?.Result is not { } result
            ? null
            : new ModuleDocumentRecord<JobPayloadEnvelope>(
                JobKey(jobId),
                result,
                aggregate.Revision,
                aggregate.Indexes);
    }

    public async Task<IReadOnlyList<ModuleDocumentRecord<JobProgress>>> ListProgressRecordsAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var aggregate = await FindAggregateRecordByJobIdAsync(jobId, cancellationToken);
        return aggregate?.Value?.Progress
            .Select((progress, index) => new ModuleDocumentRecord<JobProgress>(
                $"{jobId:N}:{index:D8}",
                progress,
                aggregate.Revision,
                aggregate.Indexes))
            .ToArray() ?? [];
    }

    public async Task SaveProgressAsync(
        JobProgress progress,
        CancellationToken cancellationToken = default,
        ModuleStorageClaimAuthority? authority = null)
    {
        var current = await RequireAggregateRecordAsync(progress.JobId, cancellationToken);
        var next = current.Value!.Progress.Append(progress).ToArray();
        await CommitAggregateAsync(
            current.Key,
            current.Value with { Progress = next },
            current.Revision,
            ModuleStorageOperations.Upsert,
            authority,
            cancellationToken);
    }

    public async Task<bool> DeleteJobAsync(
        Guid jobId,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default,
        ModuleStorageClaimAuthority? authority = null)
    {
        var current = await FindAggregateRecordByJobIdAsync(jobId, cancellationToken);
        if (current is null)
            return false;

        await CommitAggregateAsync(
            current.Key,
            null,
            expectedRevision ?? current.Revision,
            ModuleStorageOperations.Delete,
            authority,
            cancellationToken);
        return true;
    }

    private async Task<ModuleDocumentRecord<KernelJobsAggregate>?> FindAggregateRecordByJobIdAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var records = await _records.Query()
            .WhereIndex("jobId").EqualTo(jobId.ToString("D"))
            .Take(1)
            .ToRecordsAsync(cancellationToken);
        return records.FirstOrDefault();
    }

    private async Task<ModuleDocumentRecord<KernelJobsAggregate>?> FindAggregateRecordByAttemptIdAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        var records = await _records.Query().ToRecordsAsync(cancellationToken);
        return records.FirstOrDefault(record =>
            record.Value?.Attempts.Any(attempt => attempt.AttemptId == attemptId) == true);
    }

    private async Task<ModuleDocumentRecord<KernelJobsAggregate>> RequireAggregateRecordAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        return await FindAggregateRecordByJobIdAsync(jobId, cancellationToken)
            ?? throw new KernelActionExecutionException(
                $"Jobs record '{jobId:D}' was not found in the atomic aggregate.");
    }

    private async Task<long> CommitAggregateAsync(
        string key,
        KernelJobsAggregate? aggregate,
        long expectedRevision,
        string operation,
        ModuleStorageClaimAuthority? authority,
        CancellationToken cancellationToken)
    {
        var mutation = new ModuleStorageMutation(
            operation,
            key,
            aggregate is null ? null : JsonSerializer.SerializeToElement(aggregate, _jsonOptions),
            null,
            aggregate is null ? null : JobIndexes(aggregate.Job),
            expectedRevision,
            authority);
        var request = new ModuleStorageMutationAndOutboxRequest(
            new ModuleStorageCommitIdentity(
                Guid.TryParse(key, out var keyIdentity) ? keyIdentity : Guid.NewGuid(),
                $"jobs:{key}:{expectedRevision}:{operation}"),
            [mutation],
            []);
        var result = await _gateway.CommitMutationAndOutboxAsync(
            _ownerModuleId,
            KernelJobsStorage.Jobs,
            request,
            cancellationToken);
        ModuleStorageCommitValidation.Validate(request, result);
        if (result.AlreadyCommitted)
        {
            throw RevisionConflict(
                key,
                expectedRevision,
                result.Revisions.Count == 0 ? null : result.Revisions[0].Revision);
        }
        return result.Revisions[0].Revision;
    }

    private static ModuleDocumentRecord<JobDocument> ToJobRecord(
        ModuleDocumentRecord<KernelJobsAggregate> record) =>
        new(record.Key, record.Value!.Job, record.Revision, record.Indexes);

    private static ModuleDocumentRecord<JobDocument> ToJobRecord(
        ModuleStorageClaimRecord<KernelJobsAggregate> record) =>
        new(record.Key, record.Value!.Job, record.Revision, record.Indexes);

    private static object JobIndexes(JobDocument job) => new
    {
        recordType = "job",
        jobId = job.Id.ToString("D"),
        actionKey = job.ActionKey.Value,
        status = job.Status.ToString(),
        callerSubject = job.Caller.SubjectId,
        idempotencyKey = job.IdempotencyKey.ToString("D"),
        createdAt = job.CreatedAt,
        attemptId = job.ActiveAttemptId?.ToString("D"),
    };

    private static string AggregateKey(Guid idempotencyKey) => idempotencyKey.ToString("N");

    private static string JobKey(Guid jobId) => jobId.ToString("N");

    private static string AttemptKey(Guid attemptId) => attemptId.ToString("N");

    private static ModuleStorageContractException RevisionConflict(
        string key,
        long? expectedRevision,
        long? actualRevision) =>
        new(new ModuleStorageContractFailure(
            ModuleStorageErrors.RevisionConflict,
            $"The Jobs aggregate revision for '{key}' changed before the requested mutation.",
            key,
            expectedRevision,
            actualRevision));

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

internal sealed record KernelJobsClaim(
    ModuleDocumentRecord<JobDocument> Job,
    ModuleStorageClaimAuthority Authority);

internal sealed record KernelJobsAggregate(
    JobDocument Job,
    IReadOnlyList<JobAttemptDocument> Attempts,
    JobPayloadEnvelope? Result,
    IReadOnlyList<JobProgress> Progress)
{
    public static KernelJobsAggregate Empty(JobDocument job) =>
        new(job, [], null, []);

    public KernelJobsAggregate WithJob(JobDocument job) => this with { Job = job };
}
