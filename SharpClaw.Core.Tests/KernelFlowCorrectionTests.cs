using System.Collections.Concurrent;
using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelFlowCorrectionTests
{
    [Fact]
    public async Task Direct_turn_effects_enter_their_canonical_actions_without_bypass()
    {
        ActionTrace.Reset();
        TracedToolHandler.Calls = 0;
        var builder = new KernelGraphBuilder();
        builder.Hooks.AnyAction().UseAny<ActionTrace>(Order("all-actions"));
        builder.AddTool<TracedToolHandler>(new ToolDescriptor(
            "sample",
            "Sample tool.",
            JsonSerializer.SerializeToElement(new { type = "object" })));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var store = new TracedConversationStore();
        var transport = new TracedTwoRoundTransport();
        var runner = new DirectTurnRunner(
            graph,
            dispatcher,
            new TracedConversationResolver(),
            new TracedProfileResolver(),
            store,
            new KernelChatContextAssembler(graph, dispatcher, [new TracedContributor()]),
            new ProviderRoundLoop(
                transport,
                graph,
                dispatcher,
                KernelTestExecution.CreateToolContextIssuer()),
            new UnifiedToolPipeline(graph, dispatcher));

        var result = await runner.RunAsync(new ChatTurnInput("hello"), CancellationToken.None);

        Assert.Equal("final", result.Completion.Content);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(1, TracedToolHandler.Calls);
        Assert.Equal(1, store.Commits);
        var observed = ActionTrace.Keys.ToHashSet(StringComparer.Ordinal);
        var required = new[]
        {
            "chat.turn.start",
            "chat.conversation.resolve",
            "chat.user_message.prepare",
            "chat.user_message.commit",
            "chat.profile.resolve",
            "chat.history.load",
            "conversation.history.query",
            "chat.context.assemble.start",
            "chat.context.contributor.invoke",
            "chat.context.assemble.complete",
            "chat.tools.collect",
            "chat.tools.select",
            "chat.provider_round.start",
            "provider.resolve",
            "provider.client.create",
            "provider.request.prepare",
            "provider.request.serialize",
            "provider.request.serialize.after",
            "provider.request.send",
            "provider.response.deserialize",
            "provider.response.complete",
            "tool.call.propose",
            "tool.call.parse",
            "tool.call.input.transform",
            "tool.definition.select",
            "tool.call.check",
            "tool.call.coordinate",
            "tool.handler.invoke",
            "tool.result.transform",
            "tool.result.return",
            "chat.provider_round.complete",
            "chat.assistant_message.prepare",
            "conversation.message.prepare",
            "chat.assistant_message.commit",
            "conversation.message.commit",
            "chat.turn.complete"
        };
        Assert.All(required, key => Assert.Contains(key, observed));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("defer")]
    [InlineData("fail")]
    [InlineData("uncertain")]
    public async Task Direct_turn_preserves_inner_provider_round_outcome(string mode)
    {
        OutcomeInterceptor.Mode = mode;
        ActionTrace.Reset();
        var builder = new KernelGraphBuilder();
        builder.Hooks.AnyAction().UseAny<ActionTrace>(Order("all-actions"));
        builder.Hooks.For(SharpClawActions.Chat.ProviderRound)
            .Use<OutcomeInterceptor>(Order($"chat-{mode}"));
        var graph = builder.Compile();
        var store = new TestDurableContinuationStore();
        var host = new StoreBackedContinuationHost(store);
        var dispatcher = KernelTestExecution.CreateDispatcher(graph, host);
        var transport = new CountingTransport();
        var runner = new DirectTurnRunner(
            graph,
            dispatcher,
            new PlainConversationResolver(),
            new PlainProfileResolver(),
            new PlainConversationStore(),
            new KernelChatContextAssembler(graph, dispatcher, []),
            new ProviderRoundLoop(
                transport,
                graph,
                dispatcher,
                KernelTestExecution.CreateToolContextIssuer()),
            new UnifiedToolPipeline(graph, dispatcher));

        var exception = await Record.ExceptionAsync(async () =>
            await runner.RunAsync(new ChatTurnInput("hello"), CancellationToken.None));

        await AssertExceptionOutcomeAsync(mode, exception, host, store);
        Assert.Equal(0, transport.Calls);
        if (mode == "uncertain")
            Assert.DoesNotContain("chat.turn.fail", ActionTrace.Keys);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("defer")]
    [InlineData("fail")]
    [InlineData("uncertain")]
    public async Task Provider_loop_preserves_request_send_outcome(string mode)
    {
        OutcomeInterceptor.Mode = mode;
        ActionTrace.Reset();
        var builder = new KernelGraphBuilder();
        builder.Hooks.AnyAction().UseAny<ActionTrace>(Order("all-actions"));
        builder.Hooks.For(SharpClawActions.Provider.Send)
            .Use<OutcomeInterceptor>(Order($"provider-{mode}"));
        var graph = builder.Compile();
        var store = new TestDurableContinuationStore();
        var host = new StoreBackedContinuationHost(store);
        var dispatcher = KernelTestExecution.CreateDispatcher(graph, host);
        var transport = new CountingTransport();

        var exception = await Record.ExceptionAsync(async () =>
            await new ProviderRoundLoop(
                    transport,
                    graph,
                    dispatcher,
                    KernelTestExecution.CreateToolContextIssuer()).RunAsync(
                NewProviderRequest(graph),
                new UnifiedToolPipeline(graph, dispatcher),
                CancellationToken.None));

        await AssertExceptionOutcomeAsync(mode, exception, host, store);
        Assert.Equal(0, transport.Calls);
        if (mode == "uncertain")
            Assert.DoesNotContain("provider.request.fail", ActionTrace.Keys);
    }

    [Theory]
    [InlineData("cancel", ActionOutcomeKind.Cancelled)]
    [InlineData("defer", ActionOutcomeKind.Deferred)]
    [InlineData("fail", ActionOutcomeKind.Failed)]
    [InlineData("uncertain", ActionOutcomeKind.Uncertain)]
    public async Task Tool_pipeline_preserves_inner_coordinate_outcome(
        string mode,
        ActionOutcomeKind expected)
    {
        OutcomeInterceptor.Mode = mode;
        TracedToolHandler.Calls = 0;
        ActionTrace.Reset();
        var builder = new KernelGraphBuilder();
        builder.Hooks.AnyAction().UseAny<ActionTrace>(Order("all-actions"));
        builder.AddTool<TracedToolHandler>(new ToolDescriptor(
            "sample",
            "Sample tool.",
            JsonSerializer.SerializeToElement(new { type = "object" })));
        builder.Hooks.For(SharpClawActions.Tools.Coordinate)
            .Use<OutcomeInterceptor>(Order($"tool-{mode}"));
        var graph = builder.Compile();
        var store = new TestDurableContinuationStore();
        var host = new StoreBackedContinuationHost(store);
        var dispatcher = KernelTestExecution.CreateDispatcher(graph, host);

        var outcome = await new UnifiedToolPipeline(graph, dispatcher).InvokeAsync(
            NewToolInvocation(),
            CancellationToken.None);

        Assert.Equal(expected, outcome.Kind);
        Assert.Equal(0, TracedToolHandler.Calls);
        if (expected == ActionOutcomeKind.Deferred)
        {
            Assert.NotNull(outcome.Continuation);
            Assert.Equal(
                ContinuationState.Pending,
                (await host.GetAsync(outcome.Continuation!.TokenId, CancellationToken.None))!.State);
        }
        if (expected == ActionOutcomeKind.Uncertain)
        {
            Assert.NotNull(outcome.Uncertainty);
            Assert.Equal(1, store.RecoveryCount);
            Assert.NotNull(await host.GetRecoveryAsync(
                outcome.Uncertainty!.Recovery.RecoveryId,
                CancellationToken.None));
            Assert.DoesNotContain("tool.call.fail", ActionTrace.Keys);
        }
    }

    [Fact]
    public async Task Tool_pipeline_rejects_mismatched_host_authority_before_handler()
    {
        TracedToolHandler.Calls = 0;
        var builder = new KernelGraphBuilder();
        builder.AddTool<TracedToolHandler>(new ToolDescriptor(
            "sample",
            "Sample tool.",
            JsonSerializer.SerializeToElement(new { type = "object" })));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var pipeline = new UnifiedToolPipeline(graph, dispatcher);
        var valid = NewToolInvocation();
        var invalid = new[]
        {
            valid with { InvocationId = Guid.NewGuid() },
            valid with { ToolName = "other" },
            valid with
            {
                HostActionContext = valid.HostActionContext with
                {
                    Ingress = HostActionEntryIngress.Cli
                }
            },
            valid with
            {
                HostActionContext = valid.HostActionContext with
                {
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
                }
            },
            valid with
            {
                HostActionContext = valid.HostActionContext with
                {
                    Contribution = valid.HostActionContext.Contribution! with
                    {
                        IngressBinding = new HostActionEntryIngressBinding(
                            HostActionEntryIngress.Tool,
                            "other",
                            null!)
                    }
                }
            },
            valid with { HostActionContext = null! }
        };

        foreach (var invocation in invalid)
        {
            var outcome = await pipeline.InvokeAsync(invocation, CancellationToken.None);
            Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        }

        Assert.Equal(0, TracedToolHandler.Calls);
    }

    [Theory]
    [InlineData("tool.call.parse")]
    [InlineData("tool.call.input.transform")]
    [InlineData("tool.call.propose")]
    [InlineData("tool.definition.select")]
    [InlineData("tool.call.check")]
    [InlineData("tool.call.coordinate")]
    [InlineData("tool.handler.invoke")]
    public async Task Tool_pipeline_rechecks_authority_after_each_effective_boundary(string phase)
    {
        InnerToolAuthorityReplacementInterceptor.Phase = phase;
        InnerToolAuthorityReplacementInterceptor.Mutation = "ToolName";
        TracedToolHandler.Calls = 0;
        var builder = new KernelGraphBuilder();
        builder.AddTool<TracedToolHandler>(new ToolDescriptor(
            "sample",
            "Sample tool.",
            JsonSerializer.SerializeToElement(new { type = "object" })));
        builder.Hooks.For(new SharpClawActionKey(phase))
            .Use<InnerToolAuthorityReplacementInterceptor>(Order(phase));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);

        var outcome = await new UnifiedToolPipeline(graph, dispatcher).InvokeAsync(
            NewToolInvocation(),
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(0, TracedToolHandler.Calls);
    }

    [Fact]
    public async Task Tool_pipeline_rejects_replaced_resolution_handler_before_effects()
    {
        TracedToolHandler.Calls = 0;
        AlternateToolHandler.Calls = 0;
        var builder = new KernelGraphBuilder();
        builder.AddTool<TracedToolHandler>(new ToolDescriptor(
            "sample",
            "Sample tool.",
            JsonSerializer.SerializeToElement(new { type = "object" })));
        builder.AddTool<AlternateToolHandler>(new ToolDescriptor(
            "other",
            "Other tool.",
            JsonSerializer.SerializeToElement(new { type = "object" })));
        builder.Hooks.For(new SharpClawActionKey("tool.definition.select"))
            .Use<ResolutionResultReplacementInterceptor>(Order("resolution-result"));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var gate = new CountingToolGate();
        var coordinator = new CountingToolCoordinator();
        var pipeline = new UnifiedToolPipeline(graph, dispatcher, [gate], coordinator);

        var outcome = await pipeline.InvokeAsync(NewToolInvocation(), CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("TOOL_INVOCATION_AUTHORITY_CHANGED", outcome.Error?.Code);
        Assert.Equal(0, gate.Calls);
        Assert.Equal(0, coordinator.Calls);
        Assert.Equal(0, TracedToolHandler.Calls);
        Assert.Equal(0, AlternateToolHandler.Calls);
    }

    [Theory]
    [InlineData("InvocationId")]
    [InlineData("ConversationId")]
    [InlineData("ToolCallId")]
    [InlineData("ToolName")]
    [InlineData("Ingress")]
    [InlineData("Contribution")]
    [InlineData("ExpiresAt")]
    public async Task Tool_pipeline_rejects_each_inner_authority_mutation(string mutation)
    {
        InnerToolAuthorityReplacementInterceptor.Phase = "tool.call.parse";
        InnerToolAuthorityReplacementInterceptor.Mutation = mutation;
        TracedToolHandler.Calls = 0;
        var builder = new KernelGraphBuilder();
        builder.AddTool<TracedToolHandler>(new ToolDescriptor(
            "sample",
            "Sample tool.",
            JsonSerializer.SerializeToElement(new { type = "object" })));
        builder.Hooks.For(new SharpClawActionKey("tool.call.parse"))
            .Use<InnerToolAuthorityReplacementInterceptor>(Order("inner-authority"));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);

        var outcome = await new UnifiedToolPipeline(graph, dispatcher).InvokeAsync(
            NewToolInvocation(),
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(0, TracedToolHandler.Calls);
    }

    [Fact]
    public async Task Streaming_stops_before_reading_data_after_first_final_chunk()
    {
        var graph = new KernelGraphBuilder().Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var transport = new FinalThenExtraTransport();
        var chunks = new List<ChatStreamChunk>();

        await foreach (var chunk in new ProviderRoundLoop(
                           transport,
                           graph,
                           dispatcher,
                           KernelTestExecution.CreateToolContextIssuer()).StreamAsync(
                           NewProviderRequest(graph),
                           new UnifiedToolPipeline(graph, dispatcher),
                           CancellationToken.None))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal("final", chunks[0].Finished!.Content);
        Assert.Equal(0, transport.ReadsAfterFinal);
    }

    [Fact]
    public async Task Streaming_returns_the_replaced_completed_response()
    {
        var builder = new KernelGraphBuilder();
        builder.Hooks.For(SharpClawActions.Provider.AfterTransport)
            .Use<ResponseCompletionReplacementInterceptor>(Order("replace-completion"));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var chunks = new List<ChatStreamChunk>();

        await foreach (var chunk in new ProviderRoundLoop(
                           new FinalThenExtraTransport(),
                           graph,
                           dispatcher,
                           KernelTestExecution.CreateToolContextIssuer()).StreamAsync(
                           NewProviderRequest(graph),
                           new UnifiedToolPipeline(graph, dispatcher),
                           CancellationToken.None))
            chunks.Add(chunk);

        var final = Assert.Single(chunks);
        Assert.Equal("replacement", final.Finished!.Content);
    }

    private static async ValueTask AssertExceptionOutcomeAsync(
        string mode,
        Exception? exception,
        StoreBackedContinuationHost host,
        TestDurableContinuationStore store)
    {
        switch (mode)
        {
            case "cancel":
                Assert.IsType<KernelActionCancelledException>(exception);
                break;
            case "defer":
                {
                    var deferred = Assert.IsType<KernelActionDeferredException>(exception);
                    Assert.Equal(
                        ContinuationState.Pending,
                        (await host.GetAsync(deferred.Continuation.TokenId, CancellationToken.None))!.State);
                    break;
                }
            case "fail":
                Assert.IsType<KernelActionFailedException>(exception);
                break;
            case "uncertain":
                {
                    var uncertain = Assert.IsType<ActionOutcomeUncertainException>(exception);
                    Assert.Equal(1, store.RecoveryCount);
                    Assert.NotNull(await host.GetRecoveryAsync(
                        uncertain.Uncertainty.Recovery.RecoveryId,
                        CancellationToken.None));
                    break;
                }
            default:
                throw new InvalidOperationException($"Unknown test mode '{mode}'.");
        }
    }

    private static ProviderTurnRequest NewProviderRequest(KernelGraph graph)
    {
        var input = new ChatTurnInput("hello", Caller: RequestPrincipal.Anonymous);
        return new ProviderTurnRequest(
            new ChatTurnContext(Guid.NewGuid(), input, new ConversationSelection(Guid.NewGuid())),
            new ChatProfile("provider", Guid.NewGuid()),
            ChatContextContribution.Empty,
            graph.ChatSnapshot.Tools);
    }

    private static ToolInvocation NewToolInvocation() =>
        KernelTestExecution.CreateToolInvocation("sample");

    private static HookOrdering Order(string id) =>
        new(id, HookPriority.Normal, [], [], TimeSpan.FromSeconds(5), HookFailurePolicy.FailAction);

    private sealed class ActionTrace : IAnyActionInterceptor
    {
        private static readonly ConcurrentQueue<string> RecordedKeys = new();
        private static readonly AsyncLocal<string?> CurrentKey = new();

        public static IReadOnlyList<string> Keys => RecordedKeys.ToArray();

        public static void Reset()
        {
            while (RecordedKeys.TryDequeue(out _))
            {
            }
            CurrentKey.Value = null;
        }

        public static void RequireCurrent(string key) =>
            Assert.Equal(key, CurrentKey.Value);

        public async ValueTask<IUntypedActionOutcome> InvokeAsync(
            UntypedActionContext context,
            IUntypedActionControl control,
            CancellationToken cancellationToken)
        {
            RecordedKeys.Enqueue(context.Descriptor.Key.Value);
            var previous = CurrentKey.Value;
            CurrentKey.Value = context.Descriptor.Key.Value;
            try
            {
                return await control.ProceedAsync(cancellationToken);
            }
            finally
            {
                CurrentKey.Value = previous;
            }
        }
    }

    private sealed class OutcomeInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public static string Mode { get; set; } = "fail";

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) => Mode switch
            {
                "cancel" => ValueTask.FromResult(control.Cancel("TEST_CANCELLED", "cancelled")),
                "defer" => control.DeferAsync(
                    new ActionDeferRequest(DateTimeOffset.UtcNow.AddMinutes(5), "wait"),
                    cancellationToken),
                "fail" => ValueTask.FromResult(control.Fail(new ExecutionError("TEST_FAILED", "failed"))),
                "uncertain" => ValueTask.FromException<IActionOutcome<object>>(
                    new ActionOutcomeUncertainException(new ActionUncertainty(
                        "TEST_UNCERTAIN",
                        "uncertain",
                        ActionExecutionStage.BeforeContinuation,
                        null,
                        new ActionRecoveryReference(
                            Guid.NewGuid(),
                            context.ActionKey,
                            1,
                            context.IdempotencyKey),
                        DateTimeOffset.UtcNow))),
                _ => throw new InvalidOperationException($"Unknown test mode '{Mode}'.")
            };
    }

    private sealed class InnerToolAuthorityReplacementInterceptor :
        IActionInterceptor<KernelActionEnvelope, object>
    {
        public static string Phase { get; set; } = "tool.call.parse";
        public static string Mutation { get; set; } = "ToolName";

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            if (context.Action.Payload is not ToolInvocation invocation ||
                !string.Equals(context.Action.Key.Value, Phase, StringComparison.Ordinal))
                return control.ProceedAsync(cancellationToken);

            var replacement = Mutation switch
            {
                "InvocationId" => invocation with { InvocationId = Guid.NewGuid() },
                "ConversationId" => invocation with { ConversationId = Guid.NewGuid() },
                "ToolCallId" => invocation with { ToolCallId = "changed-call" },
                "ToolName" => invocation with { ToolName = "other" },
                "Ingress" => invocation with
                {
                    HostActionContext = invocation.HostActionContext! with
                    {
                        Ingress = HostActionEntryIngress.Cli
                    }
                },
                "Contribution" => invocation with
                {
                    HostActionContext = invocation.HostActionContext! with
                    {
                        Contribution = invocation.HostActionContext.Contribution! with
                        {
                            IngressBinding = new HostActionEntryIngressBinding(
                                HostActionEntryIngress.Tool,
                                "other",
                                null!)
                        }
                    }
                },
                "ExpiresAt" => invocation with
                {
                    HostActionContext = invocation.HostActionContext! with
                    {
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
                    }
                },
                _ => throw new InvalidOperationException($"Unknown mutation '{Mutation}'.")
            };

            return control.ProceedWithInputAsync(
                new ActionReplacement<KernelActionEnvelope>(
                    context.Action with { Payload = replacement },
                    "mutate tool authority for rejection test"),
                cancellationToken);
        }
    }

    private sealed class ResponseCompletionReplacementInterceptor :
        IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(control.ReplaceResult(
                new ChatCompletionResult
                {
                    Content = "replacement",
                    ToolCalls = []
                },
                "Replace the completed provider response."));
    }

    private sealed class TracedConversationResolver : IConversationResolver
    {
        public ValueTask<ConversationSelection> ResolveAsync(
            ChatTurnInput input,
            ChatOperationContext context,
            CancellationToken cancellationToken)
        {
            ActionTrace.RequireCurrent("chat.conversation.resolve");
            return ValueTask.FromResult(new ConversationSelection(Guid.NewGuid()));
        }
    }

    private sealed class TracedProfileResolver : IChatProfileResolver
    {
        public ValueTask<ChatProfile> ResolveAsync(
            ChatTurnContext turn,
            ChatOperationContext context,
            CancellationToken cancellationToken)
        {
            ActionTrace.RequireCurrent("chat.profile.resolve");
            return ValueTask.FromResult(new ChatProfile("provider", Guid.NewGuid(), SystemPrompt: "system"));
        }
    }

    private sealed class TracedConversationStore : IConversationStore
    {
        public int Commits { get; private set; }

        public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
            Guid conversationId,
            ChatOperationContext context,
            CancellationToken cancellationToken)
        {
            ActionTrace.RequireCurrent("conversation.history.query");
            return ValueTask.FromResult<IReadOnlyList<ChatCompletionMessage>>(
                [new ChatCompletionMessage("assistant", "prior")]);
        }

        public ValueTask CommitExchangeAsync(
            ChatExchange exchange,
            ChatOperationContext context,
            CancellationToken cancellationToken)
        {
            ActionTrace.RequireCurrent("conversation.message.commit");
            Commits++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TracedContributor : IChatContextContributor
    {
        public ValueTask<ChatContextContribution> ContributeAsync(
            ChatContextRequest request,
            ChatOperationContext context,
            CancellationToken cancellationToken)
        {
            ActionTrace.RequireCurrent("chat.context.contributor.invoke");
            return ValueTask.FromResult(ChatContextContribution.Empty);
        }
    }

    private sealed class TracedTwoRoundTransport : IKernelProviderTransport
    {
        public int Calls { get; private set; }

        public ValueTask<ChatCompletionResult> CompleteAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            CancellationToken cancellationToken)
        {
            ActionTrace.RequireCurrent("provider.request.send");
            Calls++;
            return Calls == 1
                ? ValueTask.FromResult(new ChatCompletionResult
                {
                    Content = "tool",
                    ToolCalls = [new ChatToolCall("call", "sample", "{}")]
                })
                : ValueTask.FromResult(new ChatCompletionResult
                {
                    Content = "final",
                    ToolCalls = []
                });
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

    private sealed class TracedToolHandler : IToolHandler
    {
        public static int Calls { get; set; }

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            ActionTrace.RequireCurrent("tool.handler.invoke");
            Calls++;
            return ValueTask.FromResult(ToolResult.Text("tool-result"));
        }
    }

    private sealed class AlternateToolHandler : IToolHandler
    {
        public static int Calls { get; set; }

        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(ToolResult.Text("alternate-result"));
        }
    }

    private sealed class ResolutionResultReplacementInterceptor :
        IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            if (context.Action.Payload is not ToolInvocation invocation)
                return control.ProceedAsync(cancellationToken);

            return ValueTask.FromResult(control.ReplaceResult(
                new KernelToolResolution(invocation, "other"),
                "replace selected handler"));
        }
    }

    private sealed class CountingToolGate : IToolInvocationGate
    {
        public int Calls { get; private set; }

        public ValueTask<ToolGateDecision> EvaluateAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult<ToolGateDecision>(new ToolGateDecision.Continue());
        }
    }

    private sealed class CountingToolCoordinator : IToolExecutionCoordinator
    {
        public int Calls { get; private set; }

        public ValueTask<ToolInvocationOutcome> CoordinateAsync(
            ToolExecutionPlan plan,
            ToolExecutionDelegate terminal,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(
                ToolInvocationOutcome.Rejected("TEST_UNEXPECTED_COORDINATION", "Unexpected coordination."));
        }
    }

    private sealed class PlainConversationResolver : IConversationResolver
    {
        public ValueTask<ConversationSelection> ResolveAsync(
            ChatTurnInput input,
            ChatOperationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ConversationSelection(Guid.NewGuid()));
    }

    private sealed class PlainProfileResolver : IChatProfileResolver
    {
        public ValueTask<ChatProfile> ResolveAsync(
            ChatTurnContext turn,
            ChatOperationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ChatProfile("provider", Guid.NewGuid()));
    }

    private sealed class PlainConversationStore : IConversationStore
    {
        public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
            Guid conversationId,
            ChatOperationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ChatCompletionMessage>>([]);

        public ValueTask CommitExchangeAsync(
            ChatExchange exchange,
            ChatOperationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class CountingTransport : IKernelProviderTransport
    {
        public int Calls { get; private set; }

        public ValueTask<ChatCompletionResult> CompleteAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new ChatCompletionResult { Content = "done", ToolCalls = [] });
        }

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Yield();
            yield return ChatStreamChunk.Final(new ChatCompletionResult { Content = "done", ToolCalls = [] });
        }
    }

    private sealed class FinalThenExtraTransport : IKernelProviderTransport
    {
        public int ReadsAfterFinal { get; private set; }

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
            yield return ChatStreamChunk.Final(new ChatCompletionResult { Content = "final", ToolCalls = [] });
            ReadsAfterFinal++;
            yield return ChatStreamChunk.Text("late");
        }
    }
}
