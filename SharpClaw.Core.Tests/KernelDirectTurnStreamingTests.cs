using System.Collections.Concurrent;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelDirectTurnStreamingTests
{
    [Fact]
    public async Task Streaming_matches_buffered_completion_and_commits_once()
    {
        var bufferedGraph = new KernelGraphBuilder().Compile();
        var bufferedContext = TestContext();
        var bufferedStore = new RecordingStore();
        var bufferedResult = await CreateRunner(
                bufferedGraph,
                new KernelActionDispatcher(bufferedGraph, bufferedContext),
                new ParityTransport(),
                bufferedStore)
            .RunAsync(new ChatTurnInput("hello"));

        var streamingGraph = new KernelGraphBuilder().Compile();
        var streamingContext = TestContext();
        var streamingStore = new RecordingStore();
        var chunks = await CollectAsync(
            CreateRunner(
                streamingGraph,
                new KernelActionDispatcher(streamingGraph, streamingContext),
                new ParityTransport(),
                streamingStore)
            .StreamAsync(new ChatTurnInput("hello")));

        Assert.Equal(bufferedResult.Completion.Content, chunks[^1].Finished?.Content);
        Assert.Equal(["hello", " "], chunks.Take(2).Select(chunk => chunk.Delta));
        Assert.Equal(1, bufferedStore.Commits);
        Assert.Equal(1, streamingStore.Commits);
        Assert.Equal(bufferedResult.Completion.Content, streamingStore.LastExchange?.Completion.Content);
    }

    [Fact]
    public async Task Streaming_preserves_action_order_and_emits_final_after_one_commit()
    {
        var graphBuilder = new KernelGraphBuilder();
        graphBuilder.Hooks.AnyAction().UseAny<RecordingInterceptor>(Order("stream-trace"));
        var graph = graphBuilder.Compile();
        var context = TestContext();
        var store = new RecordingStore();
        var chunks = await CollectAsync(
            CreateRunner(
                graph,
                new KernelActionDispatcher(graph, context),
                new ParityTransport(),
                store)
            .StreamAsync(new ChatTurnInput("hello")));

        var observations = RecordingInterceptor.Items
            .Where(item => item.IdempotencyKey == context.IdempotencyKey)
            .Select(item => item.ActionKey)
            .ToList();

        Assert.Equal(1, store.Commits);
        Assert.Equal("chat.turn.start", observations[0]);
        Assert.True(
            observations.IndexOf("conversation.message.commit") <
            observations.IndexOf("chat.turn.complete"));
        Assert.True(chunks[^1].IsFinished);
        Assert.DoesNotContain(chunks.Take(chunks.Count - 1), chunk => chunk.IsFinished);
    }

    [Fact]
    public async Task Cancellation_dispatches_turn_cancel_and_never_commits()
    {
        var graphBuilder = new KernelGraphBuilder();
        graphBuilder.Hooks.AnyAction().UseAny<RecordingInterceptor>(Order("cancel-trace"));
        var graph = graphBuilder.Compile();
        var context = TestContext();
        var store = new RecordingStore();
        using var cancellation = new CancellationTokenSource();
        var exception = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in CreateRunner(
                               graph,
                               new KernelActionDispatcher(graph, context),
                               new BlockingTransport(),
                               store)
                           .StreamAsync(new ChatTurnInput("hello"), cancellation.Token))
            {
                cancellation.Cancel();
            }
        });

        var observations = RecordingInterceptor.Items
            .Where(item => item.IdempotencyKey == context.IdempotencyKey)
            .Select(item => item.ActionKey)
            .ToArray();
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(0, store.Commits);
        Assert.Contains("chat.turn.cancel", observations);
        Assert.DoesNotContain("chat.turn.complete", observations);
    }

    [Fact]
    public async Task Provider_failure_dispatches_turn_fail_and_never_commits()
    {
        var graphBuilder = new KernelGraphBuilder();
        graphBuilder.Hooks.AnyAction().UseAny<RecordingInterceptor>(Order("failure-trace"));
        var graph = graphBuilder.Compile();
        var context = TestContext();
        var store = new RecordingStore();
        var exception = await Record.ExceptionAsync(async () =>
            await CollectAsync(
                CreateRunner(
                    graph,
                    new KernelActionDispatcher(graph, context),
                    new FailingTransport(),
                    store)
                .StreamAsync(new ChatTurnInput("hello"))));

        var observations = RecordingInterceptor.Items
            .Where(item => item.IdempotencyKey == context.IdempotencyKey)
            .Select(item => item.ActionKey)
            .ToArray();
        var failed = Assert.IsType<KernelActionFailedException>(exception);
        Assert.Contains("provider failure", failed.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.Commits);
        Assert.Contains("chat.turn.fail", observations);
        Assert.DoesNotContain("chat.turn.complete", observations);
    }

    [Fact]
    public async Task Incomplete_provider_stream_fails_without_persistence()
    {
        var graph = new KernelGraphBuilder().Compile();
        var store = new RecordingStore();
        var exception = await Record.ExceptionAsync(async () =>
            await CollectAsync(
                CreateRunner(
                    graph,
                    new KernelActionDispatcher(graph, TestContext()),
                    new IncompleteTransport(),
                    store)
                .StreamAsync(new ChatTurnInput("hello"))));

        var failed = Assert.IsType<KernelActionFailedException>(exception);
        Assert.Contains("without a completion", failed.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public async Task Early_consumer_disposal_cancels_the_active_operation_without_persistence()
    {
        var graph = new KernelGraphBuilder().Compile();
        var store = new RecordingStore();
        var runner = CreateRunner(
            graph,
            new KernelActionDispatcher(graph, TestContext()),
            new BlockingTransport(),
            store);
        await using var enumerator = runner.StreamAsync(new ChatTurnInput("hello")).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync();

        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public async Task Terminal_reporting_failure_does_not_replace_the_first_provider_failure()
    {
        var graphBuilder = new KernelGraphBuilder();
        graphBuilder.Hooks.For(new SharpClawActionKey("chat.turn.fail"))
            .Use<FailingTerminalInterceptor>(Order("fail-report"));
        var graph = graphBuilder.Compile();
        var store = new RecordingStore();
        var exception = await Record.ExceptionAsync(async () =>
            await CollectAsync(
                CreateRunner(
                    graph,
                    new KernelActionDispatcher(graph, TestContext()),
                    new FailingTransport(),
                    store)
                .StreamAsync(new ChatTurnInput("hello"))));

        var failed = Assert.IsType<KernelActionFailedException>(exception);
        Assert.Contains("provider failure", failed.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public async Task Root_result_replacement_fails_closed_before_commit_and_final_chunk()
    {
        var graphBuilder = new KernelGraphBuilder();
        graphBuilder.Hooks.For(SharpClawActions.Chat.Turn)
            .Use<RootResultReplacementInterceptor>(Order("replace-root"));
        var graph = graphBuilder.Compile();
        var store = new RecordingStore();
        var chunks = new List<ChatStreamChunk>();
        var exception = await Record.ExceptionAsync(async () =>
        {
            chunks = await CollectAsync(
                CreateRunner(
                    graph,
                    new KernelActionDispatcher(graph, TestContext()),
                    new ParityTransport(),
                    store)
                .StreamAsync(new ChatTurnInput("hello")));
        });

        var failed = Assert.IsType<KernelActionExecutionException>(exception);
        Assert.Contains("terminal callback", failed.Message, StringComparison.Ordinal);
        Assert.Empty(chunks);
        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public async Task Concurrent_active_request_contexts_remain_isolated()
    {
        RecordingInterceptor.Clear();
        var graphBuilder = new KernelGraphBuilder();
        graphBuilder.Hooks.AnyAction().UseAny<RecordingInterceptor>(Order("context-trace"));
        var graph = graphBuilder.Compile();
        var dispatcher = new KernelActionDispatcher(graph, TestContext());
        var store = new RecordingStore();
        var runner = CreateRunner(graph, dispatcher, new ContextTransport(), store);
        var first = TestContext("caller-a");
        var second = TestContext("caller-b");
        var rootKey = new SharpClawActionKey("runtime.request.receive");

        await Task.WhenAll(
            RunUnderContextAsync(first, "a"),
            RunUnderContextAsync(second, "b"));

        var observations = RecordingInterceptor.Items.ToArray();
        Assert.NotEmpty(observations);
        Assert.All(
            observations.Where(item => item.TraceId == first.TraceId),
            item =>
            {
                Assert.Equal(first.Caller.SubjectId, item.Caller.SubjectId);
                Assert.Equal(first.IdempotencyKey, item.IdempotencyKey);
            });
        Assert.All(
            observations.Where(item => item.TraceId == second.TraceId),
            item =>
            {
                Assert.Equal(second.Caller.SubjectId, item.Caller.SubjectId);
                Assert.Equal(second.IdempotencyKey, item.IdempotencyKey);
            });
        Assert.Equal(2, store.Commits);

        async Task RunUnderContextAsync(KernelActionExecutionContext context, string message)
        {
            await dispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
                context,
                graph.GetStandardAction(rootKey),
                new KernelActionEnvelope(rootKey, message),
                async (_, cancellationToken) =>
                {
                    await CollectAsync(runner.StreamAsync(new ChatTurnInput(message), cancellationToken));
                    return (object)true;
                },
                graph.ActionSnapshot,
                CancellationToken.None);
        }
    }

    private static DirectTurnRunner CreateRunner(
        KernelGraph graph,
        KernelActionDispatcher dispatcher,
        IKernelProviderTransport transport,
        RecordingStore store) =>
        new(
            graph,
            dispatcher,
            new ConversationResolver(),
            new ProfileResolver(),
            store,
            new KernelChatContextAssembler(graph, dispatcher, []),
            new ProviderRoundLoop(transport, graph, dispatcher),
            new UnifiedToolPipeline(graph, dispatcher));

    private static async Task<List<ChatStreamChunk>> CollectAsync(
        IAsyncEnumerable<ChatStreamChunk> stream)
    {
        var chunks = new List<ChatStreamChunk>();
        await foreach (var chunk in stream)
            chunks.Add(chunk);
        return chunks;
    }

    private static KernelActionExecutionContext TestContext(string subject = "test") =>
        new(
            new RequestPrincipal(subject, subject, new HashSet<string> { "operator" }, true),
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());

    private static HookOrdering Order(string id) =>
        new(id, HookPriority.Normal, [], [], TimeSpan.FromSeconds(5), HookFailurePolicy.FailAction);

    private sealed class RecordingInterceptor : IAnyActionInterceptor
    {
        public static ConcurrentQueue<Observation> Items { get; } = new();

        public static void Clear()
        {
            while (Items.TryDequeue(out _))
            {
            }
        }

        public ValueTask<IUntypedActionOutcome> InvokeAsync(
            UntypedActionContext context,
            IUntypedActionControl control,
            CancellationToken cancellationToken)
        {
            Items.Enqueue(new(
                context.Descriptor.Key.Value,
                context.TraceId,
                context.IdempotencyKey,
                context.Caller));
            return control.ProceedAsync(cancellationToken);
        }
    }

    private sealed record Observation(
        string ActionKey,
        Guid TraceId,
        Guid IdempotencyKey,
        RequestPrincipal Caller);

    private sealed class FailingTerminalInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IActionOutcome<object>>(
                control.Fail(new ExecutionError("REPORT_FAILED", "terminal reporting failed")));
    }

    private sealed class RootResultReplacementInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IActionOutcome<object>>(
                control.ReplaceResult(
                    new ChatTurnResult(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        new ChatCompletionResult
                        {
                            Content = "replaced",
                            ToolCalls = []
                        },
                        []),
                    "Replace the root result without running the direct turn."));
    }

    private sealed class ConversationResolver : IConversationResolver
    {
        public ValueTask<ConversationSelection> ResolveAsync(
            ChatTurnInput input,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ConversationSelection(Guid.NewGuid()));
    }

    private sealed class ProfileResolver : IChatProfileResolver
    {
        public ValueTask<ChatProfile> ResolveAsync(
            ChatTurnContext turn,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ChatProfile("provider", Guid.NewGuid(), SystemPrompt: "system"));
    }

    private sealed class RecordingStore : IConversationStore
    {
        private int _commits;
        public int Commits => Volatile.Read(ref _commits);
        public ChatExchange? LastExchange { get; private set; }

        public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
            Guid conversationId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ChatCompletionMessage>>([]);

        public ValueTask CommitExchangeAsync(
            ChatExchange exchange,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _commits);
            LastExchange = exchange;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ParityTransport : IKernelProviderTransport
    {
        private static ChatCompletionResult Completion => new()
        {
            Content = "hello world",
            ToolCalls = []
        };

        public ValueTask<ChatCompletionResult> CompleteAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Completion);

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return ChatStreamChunk.Text("hello");
            yield return ChatStreamChunk.Text(" ");
            yield return ChatStreamChunk.Final(Completion);
        }
    }

    private sealed class BlockingTransport : IKernelProviderTransport
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
            yield return ChatStreamChunk.Text("partial");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FailingTransport : IKernelProviderTransport
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
            yield return ChatStreamChunk.Text("partial");
            await Task.Yield();
            throw new InvalidOperationException("provider failure");
        }
    }

    private sealed class IncompleteTransport : IKernelProviderTransport
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
            yield return ChatStreamChunk.Text("partial");
            await Task.Yield();
        }
    }

    private sealed class ContextTransport : IKernelProviderTransport
    {
        public ValueTask<ChatCompletionResult> CompleteAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ChatCompletionResult
            {
                Content = request.Turn.Input.Message,
                ToolCalls = []
            });

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return ChatStreamChunk.Final(new ChatCompletionResult
            {
                Content = request.Turn.Input.Message,
                ToolCalls = []
            });
        }
    }
}
