using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Kernel;

public sealed record KernelProviderRequestEnvelope(
    ProviderTurnRequest Request,
    IReadOnlyList<ToolAwareMessage> Messages,
    string? SerializedPayload = null);

public sealed record KernelProviderCompletionEnvelope(
    KernelProviderRequestEnvelope Request,
    ChatCompletionResult Completion);

public sealed record KernelProviderFailure(
    string Code,
    string Message,
    bool IsCancellation = false);

public sealed class ProviderRoundLoop : IProviderRoundLoop
{
    private static readonly SharpClawActionKey ClientCreate = new("provider.client.create");
    private static readonly SharpClawActionKey RequestPrepare = new("provider.request.prepare");
    private static readonly SharpClawActionKey RequestSerialize = new("provider.request.serialize");
    private static readonly SharpClawActionKey RequestSerializeAfter = new("provider.request.serialize.after");
    private static readonly SharpClawActionKey StreamOpen = new("provider.stream.open");
    private static readonly SharpClawActionKey StreamClose = new("provider.stream.close");
    private static readonly SharpClawActionKey ResponseDeserialize = new("provider.response.deserialize");
    private static readonly SharpClawActionKey RequestFailure = new("provider.request.fail");
    private static readonly SharpClawActionKey RequestCancellation = new("provider.request.cancel");

    private readonly IKernelProviderTransport _transport;
    private readonly KernelGraph _graph;
    private readonly KernelActionDispatcher _dispatcher;
    private readonly int _maximumRounds;

    public ProviderRoundLoop(
        IKernelProviderTransport transport,
        KernelGraph graph,
        KernelActionDispatcher dispatcher,
        int maximumRounds = 8)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        if (maximumRounds < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumRounds));
        _maximumRounds = maximumRounds;
    }

    public async ValueTask<ChatCompletionResult> RunAsync(
        ProviderTurnRequest request,
        IUnifiedToolPipeline toolPipeline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolPipeline);
        var messages = BuildMessages(request);

        try
        {
            for (var round = 0; round < _maximumRounds; round++)
            {
                var completion = await RunCompletionTransportAsync(
                    request,
                    messages,
                    cancellationToken);
                if (!completion.HasToolCalls || completion.ToolCalls.Count == 0)
                    return completion;

                messages.Add(ToolAwareMessage.AssistantWithToolCalls(
                    completion.ToolCalls,
                    completion.Content,
                    completion.ProviderMetadataJson));
                foreach (var call in completion.ToolCalls)
                {
                    var invocation = CreateInvocation(request, call);
                    var outcome = await toolPipeline.InvokeAsync(invocation, cancellationToken);
                    if (outcome.Kind != ActionOutcomeKind.Completed)
                        ThrowToolOutcome(outcome);

                    messages.Add(ToolAwareMessage.ToolResult(call.Id, outcome.Result?.Content ?? string.Empty));
                }
            }

            return new ChatCompletionResult
            {
                Content = string.Empty,
                Refusal = "The provider round limit was reached.",
                ToolCalls = Array.Empty<ChatToolCall>()
            };
        }
        catch (KernelActionCancelledException exception)
        {
            await TryDispatchFailureAsync(RequestCancellation, new KernelProviderFailure(
                exception.Error.Code,
                exception.Error.Message,
                true));
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
        catch (KernelActionFailedException exception)
        {
            await TryDispatchFailureAsync(RequestFailure, new KernelProviderFailure(
                exception.Error.Code,
                exception.Error.Message));
            throw;
        }
        catch (OperationCanceledException)
        {
            await TryDispatchFailureAsync(RequestCancellation, new KernelProviderFailure(
                "PROVIDER_REQUEST_CANCELLED",
                "The provider request was cancelled.",
                true));
            throw;
        }
        catch (Exception exception)
        {
            await TryDispatchFailureAsync(RequestFailure, new KernelProviderFailure(
                "PROVIDER_REQUEST_FAILED",
                exception.Message));
            throw;
        }
    }

    public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
        ProviderTurnRequest request,
        IUnifiedToolPipeline toolPipeline,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolPipeline);
        var messages = BuildMessages(request);
        var completedNormally = false;
        var failureDispatched = false;

        try
        {
            for (var round = 0; round < _maximumRounds; round++)
            {
                KernelProviderRequestEnvelope state;
                IAsyncEnumerable<ChatStreamChunk> stream;
                try
                {
                    state = await PrepareRequestAsync(request, messages, cancellationToken);
                    state = await DispatchInputAsync(StreamOpen, state, static (value, _) =>
                        ValueTask.FromResult(value), cancellationToken);
                    var streamHandle = await DispatchInputAsync(
                        SharpClawActions.Provider.Send,
                        state,
                        (value, ct) => ValueTask.FromResult(
                            KernelProviderTransportResult.Streaming(
                                _transport.StreamAsync(value.Request, value.Messages, ct))),
                        cancellationToken);
                    stream = streamHandle.IsStreaming && streamHandle.Stream is not null
                        ? streamHandle.Stream
                        : throw new KernelActionExecutionException(
                            "The provider send action returned no stream transport.");
                }
                catch (KernelActionCancelledException exception)
                {
                    failureDispatched = true;
                    await TryDispatchFailureAsync(RequestCancellation, new KernelProviderFailure(
                        exception.Error.Code,
                        exception.Error.Message,
                        true));
                    throw;
                }
                catch (KernelActionDeferredException)
                {
                    failureDispatched = true;
                    throw;
                }
                catch (ActionOutcomeUncertainException)
                {
                    failureDispatched = true;
                    throw;
                }
                catch (KernelActionFailedException exception)
                {
                    failureDispatched = true;
                    await TryDispatchFailureAsync(RequestFailure, new KernelProviderFailure(
                        exception.Error.Code,
                        exception.Error.Message));
                    throw;
                }

                ChatCompletionResult? completion = null;
                ChatStreamChunk? finalChunk = null;
                await foreach (var rawChunk in stream.WithCancellation(cancellationToken))
                {
                    var received = await DispatchStreamChunkAsync(
                        new SharpClawActionKey("provider.stream.chunk.receive"),
                        rawChunk,
                        "receive",
                        () => failureDispatched = true,
                        cancellationToken);
                    if (received.Status == ChunkActionStatus.Suppressed)
                        continue;

                    foreach (var receivedChunk in received.Chunks)
                    {
                        var transformed = await DispatchStreamChunkAsync(
                            new SharpClawActionKey("provider.stream.chunk.transform"),
                            receivedChunk,
                            "transform",
                            () => failureDispatched = true,
                            cancellationToken);
                        if (transformed.Status == ChunkActionStatus.Suppressed)
                            continue;

                        foreach (var candidate in transformed.Chunks)
                        {
                            var sent = await DispatchStreamChunkAsync(
                                new SharpClawActionKey("provider.stream.chunk.send"),
                                candidate,
                                "send",
                                () => failureDispatched = true,
                                cancellationToken);
                            if (sent.Status == ChunkActionStatus.Emitted)
                            {
                                foreach (var emitted in sent.Chunks)
                                {
                                    if (emitted.IsFinished)
                                    {
                                        finalChunk = emitted;
                                        break;
                                    }

                                    yield return emitted;
                                }
                            }

                            if (finalChunk is not null)
                                break;
                        }

                        if (finalChunk is not null)
                            break;
                    }

                    if (finalChunk is not null)
                        break;
                }

                if (finalChunk is null)
                {
                    failureDispatched = true;
                    await TryDispatchFailureAsync(RequestFailure, new KernelProviderFailure(
                        "PROVIDER_STREAM_NO_COMPLETION",
                        "The provider stream ended without a completion."));
                    yield return ChatStreamChunk.Final(new ChatCompletionResult
                    {
                        Refusal = "The provider stream ended without a completion.",
                        ToolCalls = Array.Empty<ChatToolCall>()
                    });
                    yield break;
                }

                var streamClosed = false;
                try
                {
                    await DispatchInputAsync(
                        StreamClose,
                        state,
                        static (_, _) => ValueTask.FromResult(true),
                        cancellationToken);
                    streamClosed = true;
                }
                catch (KernelActionCancelledException exception)
                {
                    failureDispatched = true;
                    await TryDispatchFailureAsync(RequestCancellation, new KernelProviderFailure(
                        exception.Error.Code,
                        exception.Error.Message,
                        true));
                    throw;
                }
                catch (KernelActionDeferredException)
                {
                    failureDispatched = true;
                    throw;
                }
                catch (ActionOutcomeUncertainException)
                {
                    failureDispatched = true;
                    throw;
                }
                catch (KernelActionFailedException exception)
                {
                    failureDispatched = true;
                    await TryDispatchFailureAsync(RequestFailure, new KernelProviderFailure(
                        exception.Error.Code,
                        exception.Error.Message));
                    throw;
                }
                catch
                {
                }
                if (!streamClosed)
                {
                    failureDispatched = true;
                    await TryDispatchFailureAsync(RequestFailure, new KernelProviderFailure(
                        "PROVIDER_STREAM_CLOSE_FAILED",
                        "The provider stream close action failed."));
                    yield break;
                }
                try
                {
                    var response = await DispatchInputAsync(
                        ResponseDeserialize,
                        new KernelProviderCompletionEnvelope(state, finalChunk.Finished!),
                        static (value, _) => ValueTask.FromResult(value.Completion),
                        cancellationToken);
                    completion = await DispatchInputAsync(
                        SharpClawActions.Provider.AfterTransport,
                        response,
                        static (value, _) => ValueTask.FromResult(value),
                        cancellationToken);
                }
                catch (KernelActionCancelledException exception)
                {
                    failureDispatched = true;
                    await TryDispatchFailureAsync(RequestCancellation, new KernelProviderFailure(
                        exception.Error.Code,
                        exception.Error.Message,
                        true));
                    throw;
                }
                catch (KernelActionDeferredException)
                {
                    failureDispatched = true;
                    throw;
                }
                catch (ActionOutcomeUncertainException)
                {
                    failureDispatched = true;
                    throw;
                }
                catch (KernelActionFailedException exception)
                {
                    failureDispatched = true;
                    await TryDispatchFailureAsync(RequestFailure, new KernelProviderFailure(
                        exception.Error.Code,
                        exception.Error.Message));
                    throw;
                }
                if (!(finalChunk.IsFinished && finalChunk.Finished?.HasToolCalls == true))
                    yield return ChatStreamChunk.Final(completion);
                if (!completion.HasToolCalls || completion.ToolCalls.Count == 0)
                {
                    completedNormally = true;
                    yield break;
                }

                messages.Add(ToolAwareMessage.AssistantWithToolCalls(
                    completion.ToolCalls,
                    completion.Content,
                    completion.ProviderMetadataJson));
                foreach (var call in completion.ToolCalls)
                {
                    var outcome = await toolPipeline.InvokeAsync(CreateInvocation(request, call), cancellationToken);
                    if (outcome.Kind != ActionOutcomeKind.Completed)
                        await ThrowStreamToolOutcomeAsync(outcome, () => failureDispatched = true);

                    messages.Add(ToolAwareMessage.ToolResult(call.Id, outcome.Result?.Content ?? string.Empty));
                }
            }

            failureDispatched = true;
            await TryDispatchFailureAsync(RequestFailure, new KernelProviderFailure(
                "PROVIDER_ROUND_LIMIT",
                "The provider round limit was reached."));
            yield return ChatStreamChunk.Final(new ChatCompletionResult
            {
                Refusal = "The provider round limit was reached.",
                ToolCalls = Array.Empty<ChatToolCall>()
            });
        }
        finally
        {
            if (!completedNormally && !failureDispatched)
            {
                await TryDispatchFailureAsync(
                    cancellationToken.IsCancellationRequested ? RequestCancellation : RequestFailure,
                    new KernelProviderFailure(
                        cancellationToken.IsCancellationRequested
                            ? "PROVIDER_STREAM_CANCELLED"
                            : "PROVIDER_STREAM_FAILED",
                        cancellationToken.IsCancellationRequested
                            ? "The provider stream was cancelled."
                            : "The provider stream failed.",
                        cancellationToken.IsCancellationRequested));
            }
        }
    }

    private async ValueTask<ChatCompletionResult> RunCompletionTransportAsync(
        ProviderTurnRequest request,
        IReadOnlyList<ToolAwareMessage> messages,
        CancellationToken cancellationToken)
    {
        var state = await PrepareRequestAsync(request, messages, cancellationToken);
        var transportResult = await DispatchInputAsync(
            SharpClawActions.Provider.Send,
            state,
            async (value, ct) => KernelProviderTransportResult.Buffered(
                await _transport.CompleteAsync(value.Request, value.Messages, ct)),
            cancellationToken);
        var raw = !transportResult.IsStreaming && transportResult.Completion is not null
            ? transportResult.Completion
            : throw new KernelActionExecutionException(
                "The provider send action returned no buffered completion.");
        var deserialized = await DispatchInputAsync(
            ResponseDeserialize,
            new KernelProviderCompletionEnvelope(state, raw),
            static (value, _) => ValueTask.FromResult(value.Completion),
            cancellationToken);
        return await DispatchInputAsync(
            SharpClawActions.Provider.AfterTransport,
            deserialized,
            static (value, _) => ValueTask.FromResult(value),
            cancellationToken);
    }

    private async ValueTask<KernelProviderRequestEnvelope> PrepareRequestAsync(
        ProviderTurnRequest request,
        IReadOnlyList<ToolAwareMessage> messages,
        CancellationToken cancellationToken)
    {
        var state = new KernelProviderRequestEnvelope(request, messages);
        state = await DispatchInputAsync(
            SharpClawActions.Provider.Resolve,
            state,
            static (value, _) => ValueTask.FromResult(value),
            cancellationToken);
        state = await DispatchInputAsync(
            ClientCreate,
            state,
            static (value, _) => ValueTask.FromResult(value),
            cancellationToken);
        state = await DispatchInputAsync(
            RequestPrepare,
            state,
            static (value, _) => ValueTask.FromResult(value),
            cancellationToken);
        state = await DispatchInputAsync(
            RequestSerialize,
            state,
            static (value, _) => ValueTask.FromResult(
                value with { SerializedPayload = SerializeRequest(value) }),
            cancellationToken);
        return await DispatchInputAsync(
            RequestSerializeAfter,
            state,
            static (value, _) => ValueTask.FromResult(value),
            cancellationToken);
    }

    private async ValueTask<TResult> DispatchInputAsync<TInput, TResult>(
        SharpClawActionKey key,
        TInput input,
        Func<TInput, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken)
    {
        var descriptor = _graph.GetStandardAction(key);
        var result = await _dispatcher.RunRequiredAsync(
            descriptor,
            new KernelActionEnvelope(key, input),
            async (envelope, ct) => (object)(await terminal(ExtractInput(envelope, input), ct))!,
            _graph.ActionSnapshot,
            cancellationToken);
        return result is TResult typed
            ? typed
            : throw new KernelActionExecutionException(
                $"Provider action '{key.Value}' returned an invalid result.");
    }

    private async ValueTask<ChunkActionResult> DispatchChunkAsync(
        SharpClawActionKey key,
        ChatStreamChunk chunk,
        CancellationToken cancellationToken)
    {
        var descriptor = _graph.GetStandardAction(key);
        var outcome = await _dispatcher.RunAsync(
            descriptor,
            new KernelActionEnvelope(key, chunk),
            static (envelope, _) => envelope.Payload is ChatStreamChunk value
                ? ValueTask.FromResult<object>(new KernelProviderChunkResult([value], false))
                : throw new KernelActionExecutionException(
                    "The provider stream action received an invalid chunk."),
            _graph.ActionSnapshot,
            cancellationToken);
        if (outcome.Kind == ActionOutcomeKind.Cancelled)
            throw new KernelActionCancelledException(
                outcome.Error ?? new ExecutionError(
                    "PROVIDER_STREAM_CANCELLED",
                    "The provider stream action was cancelled."));
        if (outcome.Kind == ActionOutcomeKind.Deferred && outcome.Continuation is not null)
            throw new KernelActionDeferredException(outcome.Continuation);
        if (outcome.Kind == ActionOutcomeKind.Uncertain && outcome.Uncertainty is not null)
            throw new ActionOutcomeUncertainException(outcome.Uncertainty);
        if (outcome.Kind == ActionOutcomeKind.Failed)
            throw new KernelActionFailedException(
                outcome.Error ?? new ExecutionError(
                    "PROVIDER_STREAM_ACTION_FAILED",
                    $"Stream action '{key.Value}' failed."));
        if (outcome.Result is not KernelProviderChunkResult result)
            throw new KernelActionExecutionException(
                $"Stream action '{key.Value}' returned an invalid chunk result.");
        return result.Suppressed || result.Chunks.Count == 0
            ? new ChunkActionResult(ChunkActionStatus.Suppressed, [])
            : new ChunkActionResult(ChunkActionStatus.Emitted, result.Chunks);
    }

    private async ValueTask<ChunkActionResult> DispatchStreamChunkAsync(
        SharpClawActionKey key,
        ChatStreamChunk chunk,
        string phase,
        Action markFailureHandled,
        CancellationToken cancellationToken)
    {
        try
        {
            return await DispatchChunkAsync(key, chunk, cancellationToken);
        }
        catch (KernelActionCancelledException exception)
        {
            markFailureHandled();
            await TryDispatchFailureAsync(RequestCancellation, new KernelProviderFailure(
                exception.Error.Code,
                exception.Error.Message,
                true));
            throw;
        }
        catch (KernelActionDeferredException)
        {
            markFailureHandled();
            throw;
        }
        catch (ActionOutcomeUncertainException)
        {
            markFailureHandled();
            throw;
        }
        catch (KernelActionFailedException exception)
        {
            markFailureHandled();
            await TryDispatchFailureAsync(RequestFailure, new KernelProviderFailure(
                exception.Error.Code,
                exception.Error.Message));
            throw;
        }
        catch (Exception exception)
        {
            markFailureHandled();
            await TryDispatchFailureAsync(RequestFailure, new KernelProviderFailure(
                $"PROVIDER_STREAM_{phase.ToUpperInvariant()}_FAILED",
                exception.Message));
            throw;
        }
    }

    private async ValueTask<bool> TryDispatchFailureAsync<TInput>(
        SharpClawActionKey key,
        TInput input)
    {
        try
        {
            await DispatchInputAsync(
                key,
                input,
                static (_, _) => ValueTask.FromResult(true),
                CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static TInput ExtractInput<TInput>(KernelActionEnvelope envelope, TInput original) =>
        envelope.Payload switch
        {
            TInput typed => typed,
            KernelActionEnvelope nested when nested.Payload is TInput typed => typed,
            _ => throw new KernelActionExecutionException(
                $"Provider action '{envelope.Key.Value}' returned an invalid replacement input.")
        };

    private static string SerializeRequest(KernelProviderRequestEnvelope request) =>
        JsonSerializer.Serialize(new
        {
            request.Request.Turn,
            request.Request.Profile,
            request.Request.Tools,
            request.Messages
        });

    private static List<ToolAwareMessage> BuildMessages(ProviderTurnRequest request)
    {
        var messages = new List<ToolAwareMessage>();
        foreach (var segment in request.Context.SystemPromptSegments)
            messages.Add(ToolAwareMessage.System(segment.Content));
        if (!string.IsNullOrWhiteSpace(request.Profile.SystemPrompt) &&
            !request.Context.SystemPromptSegments.Any(segment => segment.Key == "profile.system"))
            messages.Insert(0, ToolAwareMessage.System(request.Profile.SystemPrompt));
        messages.AddRange(request.Context.Messages.Select(ToToolAwareMessage));
        messages.Add(ToolAwareMessage.User(request.Turn.Input.Message));
        return messages;
    }

    private static ToolInvocation CreateInvocation(ProviderTurnRequest request, ChatToolCall call) =>
        new(
            Guid.NewGuid(),
            request.Turn.Conversation.ConversationId,
            call.Id,
            call.Name,
            ParseArguments(call.ArgumentsJson),
            request.Turn.Input.Caller ?? RequestPrincipal.Anonymous,
            request.Turn.Input.Features ?? ExtensionFeatureSet.Empty);

    private static JsonElement ParseArguments(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    private static ToolAwareMessage ToToolAwareMessage(ChatCompletionMessage message) =>
        message.Role switch
        {
            "system" => ToolAwareMessage.System(message.Content),
            "assistant" => ToolAwareMessage.Assistant(message.Content),
            _ => ToolAwareMessage.User(message.Content)
        };

    private static void ThrowToolOutcome(ToolInvocationOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case ActionOutcomeKind.Cancelled:
                throw new KernelActionCancelledException(
                    outcome.Error ?? new ExecutionError(
                        "TOOL_CANCELLED",
                        "The provider tool invocation was cancelled."));
            case ActionOutcomeKind.Deferred when outcome.Continuation is not null:
                throw new KernelActionDeferredException(outcome.Continuation);
            case ActionOutcomeKind.Uncertain when outcome.Uncertainty is not null:
                throw new ActionOutcomeUncertainException(outcome.Uncertainty);
            case ActionOutcomeKind.Failed:
                throw new KernelActionFailedException(
                    outcome.Error ?? new ExecutionError(
                        "TOOL_FAILED",
                        "The provider tool invocation failed."));
            default:
                throw new KernelActionFailedException(
                    new ExecutionError(
                        "TOOL_OUTCOME_INVALID",
                        "The provider tool invocation returned an incomplete outcome."));
        }
    }

    private async ValueTask ThrowStreamToolOutcomeAsync(
        ToolInvocationOutcome outcome,
        Action markFailureHandled)
    {
        markFailureHandled();
        if (outcome.Kind == ActionOutcomeKind.Cancelled)
        {
            await TryDispatchFailureAsync(RequestCancellation, new KernelProviderFailure(
                outcome.Error?.Code ?? "TOOL_CANCELLED",
                outcome.Error?.Message ?? "The provider tool invocation was cancelled.",
                true));
        }
        else if (outcome.Kind == ActionOutcomeKind.Failed)
        {
            await TryDispatchFailureAsync(RequestFailure, new KernelProviderFailure(
                outcome.Error?.Code ?? "TOOL_OUTCOME_FAILED",
                outcome.Error?.Message ??
                "The provider tool invocation did not complete."));
        }
        ThrowToolOutcome(outcome);
    }

    private enum ChunkActionStatus
    {
        Emitted,
        Suppressed
    }

    private sealed record ChunkActionResult(
        ChunkActionStatus Status,
        IReadOnlyList<ChatStreamChunk> Chunks);
}
