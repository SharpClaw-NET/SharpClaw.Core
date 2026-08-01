using System.Collections.Concurrent;
using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelBoundaryTests
{
    [Fact]
    public async Task Proceed_with_input_requires_replace_input_and_wrap()
    {
        var key = new SharpClawActionKey("boundary.replace");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key, ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.ReplaceInput));
        builder.Hooks.For(key).Use<UnauthorizedInputInterceptor>(Order("replace"));
        var graph = builder.Compile();
        var terminalCalls = 0;

        var outcome = await new KernelActionDispatcher(graph).RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult<object>("terminal");
            },
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("ACTION_CAPABILITY_DENIED", outcome.Error?.Code);
        Assert.Equal(0, terminalCalls);
    }

    [Fact]
    public async Task Proceed_requires_wrap_even_when_inspection_is_granted()
    {
        var key = new SharpClawActionKey("boundary.wrap");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key, ActionInterceptionCapabilities.Inspect));
        builder.Hooks.For(key).Use<ProceedInterceptor>(Order("wrap"));
        var graph = builder.Compile();

        var outcome = await new KernelActionDispatcher(graph).RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (_, _) => ValueTask.FromResult<object>("terminal"),
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal("ACTION_CAPABILITY_DENIED", outcome.Error?.Code);
    }

    [Fact]
    public async Task Forged_and_reused_outcomes_cannot_replace_a_control_issued_outcome()
    {
        var forgedKey = new SharpClawActionKey("boundary.forged");
        var forgedBuilder = new KernelGraphBuilder(false);
        forgedBuilder.Add(Descriptor(forgedKey));
        forgedBuilder.Hooks.For(forgedKey).Use<ForgedInterceptor>(Order("forged"));
        var forgedGraph = forgedBuilder.Compile();
        var forged = await new KernelActionDispatcher(forgedGraph).RunAsync(
            forgedGraph.GetStandardAction(forgedKey),
            new KernelActionEnvelope(forgedKey, "input"),
            (_, _) => ValueTask.FromResult<object>("terminal"),
            forgedGraph.ActionSnapshot,
            CancellationToken.None);
        Assert.Equal("ACTION_FORGED_OUTCOME", forged.Error?.Code);

        var consumedKey = new SharpClawActionKey("boundary.consumed");
        var consumedBuilder = new KernelGraphBuilder(false);
        consumedBuilder.Add(Descriptor(consumedKey));
        consumedBuilder.Hooks.For(consumedKey).Use<FailThenProceedInterceptor>(Order("consumed"));
        var consumedGraph = consumedBuilder.Compile();
        var consumed = await new KernelActionDispatcher(consumedGraph).RunAsync(
            consumedGraph.GetStandardAction(consumedKey),
            new KernelActionEnvelope(consumedKey, "input"),
            (_, _) => ValueTask.FromResult<object>("terminal"),
            consumedGraph.ActionSnapshot,
            CancellationToken.None);
        Assert.Equal("FAILED", consumed.Error?.Code);

        var postProceedKey = new SharpClawActionKey("boundary.post-proceed-control");
        var postProceedBuilder = new KernelGraphBuilder(false);
        postProceedBuilder.Add(Descriptor(postProceedKey));
        postProceedBuilder.Hooks.For(postProceedKey).Use<ProceedThenFailInterceptor>(Order("post-proceed"));
        var postProceedGraph = postProceedBuilder.Compile();
        var terminalCalls = 0;
        var postProceed = await new KernelActionDispatcher(postProceedGraph).RunAsync(
            postProceedGraph.GetStandardAction(postProceedKey),
            new KernelActionEnvelope(postProceedKey, "input"),
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult<object>("terminal");
            },
            postProceedGraph.ActionSnapshot,
            CancellationToken.None);
        Assert.Equal(ActionOutcomeKind.Completed, postProceed.Kind);
        Assert.Equal("terminal", postProceed.Result);
        Assert.Equal(1, terminalCalls);
    }

    [Fact]
    public async Task Repeat_advances_attempt_and_enforces_the_declared_bound()
    {
        AttemptInterceptor.Attempts.Clear();
        var key = new SharpClawActionKey("boundary.repeat");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(
            key,
            KernelActionCapabilities,
            new ActionRepeatPolicy(ActionRepeatKind.Idempotent, 3, TimeSpan.FromMilliseconds(1), "scope")));
        builder.Hooks.For(key).Use<AttemptInterceptor>(Order("repeat"));
        var graph = builder.Compile();
        var outcome = await new KernelActionDispatcher(graph).RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (_, _) => ValueTask.FromResult<object>("done"),
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal([1, 2, 3], AttemptInterceptor.Attempts);
    }

    [Fact]
    public async Task Conflict_and_receipted_repeat_policies_require_their_evidence()
    {
        var conflictKey = new SharpClawActionKey("boundary.conflict");
        var conflictBuilder = new KernelGraphBuilder(false);
        conflictBuilder.Add(Descriptor(
            conflictKey,
            KernelActionCapabilities,
            new ActionRepeatPolicy(ActionRepeatKind.ConflictOnly, 2, TimeSpan.Zero, "scope")));
        conflictBuilder.Hooks.For(conflictKey).Use<NonConflictRepeatInterceptor>(Order("conflict"));
        var conflictGraph = conflictBuilder.Compile();
        var conflict = await new KernelActionDispatcher(conflictGraph).RunAsync(
            conflictGraph.GetStandardAction(conflictKey),
            new KernelActionEnvelope(conflictKey, "input"),
            (_, _) => ValueTask.FromResult<object>("done"),
            conflictGraph.ActionSnapshot,
            CancellationToken.None);
        Assert.Equal("ACTION_REPEAT_DENIED", conflict.Error?.Code);

        var receiptKey = new SharpClawActionKey("boundary.receipt");
        var receiptBuilder = new KernelGraphBuilder(false);
        receiptBuilder.Add(Descriptor(
            receiptKey,
            KernelActionCapabilities,
            new ActionRepeatPolicy(ActionRepeatKind.Receipted, 2, TimeSpan.Zero, "scope")));
        receiptBuilder.Hooks.For(receiptKey).Use<ReceiptRepeatInterceptor>(Order("receipt"));
        var receiptGraph = receiptBuilder.Compile();
        var receipt = await new KernelActionDispatcher(receiptGraph).RunAsync(
            receiptGraph.GetStandardAction(receiptKey),
            new KernelActionEnvelope(receiptKey, "input"),
            (_, _) => ValueTask.FromResult<object>("done"),
            receiptGraph.ActionSnapshot,
            CancellationToken.None);
        Assert.Equal("ACTION_REPEAT_DENIED", receipt.Error?.Code);
    }

    [Fact]
    public async Task Required_uncertainty_throws_the_contract_uncertainty_exception()
    {
        var key = new SharpClawActionKey("boundary.uncertain");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        var graph = builder.Compile();
        var exception = await Assert.ThrowsAsync<ActionOutcomeUncertainException>(async () =>
            await new KernelActionDispatcher(
                graph,
                new StoreBackedContinuationHost(new TestDurableContinuationStore()))
                .RunRequiredAsync(
                graph.GetStandardAction(key),
                new KernelActionEnvelope(key, "input"),
                (_, _) => throw new ActionOutcomeUncertainException(new ActionUncertainty(
                    "UNKNOWN_RECEIPT",
                    "The receipt is unavailable.",
                    ActionExecutionStage.TerminalReturned,
                    "receipt",
                    new ActionRecoveryReference(Guid.NewGuid(), key, 1, Guid.NewGuid()),
                    DateTimeOffset.UtcNow)),
                graph.ActionSnapshot,
                CancellationToken.None));

        Assert.Equal("UNKNOWN_RECEIPT", exception.Uncertainty.Code);
    }

    [Fact]
    public async Task Hook_timeout_fails_or_continues_only_when_the_ordering_allows_it()
    {
        var failKey = new SharpClawActionKey("boundary.timeout.fail");
        var failBuilder = new KernelGraphBuilder(false);
        failBuilder.Add(Descriptor(failKey) with { ContinuationPolicy = null });
        failBuilder.Hooks.For(failKey).Use<SlowInterceptor>(new HookOrdering(
            "slow-fail",
            Timeout: TimeSpan.FromMilliseconds(5),
            FailurePolicy: HookFailurePolicy.FailAction));
        var failGraph = failBuilder.Compile();
        var failed = await new KernelActionDispatcher(failGraph).RunAsync(
            failGraph.GetStandardAction(failKey),
            new KernelActionEnvelope(failKey, "input"),
            (_, _) => ValueTask.FromResult<object>("terminal"),
            failGraph.ActionSnapshot,
            CancellationToken.None);
        Assert.Equal("ACTION_HOOK_TIMEOUT", failed.Error?.Code);

        var bestEffortKey = new SharpClawActionKey("boundary.timeout.best");
        var bestEffortBuilder = new KernelGraphBuilder(false);
        bestEffortBuilder.Add(Descriptor(bestEffortKey) with { ContinuationPolicy = null });
        bestEffortBuilder.Hooks.For(bestEffortKey).Use<ThrowingTimeoutInterceptor>(new HookOrdering(
            "slow-best",
            Timeout: TimeSpan.FromMilliseconds(5),
            FailurePolicy: HookFailurePolicy.BestEffort));
        var bestEffortGraph = bestEffortBuilder.Compile();
        var completed = await new KernelActionDispatcher(bestEffortGraph).RunAsync(
            bestEffortGraph.GetStandardAction(bestEffortKey),
            new KernelActionEnvelope(bestEffortKey, "input"),
            (_, _) => ValueTask.FromResult<object>("terminal"),
            bestEffortGraph.ActionSnapshot,
            CancellationToken.None);
        Assert.Equal(ActionOutcomeKind.Completed, completed.Kind);
    }

    [Fact]
    public async Task Durable_defer_requires_durable_host_state_and_claim_authority()
    {
        var key = new SharpClawActionKey("boundary.defer");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        builder.Hooks.For(key).Use<DeferBoundaryInterceptor>(Order("defer"));
        var graph = builder.Compile();
        var processOnly = await new KernelActionDispatcher(graph).RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (_, _) => ValueTask.FromResult<object>("unused"),
            graph.ActionSnapshot,
            CancellationToken.None);
        Assert.Equal("ACTION_CONTINUATION_DENIED", processOnly.Error?.Code);

        var host = new StoreBackedContinuationHost(
            new TestDurableContinuationStore(),
            TimeSpan.FromMinutes(1));
        var outcome = await new KernelActionDispatcher(graph, host).RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (_, _) => ValueTask.FromResult<object>("unused"),
            graph.ActionSnapshot,
            CancellationToken.None);
        Assert.NotNull(outcome.Continuation);
        var state = await host.GetAsync(outcome.Continuation!.TokenId, CancellationToken.None);
        Assert.Null(await host.ClaimAsync(
            outcome.Continuation.TokenId,
            "BADSECRET",
            new KernelContinuationClaim(
                new ContinuationClaim("owner-a", DateTimeOffset.UtcNow.AddMinutes(1), 1, state!.Revision),
                graph.ActionSnapshot.ContractHash),
            DateTimeOffset.UtcNow,
            CancellationToken.None));
        var claimed = await host.ClaimAsync(
            outcome.Continuation.TokenId,
            outcome.Continuation.Secret,
            new KernelContinuationClaim(
                new ContinuationClaim("owner-a", DateTimeOffset.UtcNow.AddMinutes(1), 1, state.Revision),
                graph.ActionSnapshot.ContractHash),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal("owner-a", claimed?.ClaimOwner);
        Assert.NotNull(state);
    }

    [Fact]
    public void Canonical_kernel_catalog_is_registered_without_umbrella_keys()
    {
        var graph = new KernelGraphBuilder().Compile();

        Assert.All(SharpClawActionCatalog.Kernel, key => Assert.True(graph.ContainsAction(key), key.Value));
        Assert.All(KernelActionCatalog.Coverage, entry =>
            Assert.Contains(entry.ActionKey, SharpClawActionCatalog.Kernel));
        Assert.DoesNotContain(KernelActionCatalog.Coverage, entry =>
            entry.ActionKey.Value is "runtime.lifecycle" or "request.ingress" or "gateway.boundary");
    }

    [Fact]
    public void Sensitive_approval_and_effective_grants_are_compiled_authoritatively()
    {
        var key = new SharpClawActionKey("boundary.sensitive");
        var builder = new KernelGraphBuilder(false);
        var descriptor = Descriptor(key, KernelActionCapabilities, sensitive: true);
        builder.Add(descriptor);
        Assert.Throws<KernelGraphCompilationException>(() => builder.Compile());

        var graph = builder.Compile(options: new KernelGraphCompileOptions
        {
            SensitiveActionApprovals =
            [
                new KernelSensitiveActionApproval(
                    "core",
                    key,
                    descriptor.Version,
                    typeof(KernelActionEnvelope).AssemblyQualifiedName!,
                    typeof(object).AssemblyQualifiedName!,
                    KernelSchemaIdentity.Action(descriptor))
            ],
            ActionCapabilityGrants = new Dictionary<string, ActionInterceptionCapabilities>
            {
                [key.Value] = ActionInterceptionCapabilities.Inspect
            }
        });
        var grant = Assert.Single(graph.ActionSnapshot.ActionGrants, value => value.ActionKey == key);
        Assert.Equal(ActionInterceptionCapabilities.Inspect, grant.Capabilities);
        Assert.True(grant.SensitiveApproved);
    }

    [Fact]
    public void Contract_hash_includes_hook_identity_and_effective_grants()
    {
        var key = new SharpClawActionKey("boundary.hash");
        var first = new KernelGraphBuilder(false);
        first.Add(Descriptor(key));
        first.Hooks.For(key).Use<ProceedInterceptor>(Order("first"));
        var second = new KernelGraphBuilder(false);
        second.Add(Descriptor(key));
        second.Hooks.For(key).Use<ProceedInterceptor>(Order("second"));

        var firstGraph = first.Compile();
        var secondGraph = second.Compile(options: new KernelGraphCompileOptions
        {
            ActionCapabilityGrants = new Dictionary<string, ActionInterceptionCapabilities>
            {
                [key.Value] = ActionInterceptionCapabilities.Inspect
            }
        });

        Assert.NotEqual(firstGraph.ActionSnapshot.ContractHash, secondGraph.ActionSnapshot.ContractHash);

        var category = new KernelGraphBuilder(false);
        category.Add(Descriptor(key));
        category.Hooks.Category("boundary").Use<ProceedInterceptor>(Order("first"));
        Assert.NotEqual(firstGraph.ActionSnapshot.ContractHash, category.Compile().ActionSnapshot.ContractHash);

        var priority = new KernelGraphBuilder(false);
        priority.Add(Descriptor(key));
        priority.Hooks.For(key).Use<ProceedInterceptor>(new HookOrdering(
            "first",
            HookPriority.High,
            [],
            [],
            null,
            HookFailurePolicy.FailAction));
        Assert.NotEqual(firstGraph.ActionSnapshot.ContractHash, priority.Compile().ActionSnapshot.ContractHash);

        var constrained = new KernelGraphBuilder(false);
        constrained.Add(Descriptor(key));
        constrained.Hooks.For(key).Use<ProceedInterceptor>(new HookOrdering(
            "first",
            HookPriority.Normal,
            ["later"],
            [],
            null,
            HookFailurePolicy.FailAction));
        Assert.NotEqual(firstGraph.ActionSnapshot.ContractHash, constrained.Compile().ActionSnapshot.ContractHash);
    }

    [Fact]
    public void Contract_hash_binds_descriptor_types_schemas_and_event_delivery()
    {
        var key = new SharpClawActionKey("boundary.hash.fields");
        var descriptor = Descriptor(key);
        var baseline = ActionHash(descriptor);

        Assert.NotEqual(baseline, ActionHash(descriptor with { Version = 2 }));
        Assert.NotEqual(baseline, ActionHash(descriptor with { Category = "other" }));
        Assert.NotEqual(baseline, ActionHash(descriptor with
        {
            HasIrreversibleEffects = true
        }));
        Assert.NotEqual(baseline, ActionHash(descriptor with
        {
            RepeatPolicy = new ActionRepeatPolicy(ActionRepeatKind.Idempotent, 2, TimeSpan.FromSeconds(1), "other")
        }));
        Assert.NotEqual(baseline, ActionHash(descriptor with
        {
            ProtocolVersionRange = new ContractVersionRange(1, 2)
        }));
        Assert.NotEqual(baseline, ActionHash(descriptor with
        {
            SafePoints = [ActionSafePoint.BeforeCommit]
        }));

        var sensitive = descriptor with { ContainsSensitiveData = true };
        Assert.NotEqual(
            baseline,
            ActionHash(sensitive, SensitiveApprovalOptions(sensitive, key)));

        var stringInput = new ActionDescriptor<string, object>(
            key,
            1,
            "boundary",
            ActionInterceptionCapabilities.Inspect,
            false,
            false,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "scope"),
            null,
            TimeSpan.FromSeconds(2));
        var integerInput = new ActionDescriptor<int, object>(
            key,
            1,
            "boundary",
            ActionInterceptionCapabilities.Inspect,
            false,
            false,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "scope"),
            null,
            TimeSpan.FromSeconds(2));
        Assert.NotEqual(ActionHash(stringInput), ActionHash(integerInput));

        var stringResult = new ActionDescriptor<string, string>(
            key,
            1,
            "boundary",
            ActionInterceptionCapabilities.Inspect,
            false,
            false,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "scope"),
            null,
            TimeSpan.FromSeconds(2));
        Assert.NotEqual(ActionHash(stringInput), ActionHash(stringResult));

        var toolBaseline = ToolHash(new ToolDescriptor(
            "sample",
            "sample",
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()));
        var toolChanged = ToolHash(new ToolDescriptor(
            "sample",
            "sample",
            JsonDocument.Parse("{\"type\":\"object\",\"required\":[\"value\"]}").RootElement.Clone()));
        Assert.NotEqual(toolBaseline, toolChanged);

        var eventKey = new SharpClawEventKey("boundary.hash.event");
        var eventBaseline = EventHash(new EventDescriptor<BoundaryEvent>(
            eventKey,
            1,
            "boundary",
            EventInterceptionCapabilities.Inspect,
            false,
            false));
        var eventChanged = EventHash(new EventDescriptor<OtherBoundaryEvent>(
            eventKey,
            1,
            "boundary",
            EventInterceptionCapabilities.Inspect,
            false,
            false));
        Assert.NotEqual(eventBaseline, eventChanged);

        var deliveryChanged = EventHash(new EventDescriptor<BoundaryEvent>(
            eventKey,
            1,
            "boundary",
            EventInterceptionCapabilities.Inspect,
            false,
            false)
        {
            DeliveryClasses = [EventDelivery.Inline, EventDelivery.Queued]
        });
        Assert.NotEqual(eventBaseline, deliveryChanged);
    }

    [Fact]
    public void Module_manifest_grants_are_required_for_requested_hook_effects()
    {
        var key = new SharpClawActionKey("boundary.module.grant");
        var registry = new KernelModuleRegistry();
        registry.Add(new CapabilityModule(key));

        var exception = Assert.Throws<KernelGraphCompilationException>(() => registry.Compile(
            options: new KernelGraphCompileOptions
            {
                ActionModuleCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
                {
                    ["boundary.module"] = new Dictionary<string, ActionInterceptionCapabilities>
                    {
                        [key.Value] = ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap
                    }
                }
            }));

        Assert.Contains("boundary.module", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unauthorized", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(ActionInterceptionCapabilities.ReplaceInput), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Module_hooks_without_manifest_grants_fail_closed()
    {
        var key = new SharpClawActionKey("boundary.module.missing-grant");
        var registry = new KernelModuleRegistry();
        registry.Add(new CapabilityModule(key));

        var exception = Assert.Throws<KernelGraphCompilationException>(() => registry.Compile());

        Assert.Contains("boundary.module", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no manifest grant", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restricted_module_hook_uses_its_own_effective_capabilities()
    {
        var key = SharpClawActions.Chat.Turn;
        var registry = new KernelModuleRegistry();
        registry.Add(new RestrictedHookModule(key));
        var graph = registry.Compile(
            options: new KernelGraphCompileOptions
            {
                ActionModuleCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
                {
                    ["restricted.module"] = new Dictionary<string, ActionInterceptionCapabilities>
                    {
                        [key.Value] = ActionInterceptionCapabilities.Inspect
                    }
                }
            });
        var terminalCalls = 0;

        var outcome = await new KernelActionDispatcher(graph).RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult<object>("terminal");
            },
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("ACTION_CAPABILITY_DENIED", outcome.Error?.Code);
        Assert.Equal(0, terminalCalls);
    }

    [Fact]
    public async Task Post_proceed_timeout_keeps_the_committed_terminal_result_once()
    {
        var key = new SharpClawActionKey("boundary.timeout.post-proceed");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        builder.Hooks.For(key).Use<NonCooperativePostProceedInterceptor>(new HookOrdering(
            "post-proceed",
            Timeout: TimeSpan.FromMilliseconds(5),
            FailurePolicy: HookFailurePolicy.BestEffort));
        var graph = builder.Compile();
        var terminalCalls = 0;

        var outcome = await new KernelActionDispatcher(graph).RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult<object>("committed");
            },
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("committed", outcome.Result);
        Assert.Equal(1, terminalCalls);
        await NonCooperativePostProceedInterceptor.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Non_cooperative_hook_timeout_consumes_the_control_without_repeating_the_terminal()
    {
        var key = new SharpClawActionKey("boundary.timeout.pre-proceed");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key) with { ContinuationPolicy = null });
        builder.Hooks.For(key).Use<NonCooperativePreProceedInterceptor>(new HookOrdering(
            "pre-proceed",
            Timeout: TimeSpan.FromMilliseconds(5),
            FailurePolicy: HookFailurePolicy.BestEffort));
        var graph = builder.Compile();
        var terminalCalls = 0;
        NonCooperativePreProceedInterceptor.Completed =
            new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var outcome = await new KernelActionDispatcher(graph).RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult<object>("terminal");
            },
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Uncertain, outcome.Kind);
        Assert.Equal(0, terminalCalls);
        var lateControlError = await NonCooperativePreProceedInterceptor.Completed.Task
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("KernelControlException", lateControlError!.GetType().Name);
    }

    [Fact]
    public async Task Event_post_continue_timeout_keeps_one_event_path()
    {
        var key = new SharpClawEventKey("boundary.event.timeout");
        var builder = new KernelGraphBuilder(false);
        builder.AddEvent(new EventDescriptor<BoundaryEvent>(
            key,
            1,
            "boundary",
            EventInterceptionCapabilities.Inspect,
            false,
            false));
        builder.Events.For(key).Intercept<NonCooperativeEventInterceptor>(new HookOrdering(
            "event-post-continue",
            Timeout: TimeSpan.FromMilliseconds(5),
            FailurePolicy: HookFailurePolicy.BestEffort));
        var graph = builder.Compile();
        var result = await new KernelEventDispatcher(graph).DispatchAsync(
            new EventDescriptor<BoundaryEvent>(
                key,
                1,
                "boundary",
                EventInterceptionCapabilities.Inspect,
                false,
                false),
            new BoundaryEvent("value"),
            graph.ActionSnapshot,
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            CancellationToken.None);

        Assert.Equal(EventInterceptionKind.Continued, result.Kind);
        Assert.Equal(1, NonCooperativeEventInterceptor.Continuations);
    }

    [Fact]
    public async Task Non_cooperative_event_timeout_consumes_the_event_control()
    {
        var key = new SharpClawEventKey("boundary.event.pre-timeout");
        var builder = new KernelGraphBuilder(false);
        builder.AddEvent(new EventDescriptor<BoundaryEvent>(
            key,
            1,
            "boundary",
            EventInterceptionCapabilities.Inspect,
            false,
            false));
        builder.Events.For(key).Intercept<NonCooperativeEventPreInterceptor>(new HookOrdering(
            "event-pre-timeout",
            Timeout: TimeSpan.FromMilliseconds(5),
            FailurePolicy: HookFailurePolicy.BestEffort));
        var graph = builder.Compile();
        NonCooperativeEventPreInterceptor.Completed =
            new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await new KernelEventDispatcher(graph).DispatchAsync(
            new EventDescriptor<BoundaryEvent>(
                key,
                1,
                "boundary",
                EventInterceptionCapabilities.Inspect,
                false,
                false),
            new BoundaryEvent("value"),
            graph.ActionSnapshot,
            cancellationToken: CancellationToken.None);

        Assert.Equal(EventInterceptionKind.Failed, result.Kind);
        Assert.Equal("EVENT_OUTCOME_UNCERTAIN", result.Error?.Code);
        var lateControlError = await NonCooperativeEventPreInterceptor.Completed.Task
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("KernelControlException", lateControlError!.GetType().Name);
    }

    [Fact]
    public async Task Nested_dispatch_increments_depth_and_preserves_parent_identity()
    {
        var key = new SharpClawActionKey("boundary.recursion");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        builder.Hooks.For(key).Use<NestedDispatchInterceptor>(Order("nested"));
        var graph = builder.Compile(options: new KernelGraphCompileOptions { MaximumActionDepth = 2 });
        NestedDispatchInterceptor.Graph = graph;
        NestedDispatchInterceptor.Dispatcher = new KernelActionDispatcher(graph);
        NestedDispatchInterceptor.Observations.Clear();

        var outcome = await NestedDispatchInterceptor.Dispatcher.RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (_, _) => ValueTask.FromResult<object>("terminal"),
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("ACTION_DEPTH_EXCEEDED", outcome.Error?.Code);
        Assert.Equal([0, 1, 2], NestedDispatchInterceptor.Observations.Select(value => value.Depth));
        Assert.Equal(3, NestedDispatchInterceptor.Observations.Select(value => value.InvocationId).Distinct().Count());
        Assert.Equal(
            NestedDispatchInterceptor.Observations[0].InvocationId,
            NestedDispatchInterceptor.Observations[1].ParentInvocationId);
        Assert.Equal(
            NestedDispatchInterceptor.Observations[1].InvocationId,
            NestedDispatchInterceptor.Observations[2].ParentInvocationId);
    }

    [Fact]
    public async Task Durable_continuation_enforces_hash_claim_lease_generation_and_delivery_state()
    {
        var key = new SharpClawActionKey("boundary.continuation.state");
        var host = new StoreBackedContinuationHost(new TestDurableContinuationStore());
        var request = new KernelContinuationRequest(
            Guid.NewGuid(),
            key,
            1,
            Guid.NewGuid(),
            new ActionDeferRequest(DateTimeOffset.UtcNow.AddMinutes(1), "approval"),
            new ActionContinuationPolicy(TimeSpan.FromMinutes(5), true, true),
            "contract-hash",
            new ContinuationDestination("test", "destination"),
            "protected-input");
        var token = await host.CreateAsync(request, CancellationToken.None);
        var pending = await host.GetAsync(token.TokenId, CancellationToken.None);
        Assert.NotNull(pending);
        Assert.DoesNotContain(token.Secret, pending!.TokenHash, StringComparison.Ordinal);
        Assert.Equal("protected-input", pending.ProtectedInput);

        var now = DateTimeOffset.UtcNow;
        var claim = new KernelContinuationClaim(
            new ContinuationClaim("owner", now.AddMinutes(1), 1, pending.Revision),
            "contract-hash");
        Assert.Null(await host.ClaimAsync(token.TokenId, "wrong", claim, now, CancellationToken.None));
        Assert.Null(await host.ClaimAsync(
            token.TokenId,
            token.Secret,
            claim with { ContractHash = "other-contract" },
            now,
            CancellationToken.None));
        var claimed = await host.ClaimAsync(token.TokenId, token.Secret, claim, now, CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(ContinuationState.Claimed, claimed!.State);
        Assert.Null(await host.ClaimAsync(
            token.TokenId,
            token.Secret,
            new KernelContinuationClaim(
                new ContinuationClaim("second-owner", now.AddMinutes(1), 2, claimed.Revision),
                "contract-hash"),
            now,
            CancellationToken.None));

        var renewedClaim = claim with
        {
            Claim = claim.Claim with { ExpectedRevision = claimed.Revision }
        };
        var renewed = await host.RenewLeaseAsync(
            token.TokenId,
            token.Secret,
            renewedClaim,
            now,
            CancellationToken.None);
        Assert.NotNull(renewed);
        var currentClaim = renewedClaim with
        {
            Claim = renewedClaim.Claim with { ExpectedRevision = renewed!.Revision }
        };
        Assert.NotNull(await host.ResumeAsync(
            token.TokenId,
            token.Secret,
            currentClaim,
            now,
            CancellationToken.None));
        var completed = await host.CompleteAsync(
            token.TokenId,
            token.Secret,
            currentClaim,
            "",
            now,
            CancellationToken.None);
        Assert.Null(completed);
        completed = await host.CompleteAsync(
            token.TokenId,
            token.Secret,
            currentClaim,
            "completed",
            now,
            CancellationToken.None);
        Assert.Equal(ContinuationState.Completed, completed!.State);
        Assert.Equal("completed", completed.CompletedOutcome);

        var deliveredClaim = currentClaim with
        {
            Claim = currentClaim.Claim with { ExpectedRevision = completed.Revision }
        };
        var delivered = await host.DeliverAsync(
            token.TokenId,
            token.Secret,
            deliveredClaim,
            now,
            CancellationToken.None);
        Assert.Equal(ContinuationState.Delivered, delivered!.State);
        var acknowledged = await host.AcknowledgeAsync(
            token.TokenId,
            token.Secret,
            deliveredClaim with
            {
                Claim = deliveredClaim.Claim with { ExpectedRevision = delivered.Revision }
            },
            now,
            CancellationToken.None);
        Assert.Equal(ContinuationState.Delivered, acknowledged!.State);
        Assert.True(await host.DeleteAsync(
            token.TokenId,
            token.Secret,
            deliveredClaim with
            {
                Claim = deliveredClaim.Claim with { ExpectedRevision = acknowledged.Revision }
            },
            now,
            CancellationToken.None));
        Assert.Equal(ContinuationState.Deleted, (await host.GetAsync(token.TokenId, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Durable_recovery_enforces_token_claim_fencing_and_delivery_state()
    {
        var key = new SharpClawActionKey("boundary.recovery.state");
        var host = new StoreBackedContinuationHost(new TestDurableContinuationStore());
        var record = await host.RecordUncertaintyAsync(
            new KernelUncertaintyRequest(
                Guid.NewGuid(),
                key,
                1,
                Guid.NewGuid(),
                ActionExecutionStage.TerminalReturned,
                "UNKNOWN_RECEIPT",
                "The external receipt is unavailable.",
                "receipt-1",
                "contract-hash",
                new ContinuationDestination("test", "recovery"),
                "protected-input"),
            CancellationToken.None);

        var pending = await host.GetRecoveryAsync(record.Token.RecoveryId, CancellationToken.None);
        Assert.NotNull(pending);
        Assert.DoesNotContain(record.Token.Secret, pending!.TokenHash, StringComparison.Ordinal);
        Assert.Equal("protected-input", pending.ProtectedInput);
        var now = DateTimeOffset.UtcNow;
        var claim = new KernelContinuationClaim(
            new ContinuationClaim("recovery-owner", now.AddMinutes(1), 1, pending.Revision),
            "contract-hash");

        Assert.Null(await host.ClaimRecoveryAsync(
            record.Token.RecoveryId,
            "wrong-secret",
            claim,
            now,
            CancellationToken.None));
        Assert.Null(await host.ClaimRecoveryAsync(
            record.Token.RecoveryId,
            record.Token.Secret,
            claim with { ContractHash = "other-contract" },
            now,
            CancellationToken.None));
        var claimed = await host.ClaimRecoveryAsync(
            record.Token.RecoveryId,
            record.Token.Secret,
            claim,
            now,
            CancellationToken.None);
        Assert.Equal(ContinuationState.Claimed, claimed!.State);
        Assert.Null(await host.ClaimRecoveryAsync(
            record.Token.RecoveryId,
            record.Token.Secret,
            new KernelContinuationClaim(
                new ContinuationClaim("second-recovery-owner", now.AddMinutes(1), 2, claimed.Revision),
                "contract-hash"),
            now,
            CancellationToken.None));

        var renewed = await host.RenewRecoveryLeaseAsync(
            record.Token.RecoveryId,
            record.Token.Secret,
            claim with
            {
                Claim = claim.Claim with { ExpectedRevision = claimed.Revision }
            },
            now,
            CancellationToken.None);
        Assert.NotNull(renewed);
        var currentClaim = claim with
        {
            Claim = claim.Claim with { ExpectedRevision = renewed!.Revision }
        };
        Assert.NotNull(await host.ResumeRecoveryAsync(
            record.Token.RecoveryId,
            record.Token.Secret,
            currentClaim,
            now,
            CancellationToken.None));

        var completed = await host.CompleteRecoveryAsync(
            record.Token.RecoveryId,
            record.Token.Secret,
            currentClaim,
            "resolved-outcome",
            now,
            CancellationToken.None);
        Assert.Equal(ContinuationState.Completed, completed!.State);
        Assert.Equal("resolved-outcome", completed.CompletedOutcome);

        var delivered = await host.DeliverRecoveryAsync(
            record.Token.RecoveryId,
            record.Token.Secret,
            currentClaim with
            {
                Claim = currentClaim.Claim with { ExpectedRevision = completed.Revision }
            },
            now,
            CancellationToken.None);
        Assert.Equal(ContinuationState.Delivered, delivered!.State);
        var acknowledged = await host.AcknowledgeRecoveryAsync(
            record.Token.RecoveryId,
            record.Token.Secret,
            currentClaim with
            {
                Claim = currentClaim.Claim with { ExpectedRevision = delivered.Revision }
            },
            now,
            CancellationToken.None);
        Assert.Equal(ContinuationState.Delivered, acknowledged!.State);
        Assert.True(await host.DeleteRecoveryAsync(
            record.Token.RecoveryId,
            record.Token.Secret,
            currentClaim with
            {
                Claim = currentClaim.Claim with { ExpectedRevision = acknowledged.Revision }
            },
            now,
            CancellationToken.None));
        Assert.Equal(
            ContinuationState.Deleted,
            (await host.GetRecoveryAsync(record.Token.RecoveryId, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Expired_continuation_requires_expiry_transition_before_deletion()
    {
        var host = new StoreBackedContinuationHost(new TestDurableContinuationStore());
        var now = DateTimeOffset.UtcNow;
        var request = new KernelContinuationRequest(
            Guid.NewGuid(),
            new SharpClawActionKey("boundary.expiry"),
            1,
            Guid.NewGuid(),
            new ActionDeferRequest(now.AddSeconds(1), "approval"),
            new ActionContinuationPolicy(TimeSpan.FromMinutes(5), true, true),
            "contract-hash",
            new ContinuationDestination("test", "expiry"),
            "protected-input");
        var token = await host.CreateAsync(request, CancellationToken.None);
        var pending = await host.GetAsync(token.TokenId, CancellationToken.None);
        var claim = new KernelContinuationClaim(
            new ContinuationClaim("owner", now.AddMinutes(1), 1, pending!.Revision),
            request.ContractHash);
        var claimed = await host.ClaimAsync(token.TokenId, token.Secret, claim, now, CancellationToken.None);
        var expired = await host.ExpireAsync(token.TokenId, claimed!.ExpiresAt.AddSeconds(1), CancellationToken.None);

        Assert.Equal(ContinuationState.Expired, expired!.State);
        Assert.Null(await host.ResumeAsync(
            token.TokenId,
            token.Secret,
            claim with { Claim = claim.Claim with { ExpectedRevision = expired.Revision } },
            expired.ExpiresAt.AddSeconds(1),
            CancellationToken.None));
        Assert.True(await host.DeleteAsync(
            token.TokenId,
            token.Secret,
            claim with { Claim = claim.Claim with { ExpectedRevision = expired.Revision } },
            expired.ExpiresAt.AddSeconds(1),
            CancellationToken.None));
        Assert.Equal(ContinuationState.Deleted, (await host.GetAsync(token.TokenId, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Context_assembly_and_provider_round_keep_system_history_user_order()
    {
        var conversationId = Guid.NewGuid();
        var request = new ChatContextRequest(
            conversationId,
            new ChatProfile("provider", Guid.NewGuid(), SystemPrompt: "system"),
            [new ChatCompletionMessage("user", "history")]);
        var context = await new KernelChatContextAssembler([]).BuildAsync(request, CancellationToken.None);
        Assert.Equal("system", Assert.Single(context.SystemPromptSegments).Content);
        Assert.Equal("history", Assert.Single(context.Messages).Content);

        var graph = new KernelGraphBuilder().Compile();
        var transport = new RecordingTransport();
        var requestForProvider = NewProviderRequest(graph, context);
        await new ProviderRoundLoop(transport, graph).RunAsync(
            requestForProvider,
            new NoToolPipeline(),
            CancellationToken.None);
        Assert.Equal(["system", "history", "hello"], transport.Messages.Select(message => message.Content));
        Assert.Equal("system", transport.Messages[0].Content);
        Assert.Equal("history", transport.Messages[1].Content);
        Assert.Equal("hello", transport.Messages[2].Content);
    }

    [Fact]
    public async Task Direct_turn_consumes_replaced_inputs_at_each_effect_boundary()
    {
        DirectReplacementInterceptor.HistoryConversationId = Guid.NewGuid();
        var builder = new KernelGraphBuilder();
        foreach (var key in new[]
                 {
                     SharpClawActions.Chat.Turn,
                     SharpClawActions.Chat.ResolveConversation,
                     SharpClawActions.Chat.ResolveProfile,
                     SharpClawActions.Chat.LoadHistory,
                     SharpClawActions.Chat.AssembleContext,
                     SharpClawActions.Chat.ProviderRound,
                     SharpClawActions.Chat.CommitExchange
                 })
            builder.Hooks.For(key).Use<DirectReplacementInterceptor>(Order(key.Value));
        var graph = builder.Compile();
        var conversation = new DirectConversationResolver();
        var profile = new DirectProfileResolver();
        var store = new DirectConversationStore();
        var assembler = new DirectContextAssembler();
        var provider = new DirectProviderLoop();
        var runner = new DirectTurnRunner(
            graph,
            new KernelActionDispatcher(graph),
            conversation,
            profile,
            store,
            assembler,
            provider,
            new NoToolPipeline());

        var result = await runner.RunAsync(new ChatTurnInput("original"), CancellationToken.None);

        Assert.Equal("conversation", conversation.LastInput!.Message);
        Assert.Equal("profile", profile.LastTurn!.Input.Message);
        Assert.Equal(DirectReplacementInterceptor.HistoryConversationId, store.LastHistoryConversationId);
        Assert.Equal("context", assembler.LastRequest!.Profile.SystemPrompt);
        Assert.Equal("provider", provider.LastRequest!.Profile.SystemPrompt);
        Assert.Equal("commit", store.LastExchange!.UserMessage);
        Assert.Equal(DirectReplacementInterceptor.HistoryConversationId, result.ConversationId);
        Assert.NotEqual(result.TurnId, result.ConversationId);
    }

    [Fact]
    public async Task Unified_tool_pipeline_consumes_replaced_gate_coordinator_and_handler_inputs()
    {
        ToolReplacementInterceptor.Reset();
        var builder = new KernelGraphBuilder();
        builder.AddTool<RecordingToolHandler>(new ToolDescriptor(
            "registered",
            "registered",
            ToolSchemas.EmptyObject));
        foreach (var key in new[]
                 {
                     SharpClawActions.Tools.Invoke,
                     SharpClawActions.Tools.Resolve,
                     SharpClawActions.Tools.Check,
                     SharpClawActions.Tools.Coordinate,
                     SharpClawActions.Tools.InvokeHandler
                 })
            builder.Hooks.For(key).Use<ToolReplacementInterceptor>(Order(key.Value));
        var graph = builder.Compile();
        var gate = new RecordingGate();
        var coordinator = new RecordingCoordinator();
        var pipeline = new UnifiedToolPipeline(
            graph,
            new KernelActionDispatcher(graph),
            [gate],
            coordinator);
        using var arguments = JsonDocument.Parse("{\"stage\":\"original\"}");

        var outcome = await pipeline.InvokeAsync(
            new ToolInvocation(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "call",
                "initial",
                arguments.RootElement.Clone(),
                RequestPrincipal.Anonymous,
                ExtensionFeatureSet.Empty),
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("checked", gate.LastInvocation!.Arguments.GetProperty("stage").GetString());
        Assert.Equal("coordinate", coordinator.LastPlan!.Invocation.Arguments.GetProperty("stage").GetString());
        Assert.Equal("handler", RecordingToolHandler.LastInvocation!.Arguments.GetProperty("stage").GetString());
        Assert.Equal("handler", outcome.Result!.Content);
    }

    [Fact]
    public async Task Provider_buffered_and_streamed_transports_use_the_canonical_action_sequence()
    {
        var builder = new KernelGraphBuilder();
        ProviderRecordingInterceptor.Keys.Clear();
        foreach (var key in new[]
                 {
                      SharpClawActions.Provider.Resolve,
                      new SharpClawActionKey("provider.client.create"),
                      new SharpClawActionKey("provider.request.prepare"),
                     new SharpClawActionKey("provider.request.serialize"),
                     new SharpClawActionKey("provider.request.serialize.after"),
                     SharpClawActions.Provider.Send,
                     new SharpClawActionKey("provider.stream.open"),
                     new SharpClawActionKey("provider.stream.chunk.receive"),
                     new SharpClawActionKey("provider.stream.chunk.transform"),
                     new SharpClawActionKey("provider.stream.chunk.send"),
                     new SharpClawActionKey("provider.stream.close"),
                     new SharpClawActionKey("provider.response.deserialize"),
                     SharpClawActions.Provider.AfterTransport,
                     new SharpClawActionKey("provider.request.fail"),
                     new SharpClawActionKey("provider.request.cancel")
                 })
            builder.Hooks.For(key).Use<ProviderRecordingInterceptor>(Order(key.Value));
        var graph = builder.Compile();
        var request = NewProviderRequest(graph, ChatContextContribution.Empty);
        var transport = new RecordingTransport();
        var loop = new ProviderRoundLoop(transport, graph);

        var completion = await loop.RunAsync(request, new NoToolPipeline(), CancellationToken.None);

        Assert.Equal("done", completion.Content);
        Assert.Contains(transport.Messages, message => message.Content == "effective-provider-message");
        Assert.Equal(
            [
                "provider.resolve",
                "provider.client.create",
                "provider.request.prepare",
                "provider.request.serialize",
                "provider.request.serialize.after",
                "provider.request.send",
                "provider.response.deserialize",
                "provider.response.complete"
            ],
            ProviderRecordingInterceptor.Keys);

        ProviderRecordingInterceptor.Keys.Clear();
        var chunks = new List<ChatStreamChunk>();
        await foreach (var chunk in new ProviderRoundLoop(new OneRoundStreamTransport(), graph).StreamAsync(
                           request,
                           new NoToolPipeline(),
                           CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal("final", chunks[^1].Finished?.Content);
        Assert.Equal(
            [
                "provider.resolve",
                "provider.client.create",
                "provider.request.prepare",
                "provider.request.serialize",
                "provider.request.serialize.after",
                "provider.stream.open",
                "provider.request.send",
                "provider.stream.chunk.receive",
                     "provider.stream.chunk.transform",
                     "provider.stream.chunk.send",
                     "provider.stream.chunk.receive",
                     "provider.stream.chunk.transform",
                     "provider.stream.chunk.send",
                     "provider.stream.close",
                     "provider.response.deserialize",
                     "provider.response.complete"
                 ],
            ProviderRecordingInterceptor.Keys);

        ProviderRecordingInterceptor.Keys.Clear();
        await Assert.ThrowsAsync<KernelActionExecutionException>(async () =>
            await new ProviderRoundLoop(new FailingTransport(), graph)
                .RunAsync(request, new NoToolPipeline(), CancellationToken.None));
        Assert.Contains("provider.request.fail", ProviderRecordingInterceptor.Keys);
    }

    [Fact]
    public async Task Stream_chunks_pass_receive_transform_send_and_can_be_replaced()
    {
        StreamInterceptor.Keys.Clear();
        var builder = new KernelGraphBuilder();
        foreach (var key in new[]
                 {
                     new SharpClawActionKey("provider.stream.chunk.receive"),
                     new SharpClawActionKey("provider.stream.chunk.transform"),
                     new SharpClawActionKey("provider.stream.chunk.send")
                 })
            builder.Hooks.For(key).Use<StreamInterceptor>(Order(key.Value));
        var graph = builder.Compile();
        var chunks = new List<ChatStreamChunk>();
        await foreach (var chunk in new ProviderRoundLoop(
                           new OneRoundStreamTransport(),
                           graph,
                           new KernelActionDispatcher(graph)).StreamAsync(
                           NewProviderRequest(graph, ChatContextContribution.Empty),
                           new NoToolPipeline(),
                           CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal("transformed", chunks[0].Delta);
        Assert.Equal("final", chunks[^1].Finished?.Content);
        Assert.Equal(
            [
                "provider.stream.chunk.receive",
                "provider.stream.chunk.transform",
                "provider.stream.chunk.send",
                "provider.stream.chunk.receive",
                "provider.stream.chunk.transform",
                "provider.stream.chunk.send"
            ],
            StreamInterceptor.Keys);
    }

    [Fact]
    public async Task Provider_action_cancellation_dispatches_cancel_without_failure()
    {
        ProviderCancellationInterceptor.Keys.Clear();
        var builder = new KernelGraphBuilder();
        builder.Hooks.For(SharpClawActions.Provider.Send)
            .Use<ProviderCancellationInterceptor>(Order("provider-send-cancel"));
        builder.Hooks.For(new SharpClawActionKey("provider.request.cancel"))
            .Use<ProviderCancellationInterceptor>(Order("provider-request-cancel"));
        builder.Hooks.For(new SharpClawActionKey("provider.request.fail"))
            .Use<ProviderCancellationInterceptor>(Order("provider-request-fail"));
        var graph = builder.Compile();

        var exception = await Record.ExceptionAsync(async () =>
            await new ProviderRoundLoop(new RecordingTransport(), graph)
                .RunAsync(NewProviderRequest(graph, ChatContextContribution.Empty), new NoToolPipeline(), CancellationToken.None));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Contains("provider.request.send", ProviderCancellationInterceptor.Keys);
        Assert.Contains("provider.request.cancel", ProviderCancellationInterceptor.Keys);
        Assert.DoesNotContain("provider.request.fail", ProviderCancellationInterceptor.Keys);
    }

    [Fact]
    public async Task Stream_send_can_suppress_a_final_chunk_before_completion_actions()
    {
        StreamInterceptor.Keys.Clear();
        StreamInterceptor.SuppressFinal = true;
        try
        {
            var builder = new KernelGraphBuilder();
            foreach (var key in new[]
                     {
                         new SharpClawActionKey("provider.stream.chunk.send"),
                         new SharpClawActionKey("provider.response.deserialize"),
                         SharpClawActions.Provider.AfterTransport,
                         new SharpClawActionKey("provider.request.fail")
                     })
                builder.Hooks.For(key).Use<StreamInterceptor>(Order(key.Value));
            var graph = builder.Compile();
            var chunks = new List<ChatStreamChunk>();
            await foreach (var chunk in new ProviderRoundLoop(
                               new OneRoundStreamTransport(),
                               graph,
                               new KernelActionDispatcher(graph)).StreamAsync(
                               NewProviderRequest(graph, ChatContextContribution.Empty),
                               new NoToolPipeline(),
                               CancellationToken.None))
                chunks.Add(chunk);

            Assert.Null(chunks[^1].Finished?.Content);
            Assert.Contains("provider.request.fail", StreamInterceptor.Keys);
            Assert.DoesNotContain("provider.response.deserialize", StreamInterceptor.Keys);
            Assert.DoesNotContain("provider.response.complete", StreamInterceptor.Keys);
        }
        finally
        {
            StreamInterceptor.SuppressFinal = false;
        }
    }

    [Fact]
    public async Task Event_identity_is_stable_and_delivery_is_bounded_and_targeted()
    {
        var key = new SharpClawEventKey("boundary.event");
        var builder = new KernelGraphBuilder(false);
        builder.AddEvent(new EventDescriptor<BoundaryEvent>(
            key,
            1,
            "boundary",
            EventInterceptionCapabilities.Inspect,
            false,
            false));
        BoundaryInlineListener.LastEventId = Guid.Empty;
        builder.Events.For(key).Listen<BoundaryInlineListener>(EventDelivery.Inline, Order("inline"));
        builder.Events.For(key).Listen<BoundaryListener>(EventDelivery.Queued, Order("listener"));
        var sink = new InMemoryEventDeliverySink(capacity: 1);
        var graph = builder.Compile();
        var result = await new KernelEventDispatcher(graph, sink).DispatchAsync(
            new EventDescriptor<BoundaryEvent>(
                key,
                1,
                "boundary",
                EventInterceptionCapabilities.Inspect,
                false,
                false),
            new BoundaryEvent("value"),
            graph.ActionSnapshot,
            cancellationToken: CancellationToken.None);
        var queued = Assert.Single(sink.Drain());
        var envelope = Assert.IsType<EventEnvelope<BoundaryEvent>>(queued.Envelope);
        Assert.Equal("listener", queued.TargetListenerId);
        Assert.Equal("value", envelope.Payload.Value);
        Assert.Equal(BoundaryInlineListener.LastEventId, envelope.EventId);
        Assert.Equal(EventInterceptionKind.Continued, result.Kind);
    }

    [Fact]
    public async Task Module_lifecycle_calls_are_dispatched_through_lifecycle_actions()
    {
        LifecycleModule.Starts = 0;
        LifecycleModule.Stops = 0;
        LifecycleInterceptor.Keys.Clear();
        var registry = new KernelModuleRegistry();
        registry.Add(new LifecycleModule());
        var lifecycleStart = new SharpClawActionKey("module.start");
        var lifecycleStop = new SharpClawActionKey("module.stop");
        var graph = registry.Compile(
            options: new KernelGraphCompileOptions
            {
                ActionModuleCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
                {
                    ["boundary.module"] = new Dictionary<string, ActionInterceptionCapabilities>
                    {
                        [lifecycleStart.Value] = KernelActionCatalog.StandardCapabilities(lifecycleStart),
                        [lifecycleStop.Value] = KernelActionCatalog.StandardCapabilities(lifecycleStop)
                    }
                }
            });

        await registry.StartAsync(graph, "host", ExtensionFeatureSet.Empty, CancellationToken.None);
        await registry.StopAsync(CancellationToken.None);

        Assert.Equal(1, LifecycleModule.Starts);
        Assert.Equal(1, LifecycleModule.Stops);
        Assert.Contains("module.start", LifecycleInterceptor.Keys);
        Assert.Contains("module.stop", LifecycleInterceptor.Keys);
    }

    private static string ActionHash<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        KernelGraphCompileOptions? options = null)
    {
        var builder = new KernelGraphBuilder(false);
        builder.Add(descriptor);
        return builder.Compile(options: options).ActionSnapshot.ContractHash;
    }

    private static string EventHash<TEvent>(EventDescriptor<TEvent> descriptor)
    {
        var builder = new KernelGraphBuilder(false);
        builder.AddEvent(descriptor);
        return builder.Compile().ActionSnapshot.ContractHash;
    }

    private static string ToolHash(ToolDescriptor descriptor)
    {
        var builder = new KernelGraphBuilder(false);
        builder.AddTool<NoopToolHandler>(descriptor);
        return builder.Compile().ActionSnapshot.ContractHash;
    }

    private static KernelGraphCompileOptions SensitiveApprovalOptions(
        ActionDescriptor<KernelActionEnvelope, object> descriptor,
        SharpClawActionKey key) =>
        new()
        {
            SensitiveActionApprovals =
            [
                new KernelSensitiveActionApproval(
                    "core",
                    key,
                    descriptor.Version,
                    typeof(KernelActionEnvelope).AssemblyQualifiedName!,
                    typeof(object).AssemblyQualifiedName!,
                    KernelSchemaIdentity.Action(descriptor))
            ]
        };

    private static ActionDescriptor<KernelActionEnvelope, object> Descriptor(
        SharpClawActionKey key,
        ActionInterceptionCapabilities capabilities = KernelActionCapabilities,
        ActionRepeatPolicy? repeatPolicy = null,
        bool sensitive = false) =>
        new(
            key,
            1,
            "boundary",
            capabilities,
            sensitive,
            false,
            repeatPolicy ?? new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "scope"),
            new ActionContinuationPolicy(TimeSpan.FromHours(1), true, true),
            TimeSpan.FromSeconds(2));

    private static HookOrdering Order(string id) =>
        new(id, HookPriority.Normal, [], [], null, HookFailurePolicy.FailAction);

    private static ProviderTurnRequest NewProviderRequest(
        KernelGraph graph,
        ChatContextContribution context) =>
        new(
            new ChatTurnContext(
                Guid.NewGuid(),
                new ChatTurnInput("hello", Caller: RequestPrincipal.Anonymous),
                new ConversationSelection(Guid.NewGuid())),
            new ChatProfile("provider", Guid.NewGuid(), SystemPrompt: null),
            context,
            graph.ChatSnapshot.Tools);

    private const ActionInterceptionCapabilities KernelActionCapabilities =
        ActionInterceptionCapabilities.Inspect |
        ActionInterceptionCapabilities.ReplaceInput |
        ActionInterceptionCapabilities.Cancel |
        ActionInterceptionCapabilities.ReplaceResult |
        ActionInterceptionCapabilities.Defer |
        ActionInterceptionCapabilities.Repeat |
        ActionInterceptionCapabilities.Wrap |
        ActionInterceptionCapabilities.Observe |
        ActionInterceptionCapabilities.PublishEvents;

    private sealed class UnauthorizedInputInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            control.ProceedWithInputAsync(
                new ActionReplacement<KernelActionEnvelope>(context.Action, "replacement"),
                cancellationToken);
    }

    private sealed class ProceedInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) => control.ProceedAsync(cancellationToken);
    }

    private sealed class ForgedInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IActionOutcome<object>>(new ForgedOutcome());
    }

    private sealed class ForgedOutcome : IActionOutcome<object>
    {
        public ActionOutcomeKind Kind => ActionOutcomeKind.Completed;
        public object? Result => "forged";
        public ContinuationToken? Continuation => null;
        public ExecutionError? Error => null;
        public ActionUncertainty? Uncertainty => null;
    }

    private sealed class FailThenProceedInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            _ = control.Fail(new ExecutionError("FAILED", "failed"));
            return control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class ProceedThenFailInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public async ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            _ = await control.ProceedAsync(cancellationToken);
            _ = control.Fail(new ExecutionError("AFTER_PROCEED", "The control was already completed."));
            return KernelActionOutcome<object>.Failed("UNREACHABLE", "The control allowed a second outcome.");
        }
    }

    private sealed class AttemptInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public static List<int> Attempts { get; } = [];

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            Attempts.Add(context.Attempt);
            return context.Attempt < 3
                ? control.RepeatAsync(new ActionRepeatRequest<KernelActionEnvelope>(context.Action, "retry", null), cancellationToken)
                : control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class NonConflictRepeatInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            control.RepeatAsync(new ActionRepeatRequest<KernelActionEnvelope>(context.Action, "retry", null), cancellationToken);
    }

    private sealed class ReceiptRepeatInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            control.RepeatAsync(new ActionRepeatRequest<KernelActionEnvelope>(context.Action, "retry", null), cancellationToken);
    }

    private sealed class SlowInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public async ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            return await control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class ThrowingTimeoutInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            throw new TimeoutException("The hook timed out before continuation.");
    }

    private sealed class DeferBoundaryInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            control.DeferAsync(
                new ActionDeferRequest(DateTimeOffset.UtcNow.AddMinutes(1), "approval"),
                cancellationToken);
    }

    private sealed class CapabilityModule(SharpClawActionKey key) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("boundary.module", "Boundary module", "boundary");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Actions.Add(Descriptor(key));
            module.Hooks.For(key).Use<ProceedInterceptor>(Order("module-hook"));
        }
    }

    private sealed class RestrictedHookModule(SharpClawActionKey key) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("restricted.module", "Restricted module", "restricted");

        public void Configure(ISharpClawModuleBuilder module) =>
            module.Hooks.For(key).Use<InspectOnlyInterceptor>(Order("restricted-hook"));
    }

    private sealed class InspectOnlyInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            control.ProceedAsync(cancellationToken);
    }

    private sealed class NonCooperativePostProceedInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public static TaskCompletionSource<object?> Finished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            var outcome = await control.ProceedAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(30));
            Finished.TrySetResult(null);
            throw new InvalidOperationException("The post-continuation hook failed.");
        }
    }

    private sealed class NonCooperativePreProceedInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public static TaskCompletionSource<Exception?> Completed { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(30), CancellationToken.None);
            try
            {
                return await control.ProceedAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                Completed.TrySetResult(exception);
                throw;
            }
        }
    }

    private sealed class NonCooperativeEventInterceptor : IEventInterceptor<BoundaryEvent>
    {
        public static int Continuations;

        public ValueTask<IEventInterception<BoundaryEvent>> InterceptAsync(
            EventContext<BoundaryEvent> context,
            IEventControl<BoundaryEvent> control,
            CancellationToken cancellationToken)
        {
            var outcome = control.Continue();
            Interlocked.Increment(ref Continuations);
            return new ValueTask<IEventInterception<BoundaryEvent>>(CompleteAfterFailureAsync(outcome));
        }

        private static async Task<IEventInterception<BoundaryEvent>> CompleteAfterFailureAsync(
            IEventInterception<BoundaryEvent> outcome)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(30));
            throw new InvalidOperationException("The post-continuation event hook failed.");
        }
    }

    private sealed class NonCooperativeEventPreInterceptor : IEventInterceptor<BoundaryEvent>
    {
        public static TaskCompletionSource<Exception?> Completed { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IEventInterception<BoundaryEvent>> InterceptAsync(
            EventContext<BoundaryEvent> context,
            IEventControl<BoundaryEvent> control,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(30), CancellationToken.None);
            try
            {
                return control.Continue();
            }
            catch (Exception exception)
            {
                Completed.TrySetResult(exception);
                throw;
            }
        }
    }

    private sealed class NestedDispatchInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public static KernelGraph Graph { get; set; } = null!;
        public static KernelActionDispatcher Dispatcher { get; set; } = null!;
        public static List<(int Depth, Guid InvocationId, Guid? ParentInvocationId)> Observations { get; } = [];

        public async ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            Observations.Add((context.Depth, context.InvocationId, context.ParentInvocationId));
            var nested = await Dispatcher.RunAsync(
                Graph.GetStandardAction(context.ActionKey),
                context.Action,
                (_, _) => ValueTask.FromResult<object>("nested-terminal"),
                Graph.ActionSnapshot,
                cancellationToken);
            return nested.Kind == ActionOutcomeKind.Completed
                ? await control.ProceedAsync(cancellationToken)
                : control.Fail(nested.Error ?? new ExecutionError("NESTED_FAILED", "The nested action failed."));
        }
    }

    private sealed class NoopToolHandler : IToolHandler
    {
        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ToolResult.Text("ok"));
    }

    private sealed class DirectReplacementInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public static Guid HistoryConversationId { get; set; }

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            object? replacement = context.Action.Key.Value switch
            {
                "chat.turn.start" when context.Action.Payload is ChatTurnInput input =>
                    input with { Message = "outer" },
                "chat.conversation.resolve" when context.Action.Payload is ChatTurnInput input =>
                    input with { Message = "conversation" },
                "chat.profile.resolve" when context.Action.Payload is ChatTurnContext turn =>
                    turn with { Input = turn.Input with { Message = "profile" } },
                "chat.history.load" when context.Action.Payload is ChatTurnContext turn =>
                    turn with { Conversation = new ConversationSelection(HistoryConversationId) },
                "chat.context.assemble.start" when context.Action.Payload is ChatContextRequest request =>
                    request with { Profile = request.Profile with { SystemPrompt = "context" } },
                "chat.provider_round.start" when context.Action.Payload is ProviderTurnRequest request =>
                    request with { Profile = request.Profile with { SystemPrompt = "provider" } },
                "chat.assistant_message.commit" when context.Action.Payload is ChatExchange exchange =>
                    exchange with { UserMessage = "commit" },
                _ => null
            };

            return replacement is null
                ? control.ProceedAsync(cancellationToken)
                : control.ProceedWithInputAsync(
                    new ActionReplacement<KernelActionEnvelope>(
                        context.Action with { Payload = replacement },
                        "test replacement"),
                    cancellationToken);
        }
    }

    private sealed class DirectConversationResolver : IConversationResolver
    {
        public ChatTurnInput? LastInput { get; private set; }

        public ValueTask<ConversationSelection> ResolveAsync(ChatTurnInput input, CancellationToken ct)
        {
            LastInput = input;
            return ValueTask.FromResult(new ConversationSelection(Guid.NewGuid()));
        }
    }

    private sealed class DirectProfileResolver : IChatProfileResolver
    {
        public ChatTurnContext? LastTurn { get; private set; }

        public ValueTask<ChatProfile> ResolveAsync(ChatTurnContext turn, CancellationToken ct)
        {
            LastTurn = turn;
            return ValueTask.FromResult(new ChatProfile("provider", Guid.NewGuid()));
        }
    }

    private sealed class DirectConversationStore : IConversationStore
    {
        public Guid LastHistoryConversationId { get; private set; }
        public ChatExchange? LastExchange { get; private set; }

        public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
            Guid conversationId,
            CancellationToken ct)
        {
            LastHistoryConversationId = conversationId;
            return ValueTask.FromResult<IReadOnlyList<ChatCompletionMessage>>([]);
        }

        public ValueTask CommitExchangeAsync(ChatExchange exchange, CancellationToken ct)
        {
            LastExchange = exchange;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DirectContextAssembler : IChatContextAssembler
    {
        public ChatContextRequest? LastRequest { get; private set; }

        public ValueTask<ChatContextContribution> BuildAsync(ChatContextRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return ValueTask.FromResult(ChatContextContribution.Empty);
        }
    }

    private sealed class DirectProviderLoop : IProviderRoundLoop
    {
        public ProviderTurnRequest? LastRequest { get; private set; }

        public ValueTask<ChatCompletionResult> RunAsync(
            ProviderTurnRequest request,
            IUnifiedToolPipeline tools,
            CancellationToken ct)
        {
            LastRequest = request;
            return ValueTask.FromResult(new ChatCompletionResult { Content = "done", ToolCalls = [] });
        }
    }

    private sealed class ToolReplacementInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public static void Reset() => RecordingToolHandler.LastInvocation = null;

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            if (context.Action.Payload is not ToolInvocation invocation)
                return control.ProceedAsync(cancellationToken);

            var replacement = context.Action.Key.Value switch
            {
                "tool.call.propose" => invocation with { ToolName = "resolved" },
                "tool.definition.select" => invocation with { ToolName = "registered" },
                "tool.call.check" => invocation with { Arguments = Arguments("checked") },
                "tool.call.coordinate" => invocation with { Arguments = Arguments("coordinate") },
                "tool.handler.invoke" => invocation with { Arguments = Arguments("handler") },
                _ => null
            };
            return replacement is null
                ? control.ProceedAsync(cancellationToken)
                : control.ProceedWithInputAsync(
                    new ActionReplacement<KernelActionEnvelope>(
                        context.Action with { Payload = replacement },
                        "tool test replacement"),
                    cancellationToken);
        }

        private static JsonElement Arguments(string stage) =>
            JsonDocument.Parse($"{{\"stage\":\"{stage}\"}}").RootElement.Clone();
    }

    private sealed class RecordingGate : IToolInvocationGate
    {
        public ToolInvocation? LastInvocation { get; private set; }

        public ValueTask<ToolGateDecision> EvaluateAsync(ToolInvocation invocation, CancellationToken ct)
        {
            LastInvocation = invocation;
            return ValueTask.FromResult<ToolGateDecision>(new ToolGateDecision.Continue());
        }
    }

    private sealed class RecordingCoordinator : IToolExecutionCoordinator
    {
        public ToolExecutionPlan? LastPlan { get; private set; }

        public async ValueTask<ToolInvocationOutcome> CoordinateAsync(
            ToolExecutionPlan plan,
            ToolExecutionDelegate execute,
            CancellationToken ct)
        {
            LastPlan = plan;
            return ToolInvocationOutcome.Completed(await execute(plan.Invocation, ct));
        }
    }

    private sealed class RecordingToolHandler : IToolHandler
    {
        public static ToolInvocation? LastInvocation { get; set; }

        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
        {
            LastInvocation = invocation;
            return ValueTask.FromResult(ToolResult.Text(invocation.Arguments.GetProperty("stage").GetString()!));
        }
    }

    private sealed class ProviderRecordingInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public static List<string> Keys { get; } = [];

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            Keys.Add(context.Action.Key.Value);
            if (context.Action.Key.Value == "provider.request.prepare" &&
                context.Action.Payload is KernelProviderRequestEnvelope state)
            {
                return control.ProceedWithInputAsync(
                    new ActionReplacement<KernelActionEnvelope>(
                        context.Action with
                        {
                            Payload = state with
                            {
                                Messages = [ToolAwareMessage.User("effective-provider-message")]
                            }
                        },
                        "provider test replacement"),
                    cancellationToken);
            }

            return control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class ProviderCancellationInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public static List<string> Keys { get; } = [];

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            Keys.Add(context.Action.Key.Value);
            return context.Action.Key == SharpClawActions.Provider.Send
                ? ValueTask.FromResult<IActionOutcome<object>>(
                    control.Cancel("PROVIDER_CANCELLED", "The provider action was cancelled."))
                : control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class FailingTransport : IKernelProviderTransport
    {
        public ValueTask<ChatCompletionResult> CompleteAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<ChatCompletionResult>(new InvalidOperationException("transport failed"));

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed record BoundaryEvent(string Value);

    private sealed record OtherBoundaryEvent(string Value);

    private sealed class BoundaryListener : IEventListener<BoundaryEvent>
    {
        public ValueTask OnEventAsync(EventEnvelope<BoundaryEvent> evt, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class BoundaryInlineListener : IEventListener<BoundaryEvent>
    {
        public static Guid LastEventId { get; set; }

        public ValueTask OnEventAsync(EventEnvelope<BoundaryEvent> evt, CancellationToken cancellationToken)
        {
            LastEventId = evt.EventId;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingTransport : IKernelProviderTransport
    {
        public List<ToolAwareMessage> Messages { get; } = [];

        public ValueTask<ChatCompletionResult> CompleteAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            CancellationToken cancellationToken)
        {
            Messages.AddRange(messages);
            return ValueTask.FromResult(new ChatCompletionResult { Content = "done", ToolCalls = [] });
        }

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return ChatStreamChunk.Final(new ChatCompletionResult { Content = "unused", ToolCalls = [] });
        }
    }

    private sealed class OneRoundStreamTransport : IKernelProviderTransport
    {
        public ValueTask<ChatCompletionResult> CompleteAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ChatCompletionResult { Content = "unused", ToolCalls = [] });

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return ChatStreamChunk.Text("raw");
            yield return ChatStreamChunk.Final(new ChatCompletionResult { Content = "final", ToolCalls = [] });
        }
    }

    private sealed class StreamInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public static List<string> Keys { get; } = [];
        public static bool SuppressFinal { get; set; }

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            Keys.Add(context.Action.Key.Value);
            if (SuppressFinal &&
                context.Action.Key.Value == "provider.stream.chunk.send" &&
                context.Action.Payload is ChatStreamChunk { IsFinished: true })
                return ValueTask.FromResult<IActionOutcome<object>>(control.ReplaceResult(null!, "suppress final"));
            if (context.Action.Key.Value == "provider.stream.chunk.transform" &&
                context.Action.Payload is ChatStreamChunk chunk && chunk.Delta is not null)
            {
                return control.ProceedWithInputAsync(
                    new ActionReplacement<KernelActionEnvelope>(
                        context.Action with { Payload = ChatStreamChunk.Text("transformed") },
                        "transform"),
                    cancellationToken);
            }

            return control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class NoToolPipeline : IUnifiedToolPipeline
    {
        public ValueTask<ToolInvocationOutcome> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ToolInvocationOutcome.Rejected("unused", "unused"));
    }

    private sealed class LifecycleModule : ISharpClawModule
    {
        public static int Starts { get; set; }
        public static int Stops { get; set; }
        public ModuleIdentity Identity { get; } = new("boundary.module", "Boundary", "boundary");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Hooks.For(new SharpClawActionKey("module.start")).Use<LifecycleInterceptor>(Order("start-hook"));
            module.Hooks.For(new SharpClawActionKey("module.stop")).Use<LifecycleInterceptor>(Order("stop-hook"));
        }

        public ValueTask StartAsync(ModuleStartContext context, CancellationToken cancellationToken)
        {
            Starts++;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            Stops++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LifecycleInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public static List<string> Keys { get; } = [];

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            Keys.Add(context.Action.Key.Value);
            return control.ProceedAsync(cancellationToken);
        }
    }
}
