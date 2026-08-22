using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelTurnTests
{
    [Fact]
    public async Task Direct_turn_runner_uses_one_snapshot_and_commits_one_exchange()
    {
        TurnRecordingInterceptor.Keys.Clear();
        var builder = new KernelGraphBuilder();
        builder.Hooks.For(SharpClawActions.Chat.Turn).Use<TurnRecordingInterceptor>(Order("turn"));
        builder.Hooks.For(SharpClawActions.Chat.SelectTools).Use<TurnRecordingInterceptor>(Order("tools"));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var store = new MemoryConversationStore();
        var conversationResolver = new ConversationResolver();
        var profileResolver = new ProfileResolver();
        var runner = new DirectTurnRunner(
            graph,
            dispatcher,
            conversationResolver,
            profileResolver,
            store,
            new KernelChatContextAssembler(graph, dispatcher, []),
            new ProviderRoundLoop(
                new CompletionTransport(),
                graph,
                dispatcher,
                KernelTestExecution.CreateToolContextIssuer()),
            new UnifiedToolPipeline(graph, dispatcher));
        var input = new ChatTurnInput(
            "hello",
            null,
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            "test");

        var result = await runner.RunAsync(input, CancellationToken.None);

        Assert.Equal("done", result.Completion.Content);
        Assert.Equal(1, store.Commits);
        Assert.Equal(conversationResolver.ConversationId, result.ConversationId);
        Assert.Equal(profileResolver.TurnId, result.TurnId);
        Assert.Contains(SharpClawActions.Chat.Turn.Value, TurnRecordingInterceptor.Keys);
        Assert.Contains(SharpClawActions.Chat.SelectTools.Value, TurnRecordingInterceptor.Keys);
    }

    private static HookOrdering Order(string id) =>
        new(id, HookPriority.Normal, [], [], null, HookFailurePolicy.FailAction);

    private sealed class TurnRecordingInterceptor : IActionInterceptor<KernelActionEnvelope, object>
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

    private sealed class ConversationResolver : IConversationResolver
    {
        public Guid ConversationId { get; } = Guid.NewGuid();

        public ValueTask<ConversationSelection> ResolveAsync(
            ChatTurnInput input,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ConversationSelection(ConversationId, true));
    }

    private sealed class ProfileResolver : IChatProfileResolver
    {
        public Guid TurnId { get; private set; }

        public ValueTask<ChatProfile> ResolveAsync(
            ChatTurnContext turn,
            CancellationToken cancellationToken) =>
            Resolve(turn);

        private ValueTask<ChatProfile> Resolve(ChatTurnContext turn)
        {
            TurnId = turn.TurnId;
            return ValueTask.FromResult(new ChatProfile(
                "sample",
                Guid.NewGuid(),
                "provider",
                "system",
                null));
        }
    }

    private sealed class MemoryConversationStore : IConversationStore
    {
        public int Commits { get; private set; }

        public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
            Guid conversationId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ChatCompletionMessage>>(Array.Empty<ChatCompletionMessage>());

        public ValueTask CommitExchangeAsync(ChatExchange exchange, CancellationToken cancellationToken)
        {
            Commits++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompletionTransport : IKernelProviderTransport
    {
        public ValueTask<ChatCompletionResult> CompleteAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ChatCompletionResult
            {
                Content = "done",
                ToolCalls = Array.Empty<ChatToolCall>()
            });

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
            ProviderTurnRequest request,
            IReadOnlyList<ToolAwareMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return ChatStreamChunk.Final(new ChatCompletionResult
            {
                Content = "done",
                ToolCalls = Array.Empty<ChatToolCall>()
            });
        }
    }
}
