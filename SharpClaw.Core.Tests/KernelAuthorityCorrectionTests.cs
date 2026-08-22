using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelAuthorityCorrectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_records_one_uncertainty_while_terminal_continues(bool wildcard)
    {
        var key = new SharpClawActionKey(wildcard ? "cancel.wildcard" : "cancel.typed");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        if (wildcard)
            builder.Hooks.AnyAction().UseAny<CancellationWildcardInterceptor>(Order("wildcard"));
        else
            builder.Hooks.For(key).Use<CancellationTypedInterceptor>(Order("typed"));
        var graph = builder.Compile();
        var store = new TestDurableContinuationStore();
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            new StoreBackedContinuationHost(store));
        var started = NewSignal();
        var release = NewSignal();
        var completed = NewSignal();
        var terminalCalls = 0;
        using var cancellation = new CancellationTokenSource();

        var run = dispatcher.RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            async (_, _) =>
            {
                Interlocked.Increment(ref terminalCalls);
                started.TrySetResult();
                await release.Task;
                completed.TrySetResult();
                return (object)"committed";
            },
            graph.ActionSnapshot,
            cancellation.Token).AsTask();

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ActionOutcomeKind.Uncertain, outcome.Kind);
        Assert.NotNull(outcome.Uncertainty);
        Assert.False(completed.Task.IsCompleted);
        Assert.Equal(1, terminalCalls);
        Assert.Equal(1, store.RecoveryCount);
        var recovery = await store.ReadRecoveryAsync(
            outcome.Uncertainty!.Recovery.RecoveryId,
            CancellationToken.None);
        Assert.Equal(ContinuationState.OutcomeUncertain, recovery!.State);

        release.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, terminalCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_preserves_a_terminal_result_that_becomes_certain(bool wildcard)
    {
        var key = new SharpClawActionKey(wildcard ? "cancel.certain.wildcard" : "cancel.certain.typed");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        if (wildcard)
            builder.Hooks.AnyAction().UseAny<CancellationWildcardInterceptor>(Order("wildcard"));
        else
            builder.Hooks.For(key).Use<CancellationTypedInterceptor>(Order("typed"));
        var graph = builder.Compile();
        var store = new TestDurableContinuationStore();
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            new StoreBackedContinuationHost(store));
        var started = NewSignal();
        var release = NewSignal();
        using var cancellation = new CancellationTokenSource();

        var run = dispatcher.RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return (object)"committed";
            },
            graph.ActionSnapshot,
            cancellation.Token).AsTask();

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        release.TrySetResult();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("committed", outcome.Result);
        Assert.Equal(0, store.RecoveryCount);
    }

    [Fact]
    public async Task Pre_cancelled_dispatch_does_not_start_a_hook_or_terminal()
    {
        var key = new SharpClawActionKey("cancel.before-start");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        NeverInvokedInterceptor.Calls = 0;
        builder.Hooks.For(key).Use<NeverInvokedInterceptor>(Order("typed"));
        var graph = builder.Compile();
        var terminalCalls = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var outcome = await KernelTestExecution.CreateDispatcher(graph).RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult<object>("committed");
            },
            graph.ActionSnapshot,
            cancellation.Token);

        Assert.Equal(ActionOutcomeKind.Cancelled, outcome.Kind);
        Assert.Equal(0, NeverInvokedInterceptor.Calls);
        Assert.Equal(0, terminalCalls);
    }

    [Fact]
    public void Action_hook_without_inspection_authority_cannot_compile()
    {
        var key = new SharpClawActionKey("grant.action");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        builder.Hooks.For(key).Use<CancellationTypedInterceptor>(Order("zero-grant"));

        var exception = Assert.Throws<KernelGraphCompilationException>(() => builder.Compile(
            options: new KernelGraphCompileOptions
            {
                ActionCapabilityGrants = new Dictionary<string, ActionInterceptionCapabilities>
                {
                    [key.Value] = 0
                }
            }));

        Assert.Contains("Inspect", exception.Message, StringComparison.Ordinal);
        Assert.Contains(key.Value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sensitive_listen_only_subscription_requires_exact_approval()
    {
        var key = new SharpClawEventKey("sensitive.event");
        var descriptor = SensitiveDescriptor(key, true);
        var registry = new KernelModuleRegistry();
        registry.Add(new EventOwnerModule(descriptor));
        registry.Add(new EventListenerModule(key));

        var exception = Assert.Throws<KernelGraphCompilationException>(() => registry.Compile(
            options: new KernelGraphCompileOptions
            {
                EventModuleCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, EventInterceptionCapabilities>>
                {
                    ["event.owner"] = new Dictionary<string, EventInterceptionCapabilities>
                    {
                        [key.Value] = EventInterceptionCapabilities.Inspect |
                                      EventInterceptionCapabilities.Observe
                    },
                    ["listener.module"] = new Dictionary<string, EventInterceptionCapabilities>
                    {
                        [key.Value] = EventInterceptionCapabilities.Inspect |
                                      EventInterceptionCapabilities.Observe
                    }
                },
                SensitiveEventApprovals =
                [
                    new KernelSensitiveEventApproval(
                        "event.owner",
                        key,
                        descriptor.Version,
                        typeof(SensitivePayload).AssemblyQualifiedName!,
                        KernelSchemaIdentity.Event(descriptor, typeof(SensitivePayload)))
                ]
            }));

        Assert.Contains("Sensitive event", exception.Message, StringComparison.Ordinal);
        Assert.Contains(key.Value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Event_listener_without_observation_authority_cannot_compile()
    {
        var key = new SharpClawEventKey("grant.listener");
        var registry = new KernelModuleRegistry();
        registry.Add(new EventOwnerModule(SensitiveDescriptor(key, false)));
        registry.Add(new EventListenerModule(key));

        var exception = Assert.Throws<KernelGraphCompilationException>(() => registry.Compile(
            options: new KernelGraphCompileOptions
            {
                EventModuleCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, EventInterceptionCapabilities>>
                {
                    ["event.owner"] = new Dictionary<string, EventInterceptionCapabilities>
                    {
                        [key.Value] = EventInterceptionCapabilities.Inspect |
                                      EventInterceptionCapabilities.Observe
                    },
                    ["listener.module"] = new Dictionary<string, EventInterceptionCapabilities>
                    {
                        [key.Value] = EventInterceptionCapabilities.Inspect
                    }
                }
            }));

        Assert.Contains(nameof(EventInterceptionCapabilities.Observe), exception.Message, StringComparison.Ordinal);
        Assert.Contains(key.Value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Standard_manifest_binds_every_key_to_explicit_authority_and_contracts()
    {
        var keys = SharpClawActionCatalog.All
            .DistinctBy(key => key.Value, StringComparer.Ordinal)
            .OrderBy(key => key.Value, StringComparer.Ordinal)
            .ToArray();
        var entries = KernelActionCatalog.Descriptors
            .OrderBy(entry => entry.Key.Value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(keys.Select(key => key.Value), entries.Select(entry => entry.Key.Value));
        Assert.All(entries, entry =>
        {
            Assert.Equal(1, entry.Version);
            if (entry.Key.Value.StartsWith("jobs.", StringComparison.Ordinal))
            {
                Assert.Null(entry.InputPayloadType);
                Assert.Null(entry.ResultPayloadType);
            }
            else
            {
                Assert.NotEqual(typeof(KernelActionEnvelope), entry.InputPayloadType);
                Assert.NotEqual(typeof(object), entry.ResultPayloadType);
            }
            Assert.NotEmpty(entry.InputSchema.ContractName);
            Assert.False(string.IsNullOrEmpty(entry.InputSchema.ContentHash));
            Assert.NotEmpty(entry.ResultSchema.ContractName);
            Assert.False(string.IsNullOrEmpty(entry.ResultSchema.ContentHash));
            Assert.True(entry.DefaultTimeout > TimeSpan.Zero);
            Assert.NotEmpty(entry.SafePoints);
            Assert.True(entry.Capabilities.HasFlag(ActionInterceptionCapabilities.Inspect));
            if (entry.Profile == KernelStandardActionProfile.Observe)
                Assert.False(entry.Capabilities.HasFlag(ActionInterceptionCapabilities.Wrap));
            else
                Assert.True(entry.Capabilities.HasFlag(ActionInterceptionCapabilities.Wrap));
        });
        Assert.Equal(entries.Length, entries.Select(entry => entry.InputSchema.ContentHash).Distinct().Count());
        Assert.Equal(entries.Length, entries.Select(entry => entry.ResultSchema.ContentHash).Distinct().Count());

        var providerSend = KernelActionCatalog.DescriptorFor(new("provider.request.send"));
        Assert.Equal(typeof(KernelProviderRequestEnvelope), providerSend.InputPayloadType);
        Assert.Equal(typeof(KernelProviderTransportResult), providerSend.ResultPayloadType);
        Assert.True(providerSend.HasIrreversibleEffects);
        Assert.Equal(ActionRepeatKind.Receipted, providerSend.RepeatPolicy.Kind);
        Assert.Equal(2, providerSend.RepeatPolicy.MaximumAttempts);
        Assert.True(providerSend.RepeatPolicy.MinimumBackoff > TimeSpan.Zero);
        Assert.True(providerSend.ContinuationPolicy!.Durable);

        var storageRead = KernelActionCatalog.DescriptorFor(new("storage.get"));
        Assert.Equal(typeof(JsonElement), storageRead.InputPayloadType);
        Assert.False(storageRead.HasIrreversibleEffects);
        Assert.Equal(ActionRepeatKind.Idempotent, storageRead.RepeatPolicy.Kind);

        var storageCommit = KernelActionCatalog.DescriptorFor(new("storage.upsert.commit"));
        Assert.True(storageCommit.HasIrreversibleEffects);
        Assert.Equal(ActionRepeatKind.ConflictOnly, storageCommit.RepeatPolicy.Kind);
        Assert.Contains(ActionSafePoint.BeforeCommit, storageCommit.SafePoints);
        Assert.Contains(ActionSafePoint.AfterCommit, storageCommit.SafePoints);

        var moduleStart = KernelActionCatalog.DescriptorFor(new("module.start"));
        Assert.True(moduleStart.HasIrreversibleEffects);
        Assert.Equal(ActionRepeatKind.None, moduleStart.RepeatPolicy.Kind);

        var turnComplete = KernelActionCatalog.DescriptorFor(new("chat.turn.complete"));
        Assert.False(turnComplete.HasIrreversibleEffects);
        Assert.True(turnComplete.Capabilities.HasFlag(ActionInterceptionCapabilities.Defer));
        Assert.True(turnComplete.ContinuationPolicy!.Durable);

        var toolCall = KernelActionCatalog.DescriptorFor(SharpClawActions.Tools.Invoke);
        Assert.Equal(typeof(ToolInvocation), toolCall.InputPayloadType);
        Assert.Equal(typeof(ToolInvocationOutcome), toolCall.ResultPayloadType);
        Assert.False(toolCall.HasIrreversibleEffects);
        Assert.True(toolCall.Capabilities.HasFlag(ActionInterceptionCapabilities.Defer));

        Assert.Equal(
            typeof(KernelToolResolution),
            KernelActionCatalog.DescriptorFor(SharpClawActions.Tools.Resolve).ResultPayloadType);
        Assert.Equal(
            typeof(KernelToolCheckResult),
            KernelActionCatalog.DescriptorFor(SharpClawActions.Tools.Check).ResultPayloadType);
        Assert.Equal(
            typeof(ToolResult),
            KernelActionCatalog.DescriptorFor(SharpClawActions.Tools.InvokeHandler).ResultPayloadType);
    }

    [Fact]
    public void Standard_manifest_matches_the_reviewed_complete_contract()
    {
        var records = KernelActionCatalog.Descriptors
            .OrderBy(entry => entry.Key.Value, StringComparer.Ordinal)
            .Select(entry => string.Join(
                "|",
                entry.Key.Value,
                entry.Version.ToString(CultureInfo.InvariantCulture),
                entry.Category,
                entry.InputPayloadType?.AssemblyQualifiedName ?? "module-typed",
                entry.ResultPayloadType?.AssemblyQualifiedName ?? "module-typed",
                entry.InputSchema.ContractName,
                entry.InputSchema.Version.ToString(CultureInfo.InvariantCulture),
                entry.InputSchema.ContentHash,
                entry.ResultSchema.ContractName,
                entry.ResultSchema.Version.ToString(CultureInfo.InvariantCulture),
                entry.ResultSchema.ContentHash,
                ((int)entry.Capabilities).ToString(CultureInfo.InvariantCulture),
                entry.ContainsSensitiveData ? "sensitive" : "ordinary",
                entry.HasIrreversibleEffects ? "irreversible" : "reversible",
                ((int)entry.RepeatPolicy.Kind).ToString(CultureInfo.InvariantCulture),
                entry.RepeatPolicy.MaximumAttempts.ToString(CultureInfo.InvariantCulture),
                entry.RepeatPolicy.MinimumBackoff.Ticks.ToString(CultureInfo.InvariantCulture),
                entry.RepeatPolicy.IdempotencyScope,
                entry.ContinuationPolicy?.MaximumLifetime.Ticks.ToString(CultureInfo.InvariantCulture) ?? "none",
                entry.ContinuationPolicy?.Durable == true ? "durable" : "not-durable",
                entry.ContinuationPolicy?.SingleClaim == true ? "single" : "multiple",
                entry.DefaultTimeout.Ticks.ToString(CultureInfo.InvariantCulture),
                string.Join(',', entry.SafePoints.Select(value => ((int)value).ToString(CultureInfo.InvariantCulture))),
                ((int)entry.Profile).ToString(CultureInfo.InvariantCulture)));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", records))));
        Assert.Equal("1694E44DFFB1C91251E500579D2AC7A28B86B611EF52EC927383B2B564FA6D7E", hash);
    }

    [Fact]
    public void Module_hook_effects_must_fit_descriptor_host_and_administrator_grants()
    {
        var key = SharpClawActions.Chat.Turn;
        var registry = new KernelModuleRegistry();
        registry.Add(new GrantRequestModule(key));

        var exception = Assert.Throws<KernelGraphCompilationException>(() => registry.Compile(
            options: new KernelGraphCompileOptions
            {
                ActionCapabilityGrants = new Dictionary<string, ActionInterceptionCapabilities>
                {
                    [key.Value] = ActionInterceptionCapabilities.Inspect |
                                  ActionInterceptionCapabilities.Wrap
                },
                ActionModuleCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
                {
                    ["grant.request.module"] = new Dictionary<string, ActionInterceptionCapabilities>
                    {
                        [key.Value] = ActionInterceptionCapabilities.Inspect |
                                      ActionInterceptionCapabilities.Cancel
                    }
                }
            }));

        Assert.Contains("grant.request.module", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ActionInterceptionCapabilities.Cancel), exception.Message, StringComparison.Ordinal);
        Assert.Contains("unauthorized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Standard_wildcard_hooks_use_manifest_payload_and_result_contracts()
    {
        StandardWildcardContractInterceptor.Reset();
        var builder = new KernelGraphBuilder();
        builder.Hooks.For(SharpClawActions.Provider.Resolve)
            .UseAny<StandardWildcardContractInterceptor>(Order("replace-input"));
        builder.Hooks.For(SharpClawActions.Provider.AfterTransport)
            .UseAny<StandardWildcardContractInterceptor>(Order("replace-result"));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var request = NewProviderEnvelope("original");
        string? terminalMessage = null;

        var inputOutcome = await dispatcher.RunAsync(
            graph.GetStandardAction(SharpClawActions.Provider.Resolve),
            new KernelActionEnvelope(SharpClawActions.Provider.Resolve, request),
            (context, _) =>
            {
                var effective = Assert.IsType<KernelProviderRequestEnvelope>(context.Action.Payload);
                terminalMessage = Assert.Single(effective.Messages).Content;
                return ValueTask.FromResult<object>(effective);
            },
            graph.ActionSnapshot,
            CancellationToken.None);

        var completion = new ChatCompletionResult { Content = "original", ToolCalls = [] };
        var resultOutcome = await dispatcher.RunAsync(
            graph.GetStandardAction(SharpClawActions.Provider.AfterTransport),
            new KernelActionEnvelope(SharpClawActions.Provider.AfterTransport, completion),
            (_, _) => ValueTask.FromResult<object>(completion),
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.True(
            inputOutcome.Kind == ActionOutcomeKind.Completed,
            inputOutcome.Error is null
                ? inputOutcome.Kind.ToString()
                : $"{inputOutcome.Error.Code}: {inputOutcome.Error.Message}");
        Assert.Equal("wildcard", terminalMessage);
        Assert.False(StandardWildcardContractInterceptor.SawEnvelopeShape);
        Assert.Equal(ActionOutcomeKind.Completed, resultOutcome.Kind);
        Assert.Equal("replacement", Assert.IsType<ChatCompletionResult>(resultOutcome.Result).Content);
    }

    [Theory]
    [InlineData(ActionOutcomeKind.Completed, "action.completed")]
    [InlineData(ActionOutcomeKind.Cancelled, "action.cancelled")]
    [InlineData(ActionOutcomeKind.Deferred, "action.deferred")]
    [InlineData(ActionOutcomeKind.Failed, "action.failed")]
    public async Task Every_action_emits_starting_and_terminal_lifecycle_events(
        ActionOutcomeKind kind,
        string expectedTerminalKey)
    {
        var key = new SharpClawActionKey($"lifecycle.{kind.ToString().ToLowerInvariant()}");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        var graph = builder.Compile();
        var writer = new LifecycleWriter();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph, eventWriter: writer);
        var continuation = new ContinuationToken(Guid.NewGuid(), "secret");

        var outcome = await dispatcher.RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, null),
            kind switch
            {
                ActionOutcomeKind.Completed => (_, _) => ValueTask.FromResult<object>("done"),
                ActionOutcomeKind.Cancelled => (_, _) => ValueTask.FromException<object>(
                    new KernelActionCancelledException(new ExecutionError("CANCELLED", "cancelled"))),
                ActionOutcomeKind.Deferred => (_, _) => ValueTask.FromException<object>(
                    new KernelActionDeferredException(continuation)),
                _ => (_, _) => ValueTask.FromException<object>(
                    new KernelActionFailedException(new ExecutionError("FAILED", "failed")))
            },
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(kind, outcome.Kind);
        Assert.Equal(["action.starting", expectedTerminalKey], writer.Events.Select(value => value.Key.Value));
        Assert.Null(writer.Events[0].Payload.OutcomeKind);
        Assert.Equal(kind, writer.Events[1].Payload.OutcomeKind);
        Assert.Equal(writer.Events[0].Payload.InvocationId, writer.Events[1].Payload.InvocationId);
    }

    [Fact]
    public async Task Committed_result_isolated_from_retained_hook_reference()
    {
        MutableResultInterceptor.Retained = null;
        var key = new SharpClawActionKey("snapshot.mutable");
        var descriptor = new ActionDescriptor<string, MutableResult>(
            key,
            1,
            "snapshot",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap,
            false,
            false,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "snapshot"),
            null,
            TimeSpan.FromSeconds(5));
        var builder = new KernelGraphBuilder(false);
        builder.Add(descriptor);
        builder.Hooks.For(key).Use<MutableResultInterceptor>(Order("retain"));
        var graph = builder.Compile();

        var outcome = await KernelTestExecution.CreateDispatcher(graph).RunAsync(
            descriptor,
            "input",
            (_, _) => ValueTask.FromResult(new MutableResult { Value = "committed" }),
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
        Assert.NotSame(MutableResultInterceptor.Retained, outcome.Result);
        MutableResultInterceptor.Retained!.Value = "mutated";
        Assert.Equal("committed", outcome.Result!.Value);
    }

    [Fact]
    public async Task Abandoned_claim_can_be_recovered_and_cancellation_is_fenced()
    {
        var store = new TestDurableContinuationStore();
        var firstHost = new StoreBackedContinuationHost(store, TimeSpan.FromSeconds(1));
        var recoveryHost = new StoreBackedContinuationHost(store, TimeSpan.FromSeconds(1));
        var now = DateTimeOffset.UtcNow;
        var request = new KernelContinuationRequest(
            Guid.NewGuid(),
            new SharpClawActionKey("continuation.recovery"),
            1,
            Guid.NewGuid(),
            new ActionDeferRequest(now.AddMinutes(10), "wait"),
            new ActionContinuationPolicy(TimeSpan.FromHours(1), true, true),
            "contract-hash",
            new ContinuationDestination("test", "result"),
            "protected");
        var token = await firstHost.CreateAsync(request, CancellationToken.None);
        var pending = await firstHost.GetAsync(token.TokenId, CancellationToken.None);
        var firstRequest = Claim("worker-a", now.AddSeconds(1), 1, pending!.Revision, request.ContractHash);
        var first = await firstHost.ClaimAsync(
            token.TokenId,
            token.Secret,
            firstRequest,
            now,
            CancellationToken.None);
        Assert.Equal(ContinuationState.Claimed, first!.State);

        var prematureRecovery = Claim(
            "worker-b",
            now.AddMinutes(1),
            2,
            first.Revision,
            request.ContractHash);
        Assert.Null(await recoveryHost.ClaimContinuationRecoveryAsync(
            token.TokenId,
            token.Secret,
            prematureRecovery,
            now.AddMilliseconds(500),
            CancellationToken.None));
        var abandoned = await recoveryHost.ExpireAsync(
            token.TokenId,
            now.AddSeconds(2),
            CancellationToken.None);
        Assert.Equal(ContinuationState.OutcomeUncertain, abandoned!.State);
        Assert.NotNull(abandoned.RecoveryReference);

        var recoveryRequest = Claim(
            "worker-b",
            now.AddMinutes(1),
            2,
            abandoned.Revision,
            request.ContractHash);
        var recoveryClaimed = await recoveryHost.ClaimContinuationRecoveryAsync(
            token.TokenId,
            token.Secret,
            recoveryRequest,
            now.AddSeconds(2),
            CancellationToken.None);

        Assert.Equal(ContinuationState.Claimed, recoveryClaimed!.State);
        Assert.Equal("worker-b", recoveryClaimed.ClaimOwner);
        Assert.Equal(2, recoveryClaimed.Generation);
        Assert.Null(recoveryClaimed.CompletedOutcome);
        Assert.Null(await recoveryHost.CompleteAsync(
            token.TokenId,
            token.Secret,
            firstRequest with { Claim = firstRequest.Claim with { ExpectedRevision = recoveryClaimed.Revision } },
            "stale",
            now.AddSeconds(2),
            CancellationToken.None));
        Assert.Null(await recoveryHost.CancelAsync(
            token.TokenId,
            token.Secret,
            Claim("worker-c", now.AddMinutes(1), 2, recoveryClaimed.Revision, request.ContractHash),
            now.AddSeconds(2),
            CancellationToken.None));

        var recovered = await recoveryHost.RecoverContinuationAsync(
            token.TokenId,
            token.Secret,
            recoveryRequest with
            {
                Claim = recoveryRequest.Claim with { ExpectedRevision = recoveryClaimed.Revision }
            },
            now.AddSeconds(2),
            CancellationToken.None);
        Assert.Equal(ContinuationState.Pending, recovered!.State);
        Assert.Null(recovered.RecoveryReference);

        var resumedClaim = Claim(
            "worker-b",
            now.AddMinutes(1),
            3,
            recovered.Revision,
            request.ContractHash);
        var resumed = await recoveryHost.ClaimAsync(
            token.TokenId,
            token.Secret,
            resumedClaim,
            now.AddSeconds(2),
            CancellationToken.None);
        var current = resumedClaim with
        {
            Claim = resumedClaim.Claim with { ExpectedRevision = resumed!.Revision }
        };
        var requested = await recoveryHost.CancelAsync(
            token.TokenId,
            token.Secret,
            current,
            now.AddSeconds(2),
            CancellationToken.None);
        Assert.Equal(ContinuationState.CancelRequested, requested!.State);
        Assert.Null(await recoveryHost.CompleteAsync(
            token.TokenId,
            token.Secret,
            current with { Claim = current.Claim with { ExpectedRevision = requested.Revision } },
            "late",
            now.AddSeconds(2),
            CancellationToken.None));
        var cancelled = await recoveryHost.CancelAsync(
            token.TokenId,
            token.Secret,
            current with { Claim = current.Claim with { ExpectedRevision = requested.Revision } },
            now.AddSeconds(2),
            CancellationToken.None);
        Assert.Equal(ContinuationState.Cancelled, cancelled!.State);

        var pendingToken = await recoveryHost.CreateAsync(
            request with { InvocationId = Guid.NewGuid() },
            CancellationToken.None);
        var pendingState = await recoveryHost.GetAsync(pendingToken.TokenId, CancellationToken.None);
        var pendingCancelled = await recoveryHost.CancelAsync(
            pendingToken.TokenId,
            pendingToken.Secret,
            Claim("operator", now.AddMinutes(1), 1, pendingState!.Revision, request.ContractHash),
            now,
            CancellationToken.None);
        Assert.Equal(ContinuationState.Cancelled, pendingCancelled!.State);
    }

    [Fact]
    public void Contract_hash_is_identical_under_different_current_cultures()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var french = BuildCultureGraph().ActionSnapshot.ContractHash;

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var turkish = BuildCultureGraph().ActionSnapshot.ContractHash;

            Assert.Equal(french, turkish);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static KernelGraph BuildCultureGraph()
    {
        var key = new SharpClawActionKey("culture.action");
        var builder = new KernelGraphBuilder(false);
        builder.Add(new ActionDescriptor<string, decimal>(
            key,
            2,
            "culture",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap,
            false,
            false,
            new ActionRepeatPolicy(
                ActionRepeatKind.Idempotent,
                3,
                TimeSpan.FromMilliseconds(12.5),
                "culture"),
            new ActionContinuationPolicy(TimeSpan.FromMinutes(2.5), true, true),
            TimeSpan.FromSeconds(1.5))
        {
            SafePoints = [ActionSafePoint.BeforeTerminal, ActionSafePoint.AfterTerminal]
        });
        return builder.Compile();
    }

    private static ActionDescriptor<KernelActionEnvelope, object> Descriptor(SharpClawActionKey key) =>
        new(
            key,
            1,
            "correction",
            AllActionCapabilities,
            false,
            true,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "correction"),
            new ActionContinuationPolicy(TimeSpan.FromHours(1), true, true),
            TimeSpan.FromSeconds(10));

    private static EventDescriptor<SensitivePayload> SensitiveDescriptor(
        SharpClawEventKey key,
        bool sensitive) =>
        new(
            key,
            1,
            sensitive ? "security" : "event",
            EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
            false,
            sensitive);

    private static HookOrdering Order(string id) =>
        new(id, HookPriority.Normal, [], [], TimeSpan.FromSeconds(5), HookFailurePolicy.FailAction);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static KernelContinuationClaim Claim(
        string owner,
        DateTimeOffset leaseExpiresAt,
        int generation,
        long revision,
        string contractHash) =>
        new(new ContinuationClaim(owner, leaseExpiresAt, generation, revision), contractHash);

    private static KernelProviderRequestEnvelope NewProviderEnvelope(string message)
    {
        var input = new ChatTurnInput(message);
        var turn = new ChatTurnContext(
            Guid.NewGuid(),
            input,
            new ConversationSelection(Guid.NewGuid()));
        return new KernelProviderRequestEnvelope(
            new ProviderTurnRequest(
                turn,
                new ChatProfile("provider", Guid.NewGuid()),
                ChatContextContribution.Empty,
                []),
            [ToolAwareMessage.User(message)]);
    }

    private const ActionInterceptionCapabilities AllActionCapabilities =
        ActionInterceptionCapabilities.Inspect |
        ActionInterceptionCapabilities.ReplaceInput |
        ActionInterceptionCapabilities.Cancel |
        ActionInterceptionCapabilities.ReplaceResult |
        ActionInterceptionCapabilities.Defer |
        ActionInterceptionCapabilities.Repeat |
        ActionInterceptionCapabilities.Wrap |
        ActionInterceptionCapabilities.Observe |
        ActionInterceptionCapabilities.PublishEvents;

    private sealed class CancellationTypedInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            control.ProceedAsync(cancellationToken);
    }

    private sealed class CancellationWildcardInterceptor : IAnyActionInterceptor
    {
        public ValueTask<IUntypedActionOutcome> InvokeAsync(
            UntypedActionContext context,
            IUntypedActionControl control,
            CancellationToken cancellationToken) =>
            control.ProceedAsync(cancellationToken);
    }

    private sealed class NeverInvokedInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public static int Calls;

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class GrantRequestModule(SharpClawActionKey key) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("grant.request.module", "Grant request module", "grant_request");

        public void Configure(ISharpClawModuleBuilder module) =>
            module.Hooks.For(key).Use<CancellationTypedInterceptor>(Order("grant-request"));
    }

    private sealed class StandardWildcardContractInterceptor : IAnyActionInterceptor
    {
        public static bool SawEnvelopeShape { get; private set; }

        public static void Reset() => SawEnvelopeShape = false;

        public ValueTask<IUntypedActionOutcome> InvokeAsync(
            UntypedActionContext context,
            IUntypedActionControl control,
            CancellationToken cancellationToken)
        {
            SawEnvelopeShape |= context.Input.TryGetProperty("key", out _) ||
                                context.Input.TryGetProperty("payload", out _);
            if (context.Descriptor.Key == SharpClawActions.Provider.Resolve)
            {
                var request = context.Input.Deserialize<KernelProviderRequestEnvelope>()!;
                return control.ProceedWithInputAsync(
                    JsonSerializer.SerializeToElement(request with
                    {
                        Messages = [ToolAwareMessage.User("wildcard")]
                    }),
                    "Use the manifest payload contract.",
                    cancellationToken);
            }

            return ValueTask.FromResult(control.ReplaceResult(
                JsonSerializer.SerializeToElement(new ChatCompletionResult
                {
                    Content = "replacement",
                    ToolCalls = []
                }),
                "Use the manifest result contract."));
        }
    }

    private sealed record SensitivePayload(string Value);

    private sealed class EventOwnerModule(EventDescriptor<SensitivePayload> descriptor) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("event.owner", "Event owner", "event_owner");

        public void Configure(ISharpClawModuleBuilder module) => module.Events.Add(descriptor);
    }

    private sealed class EventListenerModule(SharpClawEventKey key) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("listener.module", "Listener module", "listener");

        public void Configure(ISharpClawModuleBuilder module) =>
            module.Events.For(key).Listen<SensitiveListener>(EventDelivery.Inline, Order("listener"));
    }

    private sealed class SensitiveListener : IEventListener<SensitivePayload>
    {
        public ValueTask OnEventAsync(
            EventEnvelope<SensitivePayload> envelope,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class LifecycleWriter : ICommittedEventWriter
    {
        public List<(SharpClawEventKey Key, KernelActionLifecycleEvent Payload)> Events { get; } = [];

        public ValueTask PublishAsync<TEvent>(
            EventDescriptor<TEvent> descriptor,
            TEvent payload,
            CancellationToken cancellationToken)
        {
            if (payload is KernelActionLifecycleEvent lifecycle)
                Events.Add((descriptor.Key, lifecycle));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableResult
    {
        public MutableResult()
        {
        }

        public string Value { get; set; } = string.Empty;
    }

    private sealed class MutableResultInterceptor : IActionInterceptor<string, MutableResult>
    {
        public static MutableResult? Retained { get; set; }

        public async ValueTask<IActionOutcome<MutableResult>> InvokeAsync(
            ActionContext<string> context,
            IActionControl<string, MutableResult> control,
            CancellationToken cancellationToken)
        {
            var outcome = await control.ProceedAsync(cancellationToken);
            Retained = outcome.Result;
            return outcome;
        }
    }
}
