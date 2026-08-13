using System.Text.Json;
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

        Assert.Equal(172, SharpClawActionCatalog.Kernel.Count);
        Assert.Equal(ExpectedFamilies, SharpClawActionCatalog.JobsFamilies);
        Assert.Equal(expectedKeys, SharpClawActionCatalog.Jobs.Select(key => key.Value));
        Assert.Equal(310, SharpClawActionCatalog.All.Count);
        Assert.Equal(310, SharpClawActionCatalog.All.Select(key => key.Value).Distinct().Count());

        var graph = new KernelGraphBuilder().Compile();

        Assert.Equal(310, KernelActionCatalog.Descriptors.Count);
        Assert.Equal(
            SharpClawActionCatalog.All.Select(key => key.Value),
            KernelActionCatalog.Descriptors.Select(entry => entry.Key.Value));
        Assert.All(SharpClawActionCatalog.All, key => Assert.True(graph.ContainsAction(key)));
    }

    [Fact]
    public void Every_jobs_key_has_one_explicit_profile_and_no_extra_manifest_entry()
    {
        var jobsEntries = KernelActionCatalog.Descriptors
            .Where(entry => entry.Key.Value.StartsWith("jobs.", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(138, jobsEntries.Length);
        Assert.Equal(
            SharpClawActionCatalog.Jobs.Select(key => key.Value),
            jobsEntries.Select(entry => entry.Key.Value));
        Assert.Equal(310, KernelActionCatalog.Descriptors.Select(entry => entry.Key.Value).Distinct().Count());

        AssertProfile("jobs.validate", KernelStandardActionProfile.Pure);
        AssertProfile("jobs.queue.persist", KernelStandardActionProfile.IdempotentEffect);
        AssertProfile("jobs.state.transition.commit", KernelStandardActionProfile.ConflictEffect);
        AssertProfile("jobs.external_call", KernelStandardActionProfile.ReceiptedEffect);
        AssertProfile("jobs.hold.resolve", KernelStandardActionProfile.Deferrable);
        AssertProfile("jobs.progress.report", KernelStandardActionProfile.Progress);
        AssertProfile("jobs.irreversible_effect", KernelStandardActionProfile.ReceiptedEffect);

        var read = Entry("jobs.read");
        Assert.False(read.HasIrreversibleEffects);
        Assert.Null(read.ContinuationPolicy);
        Assert.False(read.Capabilities.HasFlag(ActionInterceptionCapabilities.Defer));

        var signal = Entry("jobs.progress.report");
        Assert.False(signal.HasIrreversibleEffects);
        Assert.Equal(ActionRepeatKind.None, signal.RepeatPolicy.Kind);
        Assert.True(signal.Capabilities.HasFlag(ActionInterceptionCapabilities.Cancel));
        Assert.True(signal.Capabilities.HasFlag(ActionInterceptionCapabilities.Wrap));
        Assert.False(signal.Capabilities.HasFlag(ActionInterceptionCapabilities.Defer));
        Assert.False(signal.Capabilities.HasFlag(ActionInterceptionCapabilities.Repeat));

        var receipted = Entry("jobs.external_call");
        Assert.True(receipted.HasIrreversibleEffects);
        Assert.Equal(ActionRepeatKind.Receipted, receipted.RepeatPolicy.Kind);
        Assert.True(receipted.ContinuationPolicy?.Durable);

        var uncertain = Entry("jobs.external_effect.uncertain");
        Assert.True(uncertain.HasIrreversibleEffects);
        Assert.Equal(ActionRepeatKind.None, uncertain.RepeatPolicy.Kind);
        Assert.True(uncertain.ContinuationPolicy?.Durable);

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
    public void Jobs_manifest_uses_one_explicit_typed_descriptor_owner()
    {
        var root = Entry("jobs.read");
        var before = Entry("jobs.read.before");
        var after = Entry("jobs.read.after");

        Assert.Equal(typeof(JobActionInput<JsonElement>), root.InputPayloadType);
        Assert.Equal(typeof(JobActionResult<JsonElement>), root.ResultPayloadType);
        Assert.Equal(
            typeof(JobCheckpoint<JobActionInput<JsonElement>>),
            before.InputPayloadType);
        Assert.Equal(
            typeof(JobCheckpoint<JobActionResult<JsonElement>>),
            after.InputPayloadType);

        var graph = new KernelGraphBuilder().Compile();
        Assert.NotNull(graph.GetStandardJobsAction(new SharpClawActionKey("jobs.read")));
        Assert.NotNull(graph.GetStandardJobsBeforeAction(new SharpClawActionKey("jobs.read.before")));
        Assert.NotNull(graph.GetStandardJobsAfterAction(new SharpClawActionKey("jobs.read.after")));
        Assert.Throws<KernelActionExecutionException>(() =>
            graph.GetStandardAction(new SharpClawActionKey("jobs.read")));
    }

    [Theory]
    [InlineData("jobs.validate")]
    [InlineData("jobs.queue.persist")]
    [InlineData("jobs.state.transition.commit")]
    [InlineData("jobs.external_call")]
    [InlineData("jobs.hold.resolve")]
    [InlineData("jobs.progress.report")]
    [InlineData("jobs.irreversible_effect")]
    public async Task Representative_jobs_actions_use_the_standard_dispatcher(string keyValue)
    {
        var graph = new KernelGraphBuilder().Compile();
        var key = new SharpClawActionKey(keyValue);
        var outcome = await KernelTestExecution.CreateDispatcher(graph).RunAsync(
            graph.GetStandardJobsAction(key),
            new JobActionInput<JsonElement>(
                key,
                JsonSerializer.SerializeToElement(new { key = keyValue })),
            (_, _) => ValueTask.FromResult(
                new JobActionResult<JsonElement>(
                    key,
                    JsonSerializer.SerializeToElement(new { completed = true }))),
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
    }

    [Fact]
    public void Jobs_sensitive_hooks_require_exact_approval()
    {
        var key = new SharpClawActionKey("jobs.read");
        var registry = new KernelModuleRegistry();
        registry.Add(new SensitiveJobsHookModule(key));
        var grants = new Dictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
        {
            ["jobs.hook.module"] = new Dictionary<string, ActionInterceptionCapabilities>
            {
                [key.Value] = ActionInterceptionCapabilities.Inspect |
                              ActionInterceptionCapabilities.Wrap
            }
        };

        var missing = Assert.Throws<KernelGraphCompilationException>(() => registry.Compile(
            options: new KernelGraphCompileOptions
            {
                ActionModuleCapabilityGrants = grants
            }));
        Assert.Contains("Sensitive action", missing.Message, StringComparison.Ordinal);
        Assert.Contains(key.Value, missing.Message, StringComparison.Ordinal);

        var standard = new KernelGraphBuilder().Compile();
        var descriptor = standard.GetStandardJobsAction(key);
        var approved = registry.Compile(
            options: new KernelGraphCompileOptions
            {
                ActionModuleCapabilityGrants = grants,
                SensitiveActionApprovals =
                [
                    new KernelSensitiveActionApproval(
                        "jobs.hook.module",
                        key,
                        descriptor.Version,
                        typeof(JobActionInput<JsonElement>).AssemblyQualifiedName!,
                        typeof(JobActionResult<JsonElement>).AssemblyQualifiedName!,
                        KernelSchemaIdentity.Action(descriptor))
                ]
            });

        Assert.True(approved.ContainsAction(key));
    }

    [Fact]
    public async Task Jobs_progress_allows_cancel_and_denies_repeat()
    {
        var key = new SharpClawActionKey("jobs.progress.report");
        var cancelBuilder = new KernelGraphBuilder();
        cancelBuilder.Hooks.For(key).Use<ProgressCancelInterceptor>(new HookOrdering("cancel"));
        var cancelGraph = cancelBuilder.Compile();
        var terminalCalls = 0;
        var cancelled = await KernelTestExecution.CreateDispatcher(cancelGraph).RunAsync(
            cancelGraph.GetStandardJobsAction(key),
            new JobActionInput<JsonElement>(key, JsonSerializer.SerializeToElement(new { })),
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(new JobActionResult<JsonElement>(
                    key,
                    JsonSerializer.SerializeToElement(new { terminal = true })));
            },
            cancelGraph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Cancelled, cancelled.Kind);
        Assert.Equal(0, terminalCalls);

        var repeatBuilder = new KernelGraphBuilder();
        repeatBuilder.Hooks.For(key).Use<ProgressRepeatInterceptor>(new HookOrdering("repeat"));
        var repeatGraph = repeatBuilder.Compile();
        var repeated = await KernelTestExecution.CreateDispatcher(repeatGraph).RunAsync(
            repeatGraph.GetStandardJobsAction(key),
            new JobActionInput<JsonElement>(key, JsonSerializer.SerializeToElement(new { })),
            (_, _) => ValueTask.FromResult(new JobActionResult<JsonElement>(
                key,
                JsonSerializer.SerializeToElement(new { terminal = true }))),
            repeatGraph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, repeated.Kind);
        Assert.Contains("capability", repeated.Error?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static KernelStandardActionManifestEntry Entry(string key) =>
        Assert.Single(KernelActionCatalog.Descriptors, entry => entry.Key.Value == key);

    private static void AssertProfile(string key, KernelStandardActionProfile profile) =>
        Assert.Equal(profile, Entry(key).Profile);

    private sealed class SensitiveJobsHookModule(SharpClawActionKey key) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("jobs.hook.module", "Jobs hook module", "jobs_hook");

        public void Configure(ISharpClawModuleBuilder module) =>
            module.Hooks.For(key).Use<SensitiveJobsHook>(new HookOrdering("sensitive"));
    }

    private sealed class SensitiveJobsHook :
        IActionInterceptor<JobActionInput<JsonElement>, JobActionResult<JsonElement>>
    {
        public ValueTask<IActionOutcome<JobActionResult<JsonElement>>> InvokeAsync(
            ActionContext<JobActionInput<JsonElement>> context,
            IActionControl<JobActionInput<JsonElement>, JobActionResult<JsonElement>> control,
            CancellationToken cancellationToken) =>
            control.ProceedAsync(cancellationToken);
    }

    private sealed class ProgressCancelInterceptor :
        IActionInterceptor<JobActionInput<JsonElement>, JobActionResult<JsonElement>>
    {
        public ValueTask<IActionOutcome<JobActionResult<JsonElement>>> InvokeAsync(
            ActionContext<JobActionInput<JsonElement>> context,
            IActionControl<JobActionInput<JsonElement>, JobActionResult<JsonElement>> control,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IActionOutcome<JobActionResult<JsonElement>>>(
                control.Cancel("PROGRESS_CANCELLED", "Progress delivery was cancelled."));
    }

    private sealed class ProgressRepeatInterceptor :
        IActionInterceptor<JobActionInput<JsonElement>, JobActionResult<JsonElement>>
    {
        public async ValueTask<IActionOutcome<JobActionResult<JsonElement>>> InvokeAsync(
            ActionContext<JobActionInput<JsonElement>> context,
            IActionControl<JobActionInput<JsonElement>, JobActionResult<JsonElement>> control,
            CancellationToken cancellationToken) =>
            await control.RepeatAsync(
                new ActionRepeatRequest<JobActionInput<JsonElement>>(context.Action, "not permitted"),
                cancellationToken);
    }
}
