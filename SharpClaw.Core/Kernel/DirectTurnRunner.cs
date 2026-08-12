using System.Runtime.CompilerServices;
using System.Threading.Channels;
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
    private readonly KernelChatContextAssembler _contextAssembler;
    private readonly ProviderRoundLoop _providerLoop;
    private readonly UnifiedToolPipeline _toolPipeline;

    public DirectTurnRunner(
        KernelGraph graph,
        KernelActionDispatcher dispatcher,
        IConversationResolver conversationResolver,
        IChatProfileResolver profileResolver,
        IConversationStore conversationStore,
        KernelChatContextAssembler contextAssembler,
        ProviderRoundLoop providerLoop,
        UnifiedToolPipeline toolPipeline)
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
        catch (KernelActionCancelledException)
        {
            await TryDispatchTerminalAsync(
                new SharpClawActionKey("chat.turn.cancel"),
                effectiveInput,
                snapshot.Actions);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TryDispatchTerminalAsync(
                new SharpClawActionKey("chat.turn.cancel"),
                effectiveInput,
                snapshot.Actions);
            throw;
        }
        catch (KernelActionDeferredException)
        {
            throw;
        }
        catch (ActionOutcomeUncertainException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await TryDispatchTerminalAsync(
                new SharpClawActionKey("chat.turn.fail"),
                new KernelChatFailure(
                    exception is KernelActionFailedException failed
                        ? failed.Error.Code
                        : "CHAT_TURN_FAILED",
                    exception.Message),
                snapshot.Actions);
            throw;
        }
    }

    private async ValueTask<ChatTurnResult> RunCoreAsync(
        ChatTurnInput input,
        ChatPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var request = await PrepareTurnAsync(input, snapshot, cancellationToken);
        var providerStage = await RunStageWithInputAsync(
            SharpClawActions.Chat.ProviderRound,
            request,
            async (effectiveRequest, ct) =>
            {
                var value = await _providerLoop.RunAsync(effectiveRequest, _toolPipeline, ct);
                return await RunStageAsync(
                    new SharpClawActionKey("chat.provider_round.complete"),
                    value,
                    static (completion, _) => ValueTask.FromResult(completion),
                    snapshot.Actions,
                    ct);
            },
            snapshot.Actions,
            cancellationToken);
        return await CompleteTurnAsync(
            providerStage.Input,
            providerStage.Result,
            snapshot,
            cancellationToken);
    }

    /// <summary>
    /// Streams one direct turn through the same action, provider, tool, and
    /// conversation pipeline as <see cref="RunAsync"/>.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
        ChatTurnInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var snapshot = _graph.ChatSnapshot;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<ChatStreamChunk>(
            new BoundedChannelOptions(16)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        var operation = RunStreamingOperationAsync(
            input,
            snapshot,
            channel.Writer,
            linkedCancellation.Token);
        var consumedToCompletion = false;
        try
        {
            while (await channel.Reader.WaitToReadAsync(linkedCancellation.Token))
            {
                while (channel.Reader.TryRead(out var chunk))
                    yield return chunk;
            }

            await operation;
            consumedToCompletion = true;
        }
        finally
        {
            if (!consumedToCompletion)
                linkedCancellation.Cancel();

            Exception? cleanupFailure = null;
            try
            {
                await operation;
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            channel.Writer.TryComplete();
            if (!consumedToCompletion && cleanupFailure is not null &&
                cleanupFailure is not OperationCanceledException)
                throw cleanupFailure;
        }
    }

    private async Task RunStreamingOperationAsync(
        ChatTurnInput input,
        ChatPipelineSnapshot snapshot,
        ChannelWriter<ChatStreamChunk> writer,
        CancellationToken cancellationToken)
    {
        var effectiveInput = input;
        var turnCompleted = false;
        try
        {
            var stage = await RunStageWithInputAsync(
                SharpClawActions.Chat.Turn,
                input,
                (replacedInput, ct) =>
                {
                    effectiveInput = replacedInput;
                    return RunStreamingCoreAsync(
                        replacedInput,
                        snapshot,
                        writer,
                        ct);
                },
                snapshot.Actions,
                cancellationToken);
            turnCompleted = true;
            try
            {
                await writer.WriteAsync(
                    ChatStreamChunk.Final(stage.Result.Completion),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (turnCompleted)
            {
            }
            writer.TryComplete();
        }
        catch (KernelActionCancelledException)
        {
            await TryDispatchTerminalAsync(
                new SharpClawActionKey("chat.turn.cancel"),
                effectiveInput,
                snapshot.Actions);
            writer.TryComplete();
            throw;
        }
        catch (OperationCanceledException)
        {
            if (!turnCompleted)
            {
                await TryDispatchTerminalAsync(
                    new SharpClawActionKey("chat.turn.cancel"),
                    effectiveInput,
                    snapshot.Actions);
            }
            writer.TryComplete();
            throw;
        }
        catch (KernelActionDeferredException)
        {
            writer.TryComplete();
            throw;
        }
        catch (ActionOutcomeUncertainException)
        {
            writer.TryComplete();
            throw;
        }
        catch (Exception exception)
        {
            if (!turnCompleted)
            {
                await TryDispatchTerminalAsync(
                    new SharpClawActionKey("chat.turn.fail"),
                    new KernelChatFailure(
                        exception is KernelActionFailedException failed
                            ? failed.Error.Code
                            : "CHAT_TURN_FAILED",
                        exception.Message),
                    snapshot.Actions);
            }
            writer.TryComplete();
            throw;
        }
    }

    private async ValueTask<ChatTurnResult> RunStreamingCoreAsync(
        ChatTurnInput input,
        ChatPipelineSnapshot snapshot,
        ChannelWriter<ChatStreamChunk> writer,
        CancellationToken cancellationToken)
    {
        var request = await PrepareTurnAsync(input, snapshot, cancellationToken);
        var providerStage = await RunStageWithInputAsync(
            SharpClawActions.Chat.ProviderRound,
            request,
            (effectiveRequest, ct) => RunStreamingProviderAsync(
                effectiveRequest,
                snapshot,
                writer,
                ct),
            snapshot.Actions,
            cancellationToken);
        return await CompleteTurnAsync(
            providerStage.Input,
            providerStage.Result,
            snapshot,
            cancellationToken);
    }

    private async ValueTask<ChatCompletionResult> RunStreamingProviderAsync(
        ProviderTurnRequest request,
        ChatPipelineSnapshot snapshot,
        ChannelWriter<ChatStreamChunk> writer,
        CancellationToken cancellationToken)
    {
        ChatCompletionResult? completion = null;
        await foreach (var chunk in _providerLoop.StreamAsync(
            request,
            _toolPipeline,
            cancellationToken))
        {
            if (chunk.IsFinished)
            {
                completion = chunk.Finished;
                continue;
            }

            await writer.WriteAsync(chunk, cancellationToken);
        }

        if (completion is null || string.Equals(
                completion.Refusal,
                "The provider stream ended without a completion.",
                StringComparison.Ordinal))
            throw new KernelActionExecutionException(
                "The provider stream ended without a completion.");

        return await RunStageAsync(
            new SharpClawActionKey("chat.provider_round.complete"),
            completion,
            static (value, _) => ValueTask.FromResult(value),
            snapshot.Actions,
            cancellationToken);
    }

    private async ValueTask<ProviderTurnRequest> PrepareTurnAsync(
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
        var turn = new ChatTurnContext(
            Guid.NewGuid(),
            effectiveInput,
            conversationStage.Result);
        var userMessage = await RunStageAsync(
            new SharpClawActionKey("chat.user_message.prepare"),
            new KernelChatUserMessage(turn, effectiveInput.Message),
            static (value, _) => ValueTask.FromResult(value),
            snapshot.Actions,
            cancellationToken);
        await RunStageAsync(
            new SharpClawActionKey("chat.user_message.commit"),
            userMessage,
            static (value, _) => ValueTask.FromResult(value),
            snapshot.Actions,
            cancellationToken);
        var profileStage = await RunStageWithInputAsync(
            SharpClawActions.Chat.ResolveProfile,
            turn,
            (effectiveTurn, ct) => _profileResolver.ResolveAsync(effectiveTurn, ct),
            snapshot.Actions,
            cancellationToken);
        turn = profileStage.Input;
        var historyStage = await RunStageWithInputAsync(
            SharpClawActions.Chat.LoadHistory,
            turn,
            (effectiveTurn, ct) => RunStageAsync(
                new SharpClawActionKey("conversation.history.query"),
                effectiveTurn.Conversation.ConversationId,
                (conversationId, queryCt) => _conversationStore.LoadHistoryAsync(conversationId, queryCt),
                snapshot.Actions,
                ct),
            snapshot.Actions,
            cancellationToken);
        turn = historyStage.Input;
        var context = await _contextAssembler.BuildAsync(
            new ChatContextRequest(
                turn.Conversation.ConversationId,
                profileStage.Result,
                historyStage.Result,
                turn),
            cancellationToken);
        var collectedTools = await RunInputStageAsync(
            new SharpClawActionKey("chat.tools.collect"),
            snapshot.Tools,
            (tools, _) => ValueTask.FromResult<IReadOnlyList<ToolDescriptor>>(tools),
            snapshot.Actions,
            cancellationToken);
        var selectedTools = await RunInputStageAsync(
            SharpClawActions.Chat.SelectTools,
            collectedTools,
            (tools, _) => ValueTask.FromResult<IReadOnlyList<ToolDescriptor>>(tools),
            snapshot.Actions,
            cancellationToken);
        return new ProviderTurnRequest(
            turn,
            profileStage.Result,
            context,
            selectedTools);
    }

    private async ValueTask<ChatTurnResult> CompleteTurnAsync(
        ProviderTurnRequest request,
        ChatCompletionResult completion,
        ChatPipelineSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var exchange = await RunStageAsync(
            new SharpClawActionKey("chat.assistant_message.prepare"),
            new ChatExchange(request.Turn, request.Turn.Input.Message, completion),
            static (value, _) => ValueTask.FromResult(value),
            snapshot.Actions,
            cancellationToken);
        exchange = await RunStageAsync(
            new SharpClawActionKey("conversation.message.prepare"),
            exchange,
            static (value, _) => ValueTask.FromResult(value),
            snapshot.Actions,
            cancellationToken);
        await RunStageAsync(
            SharpClawActions.Chat.CommitExchange,
            exchange,
            async (effectiveExchange, ct) =>
            {
                await RunStageAsync(
                    new SharpClawActionKey("conversation.message.commit"),
                    effectiveExchange,
                    async (value, commitCt) =>
                    {
                        await _conversationStore.CommitExchangeAsync(value, commitCt);
                        return true;
                    },
                    snapshot.Actions,
                    ct);
                return true;
            },
            snapshot.Actions,
            cancellationToken);
        var result = new ChatTurnResult(
            request.Turn.TurnId,
            request.Turn.Conversation.ConversationId,
            completion,
            request.Context.Features);
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
        var effectiveInput = input;
        var result = await _dispatcher.RunRequiredAsync(
            descriptor,
            new KernelActionEnvelope(key, input),
            async (envelope, ct) =>
            {
                effectiveInput = envelope.Payload switch
                {
                    TInput typed => typed,
                    KernelActionEnvelope nested when nested.Payload is TInput typed => typed,
                    _ => throw new KernelActionExecutionException(
                        $"Action '{key.Value}' returned an invalid replacement input.")
                };
                return (object)(await operation(effectiveInput, ct))!;
            },
            snapshot,
            cancellationToken);
        return result is TResult value
            ? new StageResult<TInput, TResult>(effectiveInput, value)
            : throw new KernelActionExecutionException(
                $"Action '{key.Value}' returned '{result?.GetType().FullName ?? "null"}' " +
                $"instead of '{typeof(TResult).FullName}'.");
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
    private static readonly SharpClawActionKey AssembleStart = new("chat.context.assemble.start");
    private static readonly SharpClawActionKey ContributorInvoke = new("chat.context.contributor.invoke");
    private static readonly SharpClawActionKey AssembleComplete = new("chat.context.assemble.complete");
    private readonly KernelGraph _graph;
    private readonly KernelActionDispatcher _dispatcher;
    private readonly IReadOnlyList<IChatContextContributor> _contributors;

    public KernelChatContextAssembler(
        KernelGraph graph,
        KernelActionDispatcher dispatcher,
        IEnumerable<IChatContextContributor> contributors)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _contributors = contributors?.ToArray()
            ?? throw new ArgumentNullException(nameof(contributors));
    }

    public async ValueTask<ChatContextContribution> BuildAsync(
        ChatContextRequest request,
        CancellationToken cancellationToken)
    {
        var descriptor = _graph.GetStandardAction(AssembleStart);
        var result = await _dispatcher.RunRequiredAsync(
            descriptor,
            new KernelActionEnvelope(AssembleStart, request),
            async (envelope, ct) => (object)await BuildCoreAsync(
                ExtractContextRequest(envelope),
                ct),
            _graph.ActionSnapshot,
            cancellationToken);
        return result as ChatContextContribution
            ?? throw new KernelActionExecutionException(
                "The context assembly action returned an invalid contribution.");
    }

    private async ValueTask<ChatContextContribution> BuildCoreAsync(
        ChatContextRequest request,
        CancellationToken cancellationToken)
    {
        var systemPromptSegments = new List<SystemPromptSegment>();
        var messages = new List<ChatCompletionMessage>(request.History);
        if (!string.IsNullOrWhiteSpace(request.Profile.SystemPrompt))
            systemPromptSegments.Add(new SystemPromptSegment("profile.system", request.Profile.SystemPrompt));
        var features = new List<ExtensionFeature>();
        for (var index = 0; index < _contributors.Count; index++)
        {
            var contributor = _contributors[index];
            var invocation = new KernelChatContributorInvocation(
                request,
                index,
                contributor.GetType().AssemblyQualifiedName ?? contributor.GetType().FullName ?? contributor.GetType().Name);
            var descriptor = _graph.GetStandardAction(ContributorInvoke);
            var result = await _dispatcher.RunRequiredAsync(
                descriptor,
                new KernelActionEnvelope(ContributorInvoke, invocation),
                async (envelope, ct) =>
                {
                    var effective = ExtractContributorInvocation(envelope);
                    if (effective.ContributorIndex != index ||
                        !string.Equals(effective.ContributorType, invocation.ContributorType, StringComparison.Ordinal))
                        throw new KernelActionExecutionException(
                            "A context contributor replacement cannot select a different contributor.");
                    return (object)await contributor.ContributeAsync(effective.Request, ct);
                },
                _graph.ActionSnapshot,
                cancellationToken);
            var contribution = result as ChatContextContribution
                ?? throw new KernelActionExecutionException(
                    "The context contributor action returned an invalid contribution.");
            systemPromptSegments.AddRange(contribution.SystemPromptSegments);
            messages.AddRange(contribution.Messages);
            features.AddRange(contribution.Features);
        }

        var assembled = new ChatContextContribution(systemPromptSegments, messages, features);
        var completeDescriptor = _graph.GetStandardAction(AssembleComplete);
        var completed = await _dispatcher.RunRequiredAsync(
            completeDescriptor,
            new KernelActionEnvelope(AssembleComplete, assembled),
            static (envelope, _) => ValueTask.FromResult<object>(
                envelope.Payload is ChatContextContribution value
                    ? value
                    : throw new KernelActionExecutionException(
                        "The context completion action received an invalid contribution.")),
            _graph.ActionSnapshot,
            cancellationToken);
        return completed as ChatContextContribution
            ?? throw new KernelActionExecutionException(
                "The context completion action returned an invalid contribution.");
    }

    private static ChatContextRequest ExtractContextRequest(KernelActionEnvelope envelope) =>
        envelope.Payload switch
        {
            ChatContextRequest request => request,
            KernelActionEnvelope nested when nested.Payload is ChatContextRequest request => request,
            _ => throw new KernelActionExecutionException(
                "The context assembly action received an invalid request.")
        };

    private static KernelChatContributorInvocation ExtractContributorInvocation(KernelActionEnvelope envelope) =>
        envelope.Payload switch
        {
            KernelChatContributorInvocation invocation => invocation,
            KernelActionEnvelope nested when nested.Payload is KernelChatContributorInvocation invocation => invocation,
            _ => throw new KernelActionExecutionException(
                "The context contributor action received an invalid invocation.")
        };
}

public sealed record KernelChatUserMessage(ChatTurnContext Turn, string Message);

public sealed record KernelChatContributorInvocation(
    ChatContextRequest Request,
    int ContributorIndex,
    string ContributorType);

public sealed record KernelChatFailure(string Code, string Message);
