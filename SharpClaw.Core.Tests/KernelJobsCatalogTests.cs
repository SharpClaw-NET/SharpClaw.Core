using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelJobsCatalogTests
{
    private static readonly string[] ExpectedFamilies =
    [
        "jobs.submit",
        "jobs.validate",
        "jobs.identity.create",
        "jobs.queue.persist",
        "jobs.hold.evaluate",
        "jobs.hold.resolve",
        "jobs.dispatch",
        "jobs.start",
        "jobs.handler.invoke",
        "jobs.progress.report",
        "jobs.artifact.seal",
        "jobs.complete",
        "jobs.fail",
        "jobs.cancel",
        "jobs.cancel.request",
        "jobs.cancel.apply",
        "jobs.pause",
        "jobs.stop",
        "jobs.recovery",
        "jobs.recovery.scan",
        "jobs.recovery.classify",
        "jobs.retry",
        "jobs.retry.evaluate",
        "jobs.retry.schedule",
        "jobs.resume",
        "jobs.delete",
        "jobs.read",
        "jobs.list",
        "jobs.logs.read",
        "jobs.audit.read",
        "jobs.artifact.read",
        "jobs.event.deliver",
        "jobs.state.transition",
        "jobs.state.transition.prepare",
        "jobs.state.transition.commit",
        "jobs.state.transition.rollback",
        "jobs.persistence",
        "jobs.persistence.prepare",
        "jobs.persistence.commit",
        "jobs.persistence.rollback",
        "jobs.interruption.check",
        "jobs.external_call",
        "jobs.irreversible_effect",
        "jobs.external_effect.prepare",
        "jobs.external_effect.receipt",
        "jobs.external_effect.uncertain"
    ];

    [Fact]
    public void Complete_catalog_matches_the_proposal_and_compiles()
    {
        var expectedKeys = ExpectedFamilies.SelectMany(family => new[]
        {
            family,
            $"{family}.before",
            $"{family}.after"
        }).ToArray();
        var graph = CreateJobsGraph();

        Assert.Equal(172, SharpClawActionCatalog.Kernel.Count);
        Assert.Equal(ExpectedFamilies, SharpClawActionCatalog.JobsFamilies);
        Assert.Equal(expectedKeys, SharpClawActionCatalog.Jobs.Select(key => key.Value));
        Assert.Equal(310, SharpClawActionCatalog.All.Count);
        Assert.Equal(310, SharpClawActionCatalog.All.Select(key => key.Value).Distinct().Count());
        Assert.Equal(310, graph.ActionSnapshot.ActionGrants.Count);
        Assert.All(SharpClawActionCatalog.All, key => Assert.True(graph.ContainsAction(key)));
    }

    [Fact]
    public void Jobs_manifest_uses_module_typed_descriptors_without_placeholders()
    {
        var jobsEntries = KernelActionCatalog.Descriptors
            .Where(entry => entry.Key.Value.StartsWith("jobs.", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(138, jobsEntries.Length);
        Assert.All(jobsEntries, entry =>
        {
            Assert.Null(entry.InputPayloadType);
            Assert.Null(entry.ResultPayloadType);
        });

        AssertProfile("jobs.validate", KernelStandardActionProfile.Pure);
        AssertProfile("jobs.queue.persist", KernelStandardActionProfile.IdempotentEffect);
        AssertProfile("jobs.state.transition.commit", KernelStandardActionProfile.ConflictEffect);
        AssertProfile("jobs.external_call", KernelStandardActionProfile.ReceiptedEffect);
        AssertProfile("jobs.hold.resolve", KernelStandardActionProfile.Deferrable);
        AssertProfile("jobs.progress.report", KernelStandardActionProfile.Progress);
        AssertProfile("jobs.irreversible_effect", KernelStandardActionProfile.ReceiptedEffect);

        var read = Entry("jobs.read");
        Assert.True(read.ContainsSensitiveData);
        Assert.False(read.HasIrreversibleEffects);
        Assert.Null(read.ContinuationPolicy);
        Assert.False(read.Capabilities.HasFlag(ActionInterceptionCapabilities.Defer));

        var progress = Entry("jobs.progress.report");
        Assert.True(progress.Capabilities.HasFlag(ActionInterceptionCapabilities.Cancel));
        Assert.True(progress.Capabilities.HasFlag(ActionInterceptionCapabilities.Wrap));
        Assert.False(progress.Capabilities.HasFlag(ActionInterceptionCapabilities.Repeat));
        Assert.False(progress.Capabilities.HasFlag(ActionInterceptionCapabilities.Defer));

        var after = Entry("jobs.external_call.after");
        Assert.Equal(KernelStandardActionProfile.Observe, after.Profile);
        Assert.False(after.Capabilities.HasFlag(ActionInterceptionCapabilities.ReplaceInput));
        Assert.False(after.Capabilities.HasFlag(ActionInterceptionCapabilities.ReplaceResult));
        Assert.False(after.Capabilities.HasFlag(ActionInterceptionCapabilities.Cancel));
        Assert.False(after.Capabilities.HasFlag(ActionInterceptionCapabilities.Repeat));
        Assert.False(after.Capabilities.HasFlag(ActionInterceptionCapabilities.Defer));
        Assert.Null(after.ContinuationPolicy);
    }

    [Fact]
    public void Jobs_module_owns_distinct_typed_root_and_checkpoint_descriptors()
    {
        var graph = CreateJobsGraph();
        var readKey = new SharpClawActionKey("jobs.read");
        var progressKey = new SharpClawActionKey("jobs.progress.report");

        var read = graph.GetJobsAction<JobsInput<ReadFamily>, JobsResult<ReadFamily>>(readKey);
        var progress = graph.GetJobsAction<JobsInput<ProgressFamily>, JobsResult<ProgressFamily>>(progressKey);
        var before = graph.GetJobsBeforeAction<JobsInput<ReadFamily>>(
            new SharpClawActionKey("jobs.read.before"));
        var after = graph.GetJobsAfterAction<JobsResult<ReadFamily>>(
            new SharpClawActionKey("jobs.read.after"));

        Assert.Equal(typeof(JobsInput<ReadFamily>), read.GetType().GetGenericArguments()[0]);
        Assert.Equal(typeof(JobsResult<ReadFamily>), read.GetType().GetGenericArguments()[1]);
        Assert.Equal(typeof(JobsInput<ProgressFamily>), progress.GetType().GetGenericArguments()[0]);
        Assert.Equal(typeof(JobsResult<ProgressFamily>), progress.GetType().GetGenericArguments()[1]);
        Assert.Equal(
            typeof(JobCheckpoint<JobsInput<ReadFamily>>),
            before.GetType().GetGenericArguments()[0]);
        Assert.Equal(
            typeof(JobCheckpoint<JobsResult<ReadFamily>>),
            after.GetType().GetGenericArguments()[0]);
        Assert.NotEqual(typeof(JobsInput<ReadFamily>), typeof(JobsInput<ProgressFamily>));
        Assert.NotEqual(typeof(JobsResult<ReadFamily>), typeof(JobsResult<ProgressFamily>));

        Assert.Throws<KernelActionExecutionException>(() =>
            graph.GetJobsAction<JobsInput<ReadFamily>, JobsResult<ReadFamily>>(progressKey));
    }

    [Fact]
    public async Task Jobs_dispatch_supports_distinct_family_types()
    {
        var graph = CreateJobsGraph();
        var readKey = new SharpClawActionKey("jobs.read");
        var progressKey = new SharpClawActionKey("jobs.progress.report");
        var read = graph.GetJobsAction<JobsInput<ReadFamily>, JobsResult<ReadFamily>>(readKey);
        var progress = graph.GetJobsAction<JobsInput<ProgressFamily>, JobsResult<ProgressFamily>>(progressKey);
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);

        var readOutcome = await dispatcher.RunAsync(
            read,
            new JobsInput<ReadFamily>("read"),
            (context, _) => ValueTask.FromResult(new JobsResult<ReadFamily>(context.Action.Value + "-complete")),
            graph.ActionSnapshot,
            CancellationToken.None);
        var progressOutcome = await dispatcher.RunAsync(
            progress,
            new JobsInput<ProgressFamily>("progress"),
            (context, _) => ValueTask.FromResult(new JobsResult<ProgressFamily>(context.Action.Value + "-complete")),
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, readOutcome.Kind);
        Assert.Equal("read-complete", readOutcome.Result!.Value);
        Assert.Equal(ActionOutcomeKind.Completed, progressOutcome.Kind);
        Assert.Equal("progress-complete", progressOutcome.Result!.Value);
    }

    [Fact]
    public async Task Jobs_dispatch_supports_representative_typed_profiles()
    {
        var graph = CreateJobsGraph();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);

        Assert.Equal("jobs.validate", await Run<ValidateFamily>("jobs.validate"));
        Assert.Equal("jobs.queue.persist", await Run<QueuePersistFamily>("jobs.queue.persist"));
        Assert.Equal(
            "jobs.state.transition.commit",
            await Run<StateTransitionCommitFamily>("jobs.state.transition.commit"));
        Assert.Equal("jobs.external_call", await Run<ExternalCallFamily>("jobs.external_call"));
        Assert.Equal("jobs.hold.resolve", await Run<HoldResolveFamily>("jobs.hold.resolve"));
        Assert.Equal("jobs.cancel.request", await Run<CancelRequestFamily>("jobs.cancel.request"));
        Assert.Equal(
            "jobs.irreversible_effect",
            await Run<IrreversibleEffectFamily>("jobs.irreversible_effect"));

        async Task<string> Run<TFamily>(string keyValue)
        {
            var key = new SharpClawActionKey(keyValue);
            var descriptor = graph.GetJobsAction<JobsInput<TFamily>, JobsResult<TFamily>>(key);
            var outcome = await dispatcher.RunAsync(
                descriptor,
                new JobsInput<TFamily>(keyValue),
                (context, _) => ValueTask.FromResult(new JobsResult<TFamily>(context.Action.Value)),
                graph.ActionSnapshot,
                CancellationToken.None);

            Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
            return outcome.Result!.Value;
        }
    }

    [Fact]
    public void Partial_jobs_registration_fails_before_dispatch()
    {
        var builder = new KernelGraphBuilder();
        builder.Add(
            Descriptor<JobsInput<ReadFamily>, JobsResult<ReadFamily>>("jobs.read"),
            "partial.jobs.module");

        var exception = Assert.Throws<KernelGraphCompilationException>(() =>
            builder.Compile(null, new KernelGraphCompileOptions()));

        Assert.Contains("must register the catalog exactly once", exception.Message, StringComparison.Ordinal);
        Assert.Contains("jobs.submit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_element_jobs_placeholder_is_rejected()
    {
        var builder = new KernelGraphBuilder();
        builder.Add(
            Descriptor<JobActionInput<System.Text.Json.JsonElement>, JobActionResult<System.Text.Json.JsonElement>>(
                "jobs.read"),
            "placeholder.jobs.module");

        var exception = Assert.Throws<KernelGraphCompilationException>(() =>
            builder.Compile(null, new KernelGraphCompileOptions()));

        Assert.Contains("typed descriptor", exception.Message, StringComparison.Ordinal);
        Assert.Contains("jobs.read", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Jobs_family_rejects_a_before_checkpoint_for_the_wrong_root_input_type()
    {
        var exception = Assert.Throws<KernelGraphCompilationException>(() =>
            CreateJobsGraph(JobsDescriptorVariant.WrongBeforeType));

        Assert.Contains("jobs.read", exception.Message, StringComparison.Ordinal);
        Assert.Contains("before checkpoint types must match the root input type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Jobs_family_rejects_an_after_checkpoint_for_the_wrong_root_result_type()
    {
        var exception = Assert.Throws<KernelGraphCompilationException>(() =>
            CreateJobsGraph(JobsDescriptorVariant.WrongAfterType));

        Assert.Contains("jobs.read", exception.Message, StringComparison.Ordinal);
        Assert.Contains("after checkpoint types must match the root result type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Jobs_family_rejects_unequal_after_checkpoint_types()
    {
        var exception = Assert.Throws<KernelGraphCompilationException>(() =>
            CreateJobsGraph(JobsDescriptorVariant.UnequalAfterTypes));

        Assert.Contains("jobs.read.after", exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not match the catalog profile", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Jobs_family_requires_one_module_owner()
    {
        var registry = new KernelModuleRegistry();
        var jobs = new JobsDescriptorModule(
            skippedKeys: new HashSet<string>(StringComparer.Ordinal) { "jobs.read.before" });
        var other = new ReadBeforeOwnerModule();
        registry.Add(jobs);
        registry.Add(other);

        var exception = Assert.Throws<KernelGraphCompilationException>(() =>
            registry.Compile(null, CreateOptions(jobs, [other], includeHookApprovals: true)));

        Assert.Contains("jobs.read", exception.Message, StringComparison.Ordinal);
        Assert.Contains("one module owner", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replace_input_cannot_redirect_the_operation_key()
    {
        var hook = new JobsHookModule<ReadReplaceInputInterceptor, JobsInput<ReadFamily>, JobsResult<ReadFamily>>(
            new("jobs.replace.input", "Jobs replace input", "jobs_replace_input"),
            new SharpClawActionKey("jobs.read"),
            new("replace-input"));
        var graph = CreateJobsGraph(hook);
        var descriptor = graph.GetJobsAction<JobsInput<ReadFamily>, JobsResult<ReadFamily>>(
            new SharpClawActionKey("jobs.read"));
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var seenKey = default(SharpClawActionKey);
        var outcome = await dispatcher.RunAsync(
            descriptor,
            new JobsInput<ReadFamily>("original"),
            (context, _) =>
            {
                Assert.Equal("replaced", context.Action.Value);
                return ValueTask.FromResult(new JobsResult<ReadFamily>(context.Action.Value));
            },
            graph.ActionSnapshot,
            CancellationToken.None);

        seenKey = ReadReplaceInputInterceptor.LastActionKey;
        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("jobs.read", seenKey.Value);
        Assert.Null(typeof(JobsInput<ReadFamily>).GetProperty("ActionKey"));
    }

    [Fact]
    public void Jobs_sensitive_hooks_require_exact_approval()
    {
        var hook = new JobsHookModule<SensitiveJobsHook, JobsInput<ReadFamily>, JobsResult<ReadFamily>>(
            new("jobs.hook.module", "Jobs hook module", "jobs_hook"),
            new SharpClawActionKey("jobs.read"),
            new("sensitive"));
        var (missingRegistry, missingJobs) = BuildJobsRegistry(false, hook);
        var missing = Assert.Throws<KernelGraphCompilationException>(() => missingRegistry.Compile(
            null,
            CreateOptions(missingJobs, [hook], includeHookApprovals: false)));

        Assert.Contains("Sensitive action", missing.Message, StringComparison.Ordinal);
        Assert.Contains("jobs.read", missing.Message, StringComparison.Ordinal);

        var approvedGraph = CreateJobsGraph(hook);
        Assert.True(approvedGraph.ContainsAction(new SharpClawActionKey("jobs.read")));
    }

    [Fact]
    public async Task Jobs_progress_allows_cancel_wrap_and_denies_repeat()
    {
        var key = new SharpClawActionKey("jobs.progress.report");
        var cancelHook = new JobsHookModule<ProgressCancelInterceptor, JobsInput<ProgressFamily>, JobsResult<ProgressFamily>>(
            new("jobs.progress.cancel", "Jobs progress cancel", "jobs_progress_cancel"),
            key,
            new("cancel"));
        var cancelGraph = CreateJobsGraph(cancelHook);
        var cancelDescriptor = cancelGraph.GetJobsAction<JobsInput<ProgressFamily>, JobsResult<ProgressFamily>>(key);
        var cancelTerminalCalls = 0;
        var cancelled = await KernelTestExecution.CreateDispatcher(cancelGraph).RunAsync(
            cancelDescriptor,
            new JobsInput<ProgressFamily>("progress"),
            (_, _) =>
            {
                cancelTerminalCalls++;
                return ValueTask.FromResult(new JobsResult<ProgressFamily>("terminal"));
            },
            cancelGraph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Cancelled, cancelled.Kind);
        Assert.Equal(0, cancelTerminalCalls);

        var wrapHook = new JobsHookModule<ProgressWrapInterceptor, JobsInput<ProgressFamily>, JobsResult<ProgressFamily>>(
            new("jobs.progress.wrap", "Jobs progress wrap", "jobs_progress_wrap"),
            key,
            new("wrap"));
        var wrapGraph = CreateJobsGraph(wrapHook);
        var wrapTerminalCalls = 0;
        ProgressWrapInterceptor.Invoked = false;
        var wrapped = await KernelTestExecution.CreateDispatcher(wrapGraph).RunAsync(
            wrapGraph.GetJobsAction<JobsInput<ProgressFamily>, JobsResult<ProgressFamily>>(key),
            new JobsInput<ProgressFamily>("progress"),
            (_, _) =>
            {
                wrapTerminalCalls++;
                return ValueTask.FromResult(new JobsResult<ProgressFamily>("terminal"));
            },
            wrapGraph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, wrapped.Kind);
        Assert.Equal(1, wrapTerminalCalls);
        Assert.Equal("terminal", wrapped.Result!.Value);
        Assert.True(ProgressWrapInterceptor.Invoked);

        var repeatHook = new JobsHookModule<ProgressRepeatInterceptor, JobsInput<ProgressFamily>, JobsResult<ProgressFamily>>(
            new("jobs.progress.repeat", "Jobs progress repeat", "jobs_progress_repeat"),
            key,
            new("repeat"));
        var repeatGraph = CreateJobsGraph(repeatHook);
        var repeated = await KernelTestExecution.CreateDispatcher(repeatGraph).RunAsync(
            repeatGraph.GetJobsAction<JobsInput<ProgressFamily>, JobsResult<ProgressFamily>>(key),
            new JobsInput<ProgressFamily>("progress"),
            (_, _) => ValueTask.FromResult(new JobsResult<ProgressFamily>("terminal")),
            repeatGraph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, repeated.Kind);
        Assert.Contains("capability", repeated.Error?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static KernelGraph CreateJobsGraph(params IJobsHookModule[] hooks)
    {
        var (registry, jobs) = BuildJobsRegistry(true, hooks);
        return registry.Compile(null, CreateOptions(jobs, hooks, includeHookApprovals: true));
    }

    private static KernelGraph CreateJobsGraph(
        JobsDescriptorVariant variant,
        params IJobsHookModule[] hooks)
    {
        var (registry, jobs) = BuildJobsRegistry(variant, hooks);
        return registry.Compile(null, CreateOptions(jobs, hooks, includeHookApprovals: true));
    }

    private static (KernelModuleRegistry Registry, JobsDescriptorModule Jobs) BuildJobsRegistry(
        bool includeHookApprovals,
        params IJobsHookModule[] hooks)
    {
        return BuildJobsRegistry(JobsDescriptorVariant.Valid, hooks);
    }

    private static (KernelModuleRegistry Registry, JobsDescriptorModule Jobs) BuildJobsRegistry(
        JobsDescriptorVariant variant,
        params IJobsHookModule[] hooks)
    {
        var registry = new KernelModuleRegistry();
        var jobs = new JobsDescriptorModule(variant);
        registry.Add(jobs);
        foreach (var hook in hooks)
            registry.Add(hook);
        return (registry, jobs);
    }

    private static KernelGraphCompileOptions CreateOptions(
        JobsDescriptorModule jobs,
        IReadOnlyList<IJobsHookModule> hooks,
        bool includeHookApprovals)
    {
        var grants = new Dictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
        {
            [jobs.Identity.Id] = jobs.Grants
        };
        var approvals = jobs.Approvals.ToList();
        foreach (var hook in hooks)
        {
            grants[hook.Identity.Id] = hook.Grants;
            if (includeHookApprovals)
                approvals.AddRange(hook.Approvals);
        }

        return new KernelGraphCompileOptions
        {
            ActionModuleCapabilityGrants = grants,
            SensitiveActionApprovals = approvals
        };
    }

    private static KernelStandardActionManifestEntry Entry(string key) =>
        Assert.Single(KernelActionCatalog.Descriptors, entry => entry.Key.Value == key);

    private static void AssertProfile(string key, KernelStandardActionProfile profile) =>
        Assert.Equal(profile, Entry(key).Profile);

    private static ActionDescriptor<TAction, TResult> Descriptor<TAction, TResult>(string key)
    {
        var entry = Entry(key);
        return new(
            entry.Key,
            entry.Version,
            entry.Category,
            entry.Capabilities,
            entry.ContainsSensitiveData,
            entry.HasIrreversibleEffects,
            entry.RepeatPolicy,
            entry.ContinuationPolicy,
            entry.DefaultTimeout)
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            SafePoints = entry.SafePoints
        };
    }

    private enum JobsDescriptorVariant
    {
        Valid,
        WrongBeforeType,
        WrongAfterType,
        UnequalAfterTypes
    }

    private interface IJobsGrantSource
    {
        string ModuleId { get; }

        IReadOnlyDictionary<string, ActionInterceptionCapabilities> Grants { get; }

        IReadOnlyList<KernelSensitiveActionApproval> Approvals { get; }
    }

    private interface IJobsHookModule : ISharpClawModule, IJobsGrantSource;

    private sealed class JobsDescriptorModule : ISharpClawModule, IJobsGrantSource
    {
        private readonly JobsDescriptorVariant _variant;
        private readonly IReadOnlySet<string> _skippedKeys;
        private readonly Dictionary<string, ActionInterceptionCapabilities> _grants = new(StringComparer.Ordinal);
        private readonly List<KernelSensitiveActionApproval> _approvals = [];

        public JobsDescriptorModule(
            JobsDescriptorVariant variant = JobsDescriptorVariant.Valid,
            IReadOnlySet<string>? skippedKeys = null)
        {
            _variant = variant;
            _skippedKeys = skippedKeys ?? new HashSet<string>(StringComparer.Ordinal);
        }

        public ModuleIdentity Identity { get; } = new("jobs.module", "Jobs module", "jobs");

        public string ModuleId => Identity.Id;

        public IReadOnlyDictionary<string, ActionInterceptionCapabilities> Grants => _grants;

        public IReadOnlyList<KernelSensitiveActionApproval> Approvals => _approvals;

        public void Configure(ISharpClawModuleBuilder module)
        {
            AddFamily<SubmitFamily>(module, "jobs.submit");
            AddFamily<ValidateFamily>(module, "jobs.validate");
            AddFamily<IdentityCreateFamily>(module, "jobs.identity.create");
            AddFamily<QueuePersistFamily>(module, "jobs.queue.persist");
            AddFamily<HoldEvaluateFamily>(module, "jobs.hold.evaluate");
            AddFamily<HoldResolveFamily>(module, "jobs.hold.resolve");
            AddFamily<DispatchFamily>(module, "jobs.dispatch");
            AddFamily<StartFamily>(module, "jobs.start");
            AddFamily<HandlerInvokeFamily>(module, "jobs.handler.invoke");
            AddFamily<ProgressFamily>(module, "jobs.progress.report");
            AddFamily<ArtifactSealFamily>(module, "jobs.artifact.seal");
            AddFamily<CompleteFamily>(module, "jobs.complete");
            AddFamily<FailFamily>(module, "jobs.fail");
            AddFamily<CancelFamily>(module, "jobs.cancel");
            AddFamily<CancelRequestFamily>(module, "jobs.cancel.request");
            AddFamily<CancelApplyFamily>(module, "jobs.cancel.apply");
            AddFamily<PauseFamily>(module, "jobs.pause");
            AddFamily<StopFamily>(module, "jobs.stop");
            AddFamily<RecoveryFamily>(module, "jobs.recovery");
            AddFamily<RecoveryScanFamily>(module, "jobs.recovery.scan");
            AddFamily<RecoveryClassifyFamily>(module, "jobs.recovery.classify");
            AddFamily<RetryFamily>(module, "jobs.retry");
            AddFamily<RetryEvaluateFamily>(module, "jobs.retry.evaluate");
            AddFamily<RetryScheduleFamily>(module, "jobs.retry.schedule");
            AddFamily<ResumeFamily>(module, "jobs.resume");
            AddFamily<DeleteFamily>(module, "jobs.delete");
            AddFamily<ReadFamily>(module, "jobs.read");
            AddFamily<ListFamily>(module, "jobs.list");
            AddFamily<LogsReadFamily>(module, "jobs.logs.read");
            AddFamily<AuditReadFamily>(module, "jobs.audit.read");
            AddFamily<ArtifactReadFamily>(module, "jobs.artifact.read");
            AddFamily<EventDeliverFamily>(module, "jobs.event.deliver");
            AddFamily<StateTransitionFamily>(module, "jobs.state.transition");
            AddFamily<StateTransitionPrepareFamily>(module, "jobs.state.transition.prepare");
            AddFamily<StateTransitionCommitFamily>(module, "jobs.state.transition.commit");
            AddFamily<StateTransitionRollbackFamily>(module, "jobs.state.transition.rollback");
            AddFamily<PersistenceFamily>(module, "jobs.persistence");
            AddFamily<PersistencePrepareFamily>(module, "jobs.persistence.prepare");
            AddFamily<PersistenceCommitFamily>(module, "jobs.persistence.commit");
            AddFamily<PersistenceRollbackFamily>(module, "jobs.persistence.rollback");
            AddFamily<InterruptionCheckFamily>(module, "jobs.interruption.check");
            AddFamily<ExternalCallFamily>(module, "jobs.external_call");
            AddFamily<IrreversibleEffectFamily>(module, "jobs.irreversible_effect");
            AddFamily<ExternalEffectPrepareFamily>(module, "jobs.external_effect.prepare");
            AddFamily<ExternalEffectReceiptFamily>(module, "jobs.external_effect.receipt");
            AddFamily<ExternalEffectUncertainFamily>(module, "jobs.external_effect.uncertain");
        }

        private void AddFamily<TFamily>(ISharpClawModuleBuilder module, string family)
        {
            var contract = new JobActionContract<JobsInput<TFamily>, JobsResult<TFamily>>(
                Descriptor<JobCheckpoint<JobsInput<TFamily>>, JobCheckpoint<JobsInput<TFamily>>>($"{family}.before"),
                Descriptor<JobsInput<TFamily>, JobsResult<TFamily>>(family),
                Descriptor<JobCheckpoint<JobsResult<TFamily>>, JobCheckpoint<JobsResult<TFamily>>>($"{family}.after"));

            if (family == "jobs.read")
            {
                switch (_variant)
                {
                    case JobsDescriptorVariant.WrongBeforeType:
                        AddDescriptor(
                            module,
                            Descriptor<JobCheckpoint<JobsInput<ProgressFamily>>, JobCheckpoint<JobsInput<ProgressFamily>>>(
                                $"{family}.before"));
                        AddDescriptor(module, contract.Action);
                        AddDescriptor(module, contract.After);
                        return;
                    case JobsDescriptorVariant.WrongAfterType:
                        AddDescriptor(module, contract.Before);
                        AddDescriptor(module, contract.Action);
                        AddDescriptor(
                            module,
                            Descriptor<JobCheckpoint<JobsResult<ProgressFamily>>, JobCheckpoint<JobsResult<ProgressFamily>>>(
                                $"{family}.after"));
                        return;
                    case JobsDescriptorVariant.UnequalAfterTypes:
                        AddDescriptor(module, contract.Before);
                        AddDescriptor(module, contract.Action);
                        AddDescriptor(
                            module,
                            Descriptor<JobCheckpoint<JobsResult<ReadFamily>>, JobCheckpoint<JobsResult<ProgressFamily>>>(
                                $"{family}.after"));
                        return;
                }
            }

            AddDescriptor(module, contract.Before);
            AddDescriptor(module, contract.Action);
            AddDescriptor(module, contract.After);
        }

        private void AddDescriptor<TAction, TResult>(
            ISharpClawModuleBuilder module,
            ActionDescriptor<TAction, TResult> descriptor)
        {
            if (_skippedKeys.Contains(descriptor.Key.Value))
                return;

            module.Actions.Add(descriptor);
            AddAuthority(descriptor);
        }

        private void AddAuthority<TAction, TResult>(ActionDescriptor<TAction, TResult> descriptor)
        {
            _grants[descriptor.Key.Value] = descriptor.Capabilities;
            _approvals.Add(new KernelSensitiveActionApproval(
                Identity.Id,
                descriptor.Key,
                descriptor.Version,
                typeof(TAction).AssemblyQualifiedName!,
                typeof(TResult).AssemblyQualifiedName!,
                KernelSchemaIdentity.Action(descriptor)));
        }
    }

    private sealed class ReadBeforeOwnerModule : IJobsHookModule
    {
        private static readonly ActionDescriptor<
            JobCheckpoint<JobsInput<ReadFamily>>,
            JobCheckpoint<JobsInput<ReadFamily>>> DescriptorInstance =
            Descriptor<JobCheckpoint<JobsInput<ReadFamily>>, JobCheckpoint<JobsInput<ReadFamily>>>(
                "jobs.read.before");

        public ModuleIdentity Identity { get; } =
            new("jobs.other.module", "Other Jobs module", "jobs-other");

        public string ModuleId => Identity.Id;

        public IReadOnlyDictionary<string, ActionInterceptionCapabilities> Grants { get; } =
            new Dictionary<string, ActionInterceptionCapabilities>
            {
                [DescriptorInstance.Key.Value] = DescriptorInstance.Capabilities
            };

        public IReadOnlyList<KernelSensitiveActionApproval> Approvals { get; } =
        [
            new KernelSensitiveActionApproval(
                "jobs.other.module",
                DescriptorInstance.Key,
                DescriptorInstance.Version,
                typeof(JobCheckpoint<JobsInput<ReadFamily>>).AssemblyQualifiedName!,
                typeof(JobCheckpoint<JobsInput<ReadFamily>>).AssemblyQualifiedName!,
                KernelSchemaIdentity.Action(DescriptorInstance))
        ];

        public void Configure(ISharpClawModuleBuilder module) =>
            module.Actions.Add(DescriptorInstance);
    }

    private sealed class JobsHookModule<TInterceptor, TAction, TResult>(
        ModuleIdentity identity,
        SharpClawActionKey key,
        HookOrdering ordering) : IJobsHookModule
    {
        public ModuleIdentity Identity { get; } = identity;

        public string ModuleId => Identity.Id;

        public IReadOnlyDictionary<string, ActionInterceptionCapabilities> Grants { get; } =
            new Dictionary<string, ActionInterceptionCapabilities>
            {
                [key.Value] = Entry(key.Value).Capabilities
            };

        public IReadOnlyList<KernelSensitiveActionApproval> Approvals { get; } =
        [
            new KernelSensitiveActionApproval(
                identity.Id,
                Descriptor<TAction, TResult>(key.Value).Key,
                Descriptor<TAction, TResult>(key.Value).Version,
                typeof(TAction).AssemblyQualifiedName!,
                typeof(TResult).AssemblyQualifiedName!,
                KernelSchemaIdentity.Action(Descriptor<TAction, TResult>(key.Value)))
        ];

        public void Configure(ISharpClawModuleBuilder module) =>
            module.Hooks.For(key).Use<TInterceptor>(ordering);
    }

    private sealed record JobsInput<TFamily>(string Value);

    private sealed record JobsResult<TFamily>(string Value);

    private sealed class SensitiveJobsHook :
        IActionInterceptor<JobsInput<ReadFamily>, JobsResult<ReadFamily>>
    {
        public ValueTask<IActionOutcome<JobsResult<ReadFamily>>> InvokeAsync(
            ActionContext<JobsInput<ReadFamily>> context,
            IActionControl<JobsInput<ReadFamily>, JobsResult<ReadFamily>> control,
            CancellationToken cancellationToken) =>
            control.ProceedAsync(cancellationToken);
    }

    private sealed class ReadReplaceInputInterceptor :
        IActionInterceptor<JobsInput<ReadFamily>, JobsResult<ReadFamily>>
    {
        public static SharpClawActionKey LastActionKey { get; private set; }

        public ValueTask<IActionOutcome<JobsResult<ReadFamily>>> InvokeAsync(
            ActionContext<JobsInput<ReadFamily>> context,
            IActionControl<JobsInput<ReadFamily>, JobsResult<ReadFamily>> control,
            CancellationToken cancellationToken)
        {
            LastActionKey = context.ActionKey;
            return control.ProceedWithInputAsync(
                new ActionReplacement<JobsInput<ReadFamily>>(
                    new JobsInput<ReadFamily>("replaced"),
                    "replace value only"),
                cancellationToken);
        }
    }

    private sealed class ProgressCancelInterceptor :
        IActionInterceptor<JobsInput<ProgressFamily>, JobsResult<ProgressFamily>>
    {
        public ValueTask<IActionOutcome<JobsResult<ProgressFamily>>> InvokeAsync(
            ActionContext<JobsInput<ProgressFamily>> context,
            IActionControl<JobsInput<ProgressFamily>, JobsResult<ProgressFamily>> control,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IActionOutcome<JobsResult<ProgressFamily>>>(
                control.Cancel("PROGRESS_CANCELLED", "Progress delivery was cancelled."));
    }

    private sealed class ProgressWrapInterceptor :
        IActionInterceptor<JobsInput<ProgressFamily>, JobsResult<ProgressFamily>>
    {
        public static bool Invoked { get; set; }

        public async ValueTask<IActionOutcome<JobsResult<ProgressFamily>>> InvokeAsync(
            ActionContext<JobsInput<ProgressFamily>> context,
            IActionControl<JobsInput<ProgressFamily>, JobsResult<ProgressFamily>> control,
            CancellationToken cancellationToken)
        {
            Invoked = true;
            var outcome = await control.ProceedAsync(cancellationToken);
            if (outcome.Kind != ActionOutcomeKind.Completed)
                return outcome;
            return outcome;
        }
    }

    private sealed class ProgressRepeatInterceptor :
        IActionInterceptor<JobsInput<ProgressFamily>, JobsResult<ProgressFamily>>
    {
        public async ValueTask<IActionOutcome<JobsResult<ProgressFamily>>> InvokeAsync(
            ActionContext<JobsInput<ProgressFamily>> context,
            IActionControl<JobsInput<ProgressFamily>, JobsResult<ProgressFamily>> control,
            CancellationToken cancellationToken) =>
            await control.RepeatAsync(
                new ActionRepeatRequest<JobsInput<ProgressFamily>>(context.Action, "not permitted"),
                cancellationToken);
    }

    private sealed record SubmitFamily;
    private sealed record ValidateFamily;
    private sealed record IdentityCreateFamily;
    private sealed record QueuePersistFamily;
    private sealed record HoldEvaluateFamily;
    private sealed record HoldResolveFamily;
    private sealed record DispatchFamily;
    private sealed record StartFamily;
    private sealed record HandlerInvokeFamily;
    private sealed record ProgressFamily;
    private sealed record ArtifactSealFamily;
    private sealed record CompleteFamily;
    private sealed record FailFamily;
    private sealed record CancelFamily;
    private sealed record CancelRequestFamily;
    private sealed record CancelApplyFamily;
    private sealed record PauseFamily;
    private sealed record StopFamily;
    private sealed record RecoveryFamily;
    private sealed record RecoveryScanFamily;
    private sealed record RecoveryClassifyFamily;
    private sealed record RetryFamily;
    private sealed record RetryEvaluateFamily;
    private sealed record RetryScheduleFamily;
    private sealed record ResumeFamily;
    private sealed record DeleteFamily;
    private sealed record ReadFamily;
    private sealed record ListFamily;
    private sealed record LogsReadFamily;
    private sealed record AuditReadFamily;
    private sealed record ArtifactReadFamily;
    private sealed record EventDeliverFamily;
    private sealed record StateTransitionFamily;
    private sealed record StateTransitionPrepareFamily;
    private sealed record StateTransitionCommitFamily;
    private sealed record StateTransitionRollbackFamily;
    private sealed record PersistenceFamily;
    private sealed record PersistencePrepareFamily;
    private sealed record PersistenceCommitFamily;
    private sealed record PersistenceRollbackFamily;
    private sealed record InterruptionCheckFamily;
    private sealed record ExternalCallFamily;
    private sealed record IrreversibleEffectFamily;
    private sealed record ExternalEffectPrepareFamily;
    private sealed record ExternalEffectReceiptFamily;
    private sealed record ExternalEffectUncertainFamily;
}
