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
        var effectiveInput = input;
        try
        {
            var stage = await RunStageWithInputAsync(
                SharpClawActions.Chat.Turn,
                input,
                (replacedInput, ct) =>
                {
                    effectiveInput = replacedInput;
                    return RunCoreAsync(replacedInput, snapshot, ct);
                },
                snapshot.Actions,
                cancellationToken);
            return stage.Result;
        }
        catch (OperationCanceledException)
        {
            await TryDispatchTerminalAsync(
                new SharpClawActionKey("chat.turn.cancel"),
                effectiveInput,
                snapshot.Actions);
            throw;
        }
        catch (Exception)
        {
            await TryDispatchTerminalAsync(
                new SharpClawActionKey("chat.turn.fail"),
                effectiveInput,
                snapshot.Actions);
            throw;
        }
    }

    private async ValueTask<ChatTurnResult> RunCoreAsync(
        ChatTurnInput input,
        ChatPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var conversationStage = await RunStageWithInputAsync(
            SharpClawActions.Chat.ResolveConversation,
            input,
            (effectiveInput, ct) => _conversationResolver.ResolveAsync(effectiveInput, ct),
            snapshot.Actions,
            cancellationToken);
        var effectiveInput = conversationStage.Input;
        var conversation = conversationStage.Result;
        var turn = new ChatTurnContext(Guid.NewGuid(), effectiveInput, conversation);
        var profileStage = await RunStageWithInputAsync(
            SharpClawActions.Chat.ResolveProfile,
            turn,
            (effectiveTurn, ct) => _profileResolver.ResolveAsync(effectiveTurn, ct),
            snapshot.Actions,
            cancellationToken);
        turn = profileStage.Input;
        var profile = profileStage.Result;
        var historyStage = await RunStageWithInputAsync(
            SharpClawActions.Chat.LoadHistory,
            turn,
            (effectiveTurn, ct) => _conversationStore.LoadHistoryAsync(
                effectiveTurn.Conversation.ConversationId,
                ct),
            snapshot.Actions,
            cancellationToken);
        turn = historyStage.Input;
        var history = historyStage.Result;
        var contextStage = await RunStageWithInputAsync(
            SharpClawActions.Chat.AssembleContext,
            new ChatContextRequest(turn.Conversation.ConversationId, profile, history, turn),
            (effectiveRequest, ct) => _contextAssembler.BuildAsync(effectiveRequest, ct),
            snapshot.Actions,
            cancellationToken);
        var context = contextStage.Result;
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
        var providerStage = await RunStageWithInputAsync(
            SharpClawActions.Chat.ProviderRound,
            request,
            (effectiveRequest, ct) => _providerLoop.RunAsync(effectiveRequest, _toolPipeline, ct),
            snapshot.Actions,
            cancellationToken);
        request = providerStage.Input;
        turn = request.Turn;
        profile = request.Profile;
        context = request.Context;
        var completion = providerStage.Result;
        var exchange = new ChatExchange(turn, turn.Input.Message, completion);
        await RunStageAsync(
            SharpClawActions.Chat.CommitExchange,
            exchange,
            async (effectiveExchange, ct) =>
            {
                await _conversationStore.CommitExchangeAsync(effectiveExchange, ct);
                return true;
            },
            snapshot.Actions,
            cancellationToken);
        var result = new ChatTurnResult(
            turn.TurnId,
            turn.Conversation.ConversationId,
            completion,
            context.Features);
        return await RunStageAsync(
            new SharpClawActionKey("chat.turn.complete"),
            result,
            static (effectiveResult, _) => ValueTask.FromResult(effectiveResult),
            snapshot.Actions,
            cancellationToken);
    }

    private ValueTask<TResult> RunStageAsync<TInput, TResult>(
        SharpClawActionKey key,
        TInput input,
        Func<TInput, CancellationToken, ValueTask<TResult>> operation,
        ActionPipelineSnapshot snapshot,
        CancellationToken cancellationToken) =>
        RunStageResultAsync(key, input, operation, snapshot, cancellationToken);

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
        var stage = await RunStageWithInputAsync(key, input, operation, snapshot, cancellationToken);
        return stage.Result;
    }

    private async ValueTask<TResult> RunStageResultAsync<TInput, TResult>(
        SharpClawActionKey key,
        TInput input,
        Func<TInput, CancellationToken, ValueTask<TResult>> operation,
        ActionPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var stage = await RunStageWithInputAsync(key, input, operation, snapshot, cancellationToken);
        return stage.Result;
    }

    private async ValueTask<StageResult<TInput, TResult>> RunStageWithInputAsync<TInput, TResult>(
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
                var effectiveInput = envelope.Payload switch
                {
                    TInput typed => typed,
                    KernelActionEnvelope nested when nested.Payload is TInput typed => typed,
                    _ => throw new KernelActionExecutionException(
                        $"Action '{key.Value}' returned an invalid replacement input.")
                };
                var value = await operation(effectiveInput, ct);
                return (object)new StageResult<TInput, TResult>(effectiveInput, value);
            },
            snapshot,
            cancellationToken);
        return result is StageResult<TInput, TResult> stage
            ? stage
            : throw new KernelActionExecutionException(
                $"Action '{key.Value}' returned '{result?.GetType().FullName ?? "null"}' " +
                $"instead of a stage result for '{typeof(TResult).FullName}'.");
    }

    private sealed record StageResult<TInput, TResult>(TInput Input, TResult Result);

    private async ValueTask TryDispatchTerminalAsync<TInput>(
        SharpClawActionKey key,
        TInput input,
        ActionPipelineSnapshot snapshot)
    {
        if (!_graph.ContainsAction(key))
            return;
        try
        {
            var descriptor = _graph.GetStandardAction(key);
            await _dispatcher.RunAsync(
                descriptor,
                new KernelActionEnvelope(key, input),
                static (_, _) => ValueTask.FromResult<object>(true),
                snapshot,
                CancellationToken.None);
        }
        catch
        {
        }
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
