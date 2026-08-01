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

    public ProviderRoundLoop(IKernelProviderTransport transport, int maximumRounds = 8)
        : this(transport, new KernelGraphBuilder().Compile(), null, maximumRounds)
    {
    }

    public ProviderRoundLoop(
        IKernelProviderTransport transport,
        KernelGraph graph,
        KernelActionDispatcher? dispatcher = null,
        int maximumRounds = 8)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _dispatcher = dispatcher ?? new KernelActionDispatcher(graph);
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
                    {
                        return new ChatCompletionResult
                        {
                            Content = outcome.Result?.Content ?? string.Empty,
                            Refusal = outcome.Error?.Message ?? "The tool invocation did not complete.",
                            ToolCalls = Array.Empty<ChatToolCall>()
                        };
                    }

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
        catch (ProviderActionCancelledException exception)
        {
            await TryDispatchFailureAsync(RequestCancellation, new KernelProviderFailure(
                "PROVIDER_ACTION_CANCELLED",
                exception.Message,
                true));
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
                    stream = await DispatchInputAsync(
                        SharpClawActions.Provider.Send,
                        state,
                        (value, ct) => ValueTask.FromResult(
                            _transport.StreamAsync(value.Request, value.Messages, ct)),
                        cancellationToken);
                }
                catch (ProviderActionCancelledException exception)
                {
                    failureDispatched = true;
                    await TryDispatchFailureAsync(RequestCancellation, new KernelProviderFailure(
                        "PROVIDER_ACTION_CANCELLED",
                        exception.Message,
                        true));
                    throw;
                }

                ChatCompletionResult? completion = null;
                ChatStreamChunk? finalChunk = null;
                await foreach (var rawChunk in stream.WithCancellation(cancellationToken))
                {
                    var received = await DispatchChunkAsync(
                        new SharpClawActionKey("provider.stream.chunk.receive"),
                        rawChunk,
                        cancellationToken);
                    if (received.Status == ChunkActionStatus.Cancelled)
                    {
                        failureDispatched = true;
                        await TryDispatchFailureAsync(RequestCancellation, new KernelProviderFailure(
                            "PROVIDER_STREAM_CANCELLED",
                            "The provider stream was cancelled.",
                            true));
                        yield break;
                    }
                    if (received.Status == ChunkActionStatus.Failed)
                    {
                        failureDispatched = true;
                        await TryDispatchFailureAsync(RequestFailure, new KernelProviderFailure(
                            "PROVIDER_STREAM_RECEIVE_FAILED",
                            "The provider stream receive action failed."));
                        yield break;
                    }
                    if (received.Status == ChunkActionStatus.Suppressed)
                        continue;

                    foreach (var receivedChunk in received.Chunks)
                    {
                        var transformed = await DispatchChunkAsync(
                            new SharpClawActionKey("provider.stream.chunk.transform"),
                            receivedChunk,
                            cancellationToken);
                        if (transformed.Status == ChunkActionStatus.Cancelled)
                        {
                            failureDispatched = true;
                            await TryDispatchFailureAsync(RequestCancellation, new KernelProviderFailure(
                                "PROVIDER_STREAM_CANCELLED",
                                "The provider stream transform was cancelled.",
                                true));
                            yield break;
                        }
                        if (transformed.Status == ChunkActionStatus.Failed)
                        {
                            failureDispatched = true;
                            await TryDispatchFailureAsync(RequestFailure, new KernelProviderFailure(
                                "PROVIDER_STREAM_TRANSFORM_FAILED",
                                "The provider stream transform action failed."));
                            yield break;
                        }
                        if (transformed.Status == ChunkActionStatus.Suppressed)
                            continue;

                        foreach (var candidate in transformed.Chunks)
                        {
                            var sent = await DispatchChunkAsync(
                                new SharpClawActionKey("provider.stream.chunk.send"),
                                candidate,
                                cancellationToken);
                            if (sent.Status == ChunkActionStatus.Cancelled)
                            {
                                failureDispatched = true;
                                await TryDispatchFailureAsync(RequestCancellation, new KernelProviderFailure(
                                    "PROVIDER_STREAM_CANCELLED",
                                    "The provider stream send action was cancelled.",
                                    true));
                                yield break;
                            }
                            if (sent.Status == ChunkActionStatus.Failed)
                            {
                                failureDispatched = true;
                                await TryDispatchFailureAsync(RequestFailure, new KernelProviderFailure(
                                    "PROVIDER_STREAM_SEND_FAILED",
                                    "The provider stream send action failed."));
                                yield break;
                            }
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

                            if (completion is not null)
                                break;
                        }

                        if (completion is not null)
                            break;
                    }

                    if (completion is not null)
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
                catch (ProviderActionCancelledException exception)
                {
                    failureDispatched = true;
                    await TryDispatchFailureAsync(RequestCancellation, new KernelProviderFailure(
                        "PROVIDER_ACTION_CANCELLED",
                        exception.Message,
                        true));
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
                catch (ProviderActionCancelledException exception)
                {
                    failureDispatched = true;
                    await TryDispatchFailureAsync(RequestCancellation, new KernelProviderFailure(
                        "PROVIDER_ACTION_CANCELLED",
                        exception.Message,
                        true));
                    throw;
                }
                if (!(finalChunk.IsFinished && finalChunk.Finished?.HasToolCalls == true))
                    yield return finalChunk;
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
                    {
                        failureDispatched = true;
                        await TryDispatchFailureAsync(RequestFailure, new KernelProviderFailure(
                            "PROVIDER_TOOL_INVOCATION_FAILED",
                            outcome.Error?.Message ?? "The provider tool invocation did not complete."));
                        yield return ChatStreamChunk.Final(new ChatCompletionResult
                        {
                            Content = outcome.Result?.Content ?? string.Empty,
                            Refusal = outcome.Error?.Message ?? "The tool invocation did not complete.",
                            ToolCalls = Array.Empty<ChatToolCall>()
                        });
                        yield break;
                    }

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
        var raw = await DispatchInputAsync(
            SharpClawActions.Provider.Send,
            state,
            (value, ct) => _transport.CompleteAsync(value.Request, value.Messages, ct),
            cancellationToken);
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
        var outcome = await _dispatcher.RunAsync(
            descriptor,
            new KernelActionEnvelope(key, input),
            async (envelope, ct) => (object)(await terminal(ExtractInput(envelope, input), ct))!,
            _graph.ActionSnapshot,
            cancellationToken);
        if (outcome.Kind == ActionOutcomeKind.Cancelled)
            throw new ProviderActionCancelledException(
                key,
                outcome.Error?.Message ?? "The provider action was cancelled.");
        if (outcome.Kind == ActionOutcomeKind.Uncertain && outcome.Uncertainty is not null)
            throw new ActionOutcomeUncertainException(outcome.Uncertainty);
        if (outcome.Kind != ActionOutcomeKind.Completed)
            throw new KernelActionExecutionException(
                $"Provider action '{key.Value}' did not complete. " +
                $"{outcome.Error?.Message ?? outcome.Kind.ToString()}.");
        return outcome.Result is TResult typed
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
            (envelope, _) => ValueTask.FromResult<object>(envelope),
            _graph.ActionSnapshot,
            cancellationToken);
        if (outcome.Kind == ActionOutcomeKind.Cancelled)
            return new ChunkActionResult(ChunkActionStatus.Cancelled, []);
        if (outcome.Kind != ActionOutcomeKind.Completed)
            return new ChunkActionResult(ChunkActionStatus.Failed, []);
        var result = outcome.Result switch
        {
            KernelActionEnvelope envelope => envelope.Payload,
            _ => outcome.Result
        };
        if (result is null)
            return new ChunkActionResult(ChunkActionStatus.Suppressed, []);
        if (result is ChatStreamChunk value)
            return new ChunkActionResult(ChunkActionStatus.Emitted, [value]);
        if (result is IReadOnlyList<ChatStreamChunk> list)
            return list.Count == 0
                ? new ChunkActionResult(ChunkActionStatus.Suppressed, [])
                : new ChunkActionResult(ChunkActionStatus.Emitted, list);
        if (result is IEnumerable<ChatStreamChunk> sequence)
        {
            var chunks = sequence.ToArray();
            return chunks.Length == 0
                ? new ChunkActionResult(ChunkActionStatus.Suppressed, [])
                : new ChunkActionResult(ChunkActionStatus.Emitted, chunks);
        }
        throw new KernelActionExecutionException(
            $"Stream action '{key.Value}' returned an invalid chunk type.");
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

    private enum ChunkActionStatus
    {
        Emitted,
        Suppressed,
        Cancelled,
        Failed
    }

    private sealed record ChunkActionResult(
        ChunkActionStatus Status,
        IReadOnlyList<ChatStreamChunk> Chunks);
}

internal sealed class ProviderActionCancelledException(
    SharpClawActionKey key,
    string message) : OperationCanceledException(
        $"Provider action '{key.Value}' was cancelled: {message}");
