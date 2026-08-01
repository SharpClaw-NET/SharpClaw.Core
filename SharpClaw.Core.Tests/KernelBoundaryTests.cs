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
        Assert.Equal("ACTION_CONTROL_CONSUMED", consumed.Error?.Code);
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
            await new KernelActionDispatcher(graph, new InMemoryContinuationHost(true)).RunRequiredAsync(
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
        failBuilder.Add(Descriptor(failKey));
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
        bestEffortBuilder.Add(Descriptor(bestEffortKey));
        bestEffortBuilder.Hooks.For(bestEffortKey).Use<SlowInterceptor>(new HookOrdering(
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

        var host = new InMemoryContinuationHost(true, TimeSpan.FromMinutes(1));
        var outcome = await new KernelActionDispatcher(graph, host).RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (_, _) => ValueTask.FromResult<object>("unused"),
            graph.ActionSnapshot,
            CancellationToken.None);
        Assert.NotNull(outcome.Continuation);
        Assert.True(host.TryGet(outcome.Continuation!.TokenId, out var state));
        Assert.False(host.TryClaim(
            outcome.Continuation.TokenId,
            "BADSECRET",
            "owner-a",
            DateTimeOffset.UtcNow,
            out _));
        Assert.True(host.TryClaim(
            outcome.Continuation.TokenId,
            outcome.Continuation.Secret,
            "owner-a",
            DateTimeOffset.UtcNow,
            out var claimed));
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
        builder.Add(Descriptor(key, KernelActionCapabilities, sensitive: true));
        Assert.Throws<KernelGraphCompilationException>(() => builder.Compile());

        var graph = builder.Compile(options: new KernelGraphCompileOptions
        {
            ApprovedSensitiveActions = new HashSet<string>([key.Value], StringComparer.Ordinal),
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
        var graph = registry.Compile();

        await registry.StartAsync(graph, "host", ExtensionFeatureSet.Empty, CancellationToken.None);
        await registry.StopAsync(CancellationToken.None);

        Assert.Equal(1, LifecycleModule.Starts);
        Assert.Equal(1, LifecycleModule.Stops);
        Assert.Contains("module.start", LifecycleInterceptor.Keys);
        Assert.Contains("module.stop", LifecycleInterceptor.Keys);
    }

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

    private sealed record BoundaryEvent(string Value);

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

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            Keys.Add(context.Action.Key.Value);
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
