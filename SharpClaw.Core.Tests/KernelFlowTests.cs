using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelFlowTests
{
    [Fact]
    public async Task Typed_and_wildcard_event_interceptors_deliver_the_compiled_payload()
    {
        var key = new SharpClawEventKey("sample.event");
        var builder = new KernelGraphBuilder(false);
        builder.AddEvent(new EventDescriptor<SampleEvent>(
            key,
            1,
            "sample",
            EventInterceptionCapabilities.Inspect |
            EventInterceptionCapabilities.Replace |
            EventInterceptionCapabilities.Cancel |
            EventInterceptionCapabilities.StopPropagation |
            EventInterceptionCapabilities.Observe,
            false,
            false));
        builder.Events.For(key).Intercept<ReplaceEvent>(Order("replace"));
        builder.Events.AnyEvent().InterceptAny<PassEvent>(Order("wildcard"));
        builder.Events.For(key).Listen<ObserveEvent>(EventDelivery.Inline, Order("listener"));
        var graph = builder.Compile();
        var dispatcher = new KernelEventDispatcher(graph);

        var result = await dispatcher.DispatchAsync(
            new EventDescriptor<SampleEvent>(
                key,
                1,
                "sample",
                EventInterceptionCapabilities.Inspect |
                EventInterceptionCapabilities.Replace |
                EventInterceptionCapabilities.Cancel |
                EventInterceptionCapabilities.StopPropagation |
                EventInterceptionCapabilities.Observe,
                false,
                false),
            new SampleEvent("before"),
            graph.ActionSnapshot);

        Assert.Equal(EventInterceptionKind.Continued, result.Kind);
        Assert.Equal("after", result.Payload?.Value);
        Assert.Equal("after", ObserveEvent.LastValue);
    }

    [Fact]
    public async Task Unified_tool_pipeline_runs_gates_before_one_handler_path()
    {
        SampleToolHandler.Calls = 0;
        var builder = new KernelGraphBuilder();
        builder.AddTool<SampleToolHandler>(new ToolDescriptor(
            "sample",
            "sample tool",
            JsonSerializer.SerializeToElement(new { type = "object" }),
            1,
            false));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var pipeline = new UnifiedToolPipeline(graph, dispatcher);
        var invocation = NewInvocation("sample");

        var outcome = await pipeline.InvokeAsync(invocation, CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("handled", outcome.Result?.Content);
        Assert.Equal(1, SampleToolHandler.Calls);
    }

    [Fact]
    public async Task A_gate_rejection_prevents_the_handler_effect()
    {
        SampleToolHandler.Calls = 0;
        var builder = new KernelGraphBuilder();
        builder.AddTool<SampleToolHandler>(new ToolDescriptor(
            "sample",
            "sample tool",
            JsonSerializer.SerializeToElement(new { type = "object" }),
            1,
            false));
        var graph = builder.Compile();
        var pipeline = new UnifiedToolPipeline(
            graph,
            KernelTestExecution.CreateDispatcher(graph),
            [new RejectGate()]);

        var outcome = await pipeline.InvokeAsync(NewInvocation("sample"), CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("blocked", outcome.Error?.Code);
        Assert.Equal(0, SampleToolHandler.Calls);
    }

    [Fact]
    public async Task Provider_rounds_feed_tool_results_back_to_the_same_pipeline()
    {
        SampleToolHandler.Calls = 0;
        var builder = new KernelGraphBuilder();
        builder.AddTool<SampleToolHandler>(new ToolDescriptor(
            "sample",
            "sample tool",
            JsonSerializer.SerializeToElement(new { type = "object" }),
            1,
            false));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var pipeline = new UnifiedToolPipeline(graph, dispatcher);
        var transport = new TwoRoundTransport();
        var loop = new ProviderRoundLoop(
            transport,
            graph,
            dispatcher,
            KernelTestExecution.CreateToolContextIssuer());
        var request = NewProviderRequest(graph);

        var completion = await loop.RunAsync(request, pipeline, CancellationToken.None);

        Assert.Equal("final", completion.Content);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(1, SampleToolHandler.Calls);
    }

    [Fact]
    public async Task Provider_issues_one_valid_context_for_each_distinct_tool_call()
    {
        SampleToolHandler.Calls = 0;
        var builder = new KernelGraphBuilder();
        builder.AddTool<SampleToolHandler>(new ToolDescriptor(
            "sample",
            "sample tool",
            JsonSerializer.SerializeToElement(new { type = "object" }),
            1,
            false));
        builder.AddTool<SampleToolHandler>(new ToolDescriptor(
            "other",
            "other tool",
            JsonSerializer.SerializeToElement(new { type = "object" }),
            1,
            false));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var issuer = new TestToolContextIssuer();
        var loop = new ProviderRoundLoop(
            new TwoToolCallTransport(),
            graph,
            dispatcher,
            issuer);
        var parent = new ActionContext<KernelActionEnvelope>(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            1,
            DateTimeOffset.UtcNow.AddMinutes(1),
            SharpClawActions.Chat.ProviderRound,
            "test",
            RequestPrincipal.Anonymous,
            new KernelActionEnvelope(SharpClawActions.Chat.ProviderRound, NewProviderRequest(graph)),
            ExtensionFeatureSet.Empty,
            graph.ActionSnapshot);

        var completion = await loop.RunAsync(
            NewProviderRequest(graph),
            new UnifiedToolPipeline(graph, dispatcher),
            parent,
            CancellationToken.None);

        Assert.Equal("final", completion.Content);
        Assert.Equal(2, issuer.Requests.Count);
        Assert.Equal(["sample", "other"], issuer.Requests.Select(request => request.ToolName));
        Assert.Equal(2, issuer.Requests.Select(request => request.InvocationId).Distinct().Count());
        Assert.All(issuer.Requests, request =>
        {
            Assert.Same(parent, request.ParentActionContext);
            Assert.True(KernelTestExecution.CreateToolContext(
                request.InvocationId,
                request.ToolName,
                request.Arguments,
                parent).IsWellFormed(DateTimeOffset.UtcNow));
        });
        Assert.Equal(2, SampleToolHandler.Calls);
    }

    [Fact]
    public async Task Provider_without_a_host_issuer_fails_before_tool_handler()
    {
        SampleToolHandler.Calls = 0;
        var builder = new KernelGraphBuilder();
        builder.AddTool<SampleToolHandler>(new ToolDescriptor(
            "sample",
            "sample tool",
            JsonSerializer.SerializeToElement(new { type = "object" }),
            1,
            false));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var loop = new ProviderRoundLoop(new TwoRoundTransport(), graph, dispatcher);

        var exception = await Assert.ThrowsAsync<KernelActionExecutionException>(async () =>
            await loop.RunAsync(
                NewProviderRequest(graph),
                new UnifiedToolPipeline(graph, dispatcher),
                CancellationToken.None));

        Assert.Contains("host-issued action context issuer", exception.Message);
        Assert.Equal(0, SampleToolHandler.Calls);
    }

    [Fact]
    public async Task Streaming_rounds_feed_tool_results_back_to_the_same_pipeline()
    {
        SampleToolHandler.Calls = 0;
        var builder = new KernelGraphBuilder();
        builder.AddTool<SampleToolHandler>(new ToolDescriptor(
            "sample",
            "sample tool",
            JsonSerializer.SerializeToElement(new { type = "object" }),
            1,
            false));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var pipeline = new UnifiedToolPipeline(graph, dispatcher);
        var transport = new StreamingTwoRoundTransport();
        var loop = new ProviderRoundLoop(
            transport,
            graph,
            dispatcher,
            KernelTestExecution.CreateToolContextIssuer());
        var chunks = new List<ChatStreamChunk>();

        await foreach (var chunk in loop.StreamAsync(NewProviderRequest(graph), pipeline, CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal(2, transport.Calls);
        Assert.Equal(1, SampleToolHandler.Calls);
        Assert.Equal("partial", chunks[0].Delta);
        Assert.Equal("final", chunks[^1].Finished?.Content);
        Assert.DoesNotContain(chunks, chunk => chunk.Finished?.HasToolCalls == true);
    }

    private static ToolInvocation NewInvocation(string toolName) =>
        KernelTestExecution.CreateToolInvocation(
            toolName,
            JsonSerializer.SerializeToElement(new { value = "sample" }));

    private static ProviderTurnRequest NewProviderRequest(KernelGraph graph)
    {
        var input = new ChatTurnInput(
            "hello",
            null,
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            "test");
        var turn = new ChatTurnContext(
            Guid.NewGuid(),
            input,
            new ConversationSelection(Guid.NewGuid(), true));
        var profile = new ChatProfile(
            "sample",
            Guid.NewGuid(),
            "provider",
            "system",
            null!);
        return new ProviderTurnRequest(turn, profile, ChatContextContribution.Empty, graph.ChatSnapshot.Tools);
    }

    private static HookOrdering Order(string id) =>
        new(id, HookPriority.Normal, Array.Empty<string>(), Array.Empty<string>(), null, HookFailurePolicy.FailAction);

    private sealed record SampleEvent(string Value);

    private sealed class ReplaceEvent : IEventInterceptor<SampleEvent>
    {
        public ValueTask<IEventInterception<SampleEvent>> InterceptAsync(
            EventContext<SampleEvent> context,
            IEventControl<SampleEvent> control,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(control.Replace(new SampleEvent("after"), "test replacement"));
    }

    private sealed class PassEvent : IAnyEventInterceptor
    {
        public ValueTask<IUntypedEventInterception> InterceptAsync(
            UntypedEventContext context,
            IUntypedEventControl control,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(control.Continue());
    }

    private sealed class ObserveEvent : IEventListener<SampleEvent>
    {
        public static string? LastValue { get; set; }

        public ValueTask OnEventAsync(EventEnvelope<SampleEvent> envelope, CancellationToken cancellationToken)
        {
            LastValue = envelope.Payload.Value;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SampleToolHandler : IToolHandler
    {
        public static int Calls { get; set; }

        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(ToolResult.Text("handled"));
        }
    }

    private sealed class RejectGate : IToolInvocationGate
    {
        public ValueTask<ToolGateDecision> EvaluateAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ToolGateDecision>(new ToolGateDecision.Reject("blocked", "blocked by test"));
    }

    private sealed class TwoToolCallTransport : IKernelProviderTransport
    {
        private int _calls;

        public ValueTask<ChatCompletionResult> CompleteAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            CancellationToken cancellationToken)
        {
            _calls++;
            return ValueTask.FromResult(_calls == 1
                ? new ChatCompletionResult
                {
                    Content = "call",
                    ToolCalls =
                    [
                        new ChatToolCall("call-1", "sample", "{}"),
                        new ChatToolCall("call-2", "other", "{}")
                    ]
                }
                : new ChatCompletionResult
                {
                    Content = "final",
                    ToolCalls = Array.Empty<ChatToolCall>()
                });
        }

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return ChatStreamChunk.Final(new ChatCompletionResult
            {
                Content = "final",
                ToolCalls = Array.Empty<ChatToolCall>()
            });
        }
    }

    private sealed class TwoRoundTransport : IKernelProviderTransport
    {
        public int Calls { get; private set; }

        public ValueTask<ChatCompletionResult> CompleteAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Calls == 1
                ? ValueTask.FromResult(new ChatCompletionResult
                {
                    Content = "call",
                    ToolCalls = [new ChatToolCall("call-1", "sample", "{}")]
                })
                : ValueTask.FromResult(new ChatCompletionResult
                {
                    Content = "final",
                    ToolCalls = Array.Empty<ChatToolCall>()
                });
        }

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return ChatStreamChunk.Text("stream");
            yield return ChatStreamChunk.Final(new ChatCompletionResult
            {
                Content = "stream",
                ToolCalls = Array.Empty<ChatToolCall>()
            });
        }
    }

    private sealed class StreamingTwoRoundTransport : IKernelProviderTransport
    {
        public int Calls { get; private set; }

        public ValueTask<ChatCompletionResult> CompleteAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ChatCompletionResult { Content = "unused" });

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Yield();
            if (Calls == 1)
            {
                yield return ChatStreamChunk.Text("partial");
                yield return ChatStreamChunk.Final(new ChatCompletionResult
                {
                    Content = "call",
                    ToolCalls = [new ChatToolCall("call-1", "sample", "{}")]
                });
                yield break;
            }

            yield return ChatStreamChunk.Final(new ChatCompletionResult
            {
                Content = "final",
                ToolCalls = Array.Empty<ChatToolCall>()
            });
        }
    }
}
