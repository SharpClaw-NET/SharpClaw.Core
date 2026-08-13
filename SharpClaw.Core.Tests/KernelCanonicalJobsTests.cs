using System.Text.Json;
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
            [KernelJobsStorage.Jobs, KernelJobsStorage.Attempts, KernelJobsStorage.Results, KernelJobsStorage.Progress],
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
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await coordinator.DispatchAsync<ReadResult>(job.Id, context);
        handler.Release.TrySetResult(true);
        var first = await firstTask;

        Assert.Equal(ActionOutcomeKind.Deferred, second.Outcome);
        Assert.Equal(ActionOutcomeKind.Completed, first.Outcome);
        Assert.Equal(1, gateway.Count(KernelJobsStorage.Attempts));
        Assert.Equal(1, gateway.Count(KernelJobsStorage.Results));
        Assert.Equal(JobStatus.Completed, first.Job.Status);
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
        Assert.Equal(2, gateway.Count(KernelJobsStorage.Progress));

        Assert.Equal(JobStatus.Cancelled, (await coordinator.StopAsync(job.Id, context)).Status);
        Assert.True(await coordinator.DeleteAsync(job.Id, context));
        Assert.Empty(await coordinator.ListAsync(context));
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

        Assert.Equal(ActionOutcomeKind.Completed, read.Outcome);
        Assert.Equal(ActionOutcomeKind.Completed, validate.Outcome);
        Assert.Equal("read:jobs-user", read.Result!.Value);
        Assert.Equal("validate:jobs-user", validate.Result!.Value);
        Assert.Equal(JobStatus.Completed, read.Job.Status);
        Assert.Equal(JobStatus.Completed, validate.Job.Status);
        Assert.Equal(context.Caller.SubjectId, read.Job.Caller.SubjectId);
        Assert.Equal(context.Caller.SubjectId, validate.Job.Caller.SubjectId);
        Assert.Equal(readIdempotencyKey, read.Job.IdempotencyKey);
        Assert.Equal(validateIdempotencyKey, validate.Job.IdempotencyKey);
        Assert.Equal(2, gateway.Count(KernelJobsStorage.Jobs));
        Assert.Equal(2, gateway.Count(KernelJobsStorage.Results));
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
        Assert.Equal(1, gateway.Count(KernelJobsStorage.Progress));
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
        Assert.Equal(0, gateway.Count(KernelJobsStorage.Results));
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
        private readonly Dictionary<(string Storage, string Key), StoredRecord> _records = [];
        private readonly object _sync = new();

        public int Count(string storageName)
        {
            lock (_sync)
                return _records.Keys.Count(key => key.Storage == storageName);
        }

        public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() =>
            KernelJobsStorage.Contracts;

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
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ModuleStorageClaimResult<T>> ClaimAsync<T>(
            string moduleId,
            string storageName,
            ModuleStorageClaimRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ModuleStorageClaimRenewalResult> RenewClaimAsync(
            string moduleId,
            string storageName,
            ModuleStorageClaimRenewalRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ModuleStorageClaimRecoveryResult> RecoverClaimAsync(
            string moduleId,
            string storageName,
            ModuleStorageClaimRecoveryRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

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
    }
}
