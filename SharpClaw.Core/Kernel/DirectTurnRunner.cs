using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Kernel;

public sealed class DirectTurnRunner
{
    private readonly KernelGraph _graph;
    private readonly KernelActionDispatcher _dispatcher;
    private readonly IConversationResolver _conversationResolver;
    private readonly IChatProfileResolver _profileResolver;
    private readonly IConversationStore _conversationStore;
    private readonly IChatContextAssembler _contextAssembler;
    private readonly IProviderRoundLoop _providerLoop;
    private readonly IUnifiedToolPipeline _toolPipeline;

    public DirectTurnRunner(
        KernelGraph graph,
        KernelActionDispatcher dispatcher,
        IConversationResolver conversationResolver,
        IChatProfileResolver profileResolver,
        IConversationStore conversationStore,
        IChatContextAssembler contextAssembler,
        IProviderRoundLoop providerLoop,
        IUnifiedToolPipeline toolPipeline)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _conversationResolver = conversationResolver ?? throw new ArgumentNullException(nameof(conversationResolver));
        _profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
        _conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        _contextAssembler = contextAssembler ?? throw new ArgumentNullException(nameof(contextAssembler));
        _providerLoop = providerLoop ?? throw new ArgumentNullException(nameof(providerLoop));
        _toolPipeline = toolPipeline ?? throw new ArgumentNullException(nameof(toolPipeline));
    }

    public async ValueTask<ChatTurnResult> RunAsync(
        ChatTurnInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var snapshot = _graph.ChatSnapshot;
        return await RunStageAsync(
            SharpClawActions.Chat.Turn,
            input,
            ct => RunCoreAsync(input, snapshot, ct),
            snapshot.Actions,
            cancellationToken);
    }

    private async ValueTask<ChatTurnResult> RunCoreAsync(
        ChatTurnInput input,
        ChatPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var conversation = await RunStageAsync(
            SharpClawActions.Chat.ResolveConversation,
            input,
            ct => _conversationResolver.ResolveAsync(input, ct),
            snapshot.Actions,
            cancellationToken);
        var turn = new ChatTurnContext(Guid.NewGuid(), input, conversation);
        var profile = await RunStageAsync(
            SharpClawActions.Chat.ResolveProfile,
            turn,
            ct => _profileResolver.ResolveAsync(turn, ct),
            snapshot.Actions,
            cancellationToken);
        var history = await RunStageAsync(
            SharpClawActions.Chat.LoadHistory,
            turn,
            ct => _conversationStore.LoadHistoryAsync(conversation.ConversationId, ct),
            snapshot.Actions,
            cancellationToken);
        var context = await RunStageAsync(
            SharpClawActions.Chat.AssembleContext,
            new ChatContextRequest(conversation.ConversationId, profile, history, turn),
            ct => _contextAssembler.BuildAsync(
                new ChatContextRequest(conversation.ConversationId, profile, history, turn),
                ct),
            snapshot.Actions,
            cancellationToken);
        var selectedTools = await RunInputStageAsync(
            SharpClawActions.Chat.SelectTools,
            snapshot.Tools,
            (tools, _) => ValueTask.FromResult<IReadOnlyList<ToolDescriptor>>(tools),
            snapshot.Actions,
            cancellationToken);
        var request = new ProviderTurnRequest(
            turn,
            profile,
            context,
            selectedTools);
        var completion = await RunStageAsync(
            SharpClawActions.Chat.ProviderRound,
            request,
            ct => _providerLoop.RunAsync(request, _toolPipeline, ct),
            snapshot.Actions,
            cancellationToken);
        var exchange = new ChatExchange(turn, input.Message, completion);
        await RunStageAsync(
            SharpClawActions.Chat.CommitExchange,
            exchange,
            async ct =>
            {
                await _conversationStore.CommitExchangeAsync(exchange, ct);
                return true;
            },
            snapshot.Actions,
            cancellationToken);
        return new ChatTurnResult(
            turn.TurnId,
            conversation.ConversationId,
            completion,
            context.Features);
    }

    private ValueTask<TResult> RunStageAsync<TInput, TResult>(
        SharpClawActionKey key,
        TInput input,
        Func<CancellationToken, ValueTask<TResult>> operation,
        ActionPipelineSnapshot snapshot,
        CancellationToken cancellationToken) =>
        RunStageCoreAsync(key, input, operation, snapshot, cancellationToken);

    private ValueTask<TResult> RunInputStageAsync<TInput, TResult>(
        SharpClawActionKey key,
        TInput input,
        Func<TInput, CancellationToken, ValueTask<TResult>> operation,
        ActionPipelineSnapshot snapshot,
        CancellationToken cancellationToken) =>
        RunInputStageCoreAsync(key, input, operation, snapshot, cancellationToken);

    private async ValueTask<TResult> RunInputStageCoreAsync<TInput, TResult>(
        SharpClawActionKey key,
        TInput input,
        Func<TInput, CancellationToken, ValueTask<TResult>> operation,
        ActionPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var descriptor = _graph.GetStandardAction(key);
        var result = await _dispatcher.RunRequiredAsync(
            descriptor,
            new KernelActionEnvelope(key, input),
            async (envelope, ct) =>
            {
                var effectiveInput = envelope.Payload is TInput typed ? typed : input;
                var value = await operation(effectiveInput, ct);
                return value is null
                    ? throw new KernelActionExecutionException($"Action '{key.Value}' returned no value.")
                    : (object)value;
            },
            snapshot,
            cancellationToken);
        return result is TResult typed
            ? typed
            : throw new KernelActionExecutionException(
                $"Action '{key.Value}' returned '{result?.GetType().FullName ?? "null"}' " +
                $"instead of '{typeof(TResult).FullName}'.");
    }

    private async ValueTask<TResult> RunStageCoreAsync<TInput, TResult>(
        SharpClawActionKey key,
        TInput input,
        Func<CancellationToken, ValueTask<TResult>> operation,
        ActionPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var descriptor = _graph.GetStandardAction(key);
        var result = await _dispatcher.RunRequiredAsync(
            descriptor,
            new KernelActionEnvelope(key, input),
            async (_, ct) =>
            {
                var value = await operation(ct);
                return value is null
                    ? throw new KernelActionExecutionException($"Action '{key.Value}' returned no value.")
                    : (object)value;
            },
            snapshot,
            cancellationToken);
        return result is TResult typed
            ? typed
            : throw new KernelActionExecutionException(
                $"Action '{key.Value}' returned '{result?.GetType().FullName ?? "null"}' " +
                $"instead of '{typeof(TResult).FullName}'.");
    }
}

public sealed class KernelChatContextAssembler : IChatContextAssembler
{
    private readonly IReadOnlyList<IChatContextContributor> _contributors;

    public KernelChatContextAssembler(IEnumerable<IChatContextContributor> contributors)
    {
        _contributors = contributors?.ToArray()
            ?? throw new ArgumentNullException(nameof(contributors));
    }

    public async ValueTask<ChatContextContribution> BuildAsync(
        ChatContextRequest request,
        CancellationToken cancellationToken)
    {
        var systemPromptSegments = new List<SystemPromptSegment>();
        var messages = new List<ChatCompletionMessage>(request.History);
        if (!string.IsNullOrWhiteSpace(request.Profile.SystemPrompt))
            systemPromptSegments.Add(new SystemPromptSegment("profile.system", request.Profile.SystemPrompt));
        var features = new List<ExtensionFeature>();
        foreach (var contributor in _contributors)
        {
            var contribution = await contributor.ContributeAsync(request, cancellationToken);
            systemPromptSegments.AddRange(contribution.SystemPromptSegments);
            messages.AddRange(contribution.Messages);
            features.AddRange(contribution.Features);
        }

        return new ChatContextContribution(systemPromptSegments, messages, features);
    }
}
