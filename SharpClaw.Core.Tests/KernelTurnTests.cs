using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelTurnTests
{
    [Fact]
    public async Task Direct_turn_runner_uses_one_snapshot_and_commits_one_exchange()
    {
        var graph = new KernelGraphBuilder().Compile();
        var dispatcher = new KernelActionDispatcher(graph);
        var store = new MemoryConversationStore();
        var runner = new DirectTurnRunner(
            graph,
            dispatcher,
            new ConversationResolver(),
            new ProfileResolver(),
            store,
            new EmptyContextAssembler(),
            new CompletionRoundLoop(),
            new EmptyToolPipeline());
        var input = new ChatTurnInput(
            "hello",
            null,
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            "test");

        var result = await runner.RunAsync(input, CancellationToken.None);

        Assert.Equal("done", result.Completion.Content);
        Assert.Equal(1, store.Commits);
        Assert.NotEqual(Guid.Empty, result.ConversationId);
        Assert.NotEqual(Guid.Empty, result.TurnId);
    }

    private sealed class ConversationResolver : IConversationResolver
    {
        public ValueTask<ConversationSelection> ResolveAsync(
            ChatTurnInput input,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ConversationSelection(Guid.NewGuid(), true));
    }

    private sealed class ProfileResolver : IChatProfileResolver
    {
        public ValueTask<ChatProfile> ResolveAsync(
            ChatTurnContext turn,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ChatProfile(
                "sample",
                Guid.NewGuid(),
                "provider",
                "system",
                null!));
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

    private sealed class EmptyContextAssembler : IChatContextAssembler
    {
        public ValueTask<ChatContextContribution> BuildAsync(
            ChatContextRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ChatContextContribution.Empty);
    }

    private sealed class CompletionRoundLoop : IProviderRoundLoop
    {
        public ValueTask<ChatCompletionResult> RunAsync(
            ProviderTurnRequest request,
            IUnifiedToolPipeline toolPipeline,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ChatCompletionResult
            {
                Content = "done",
                ToolCalls = Array.Empty<ChatToolCall>()
            });
    }

    private sealed class EmptyToolPipeline : IUnifiedToolPipeline
    {
        public ValueTask<ToolInvocationOutcome> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ToolInvocationOutcome.Rejected("unused", "unused"));
    }
}
