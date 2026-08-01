using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Kernel;

public sealed class ProviderRoundLoop : IProviderRoundLoop
{
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

        for (var round = 0; round < _maximumRounds; round++)
        {
            var completion = await _transport.CompleteAsync(request, messages, cancellationToken);
            if (!completion.HasToolCalls || completion.ToolCalls.Count == 0)
                return completion;

            messages.Add(ToolAwareMessage.AssistantWithToolCalls(
                completion.ToolCalls,
                completion.Content,
                completion.ProviderMetadataJson));
            foreach (var call in completion.ToolCalls ?? Array.Empty<ChatToolCall>())
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

    public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(
        ProviderTurnRequest request,
        IUnifiedToolPipeline toolPipeline,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolPipeline);
        var messages = BuildMessages(request);

        for (var round = 0; round < _maximumRounds; round++)
        {
            ChatCompletionResult? completion = null;
            await foreach (var rawChunk in _transport.StreamAsync(request, messages, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                var received = await DispatchChunkAsync(
                    new SharpClawActionKey("provider.stream.chunk.receive"),
                    rawChunk,
                    cancellationToken);
                var transformed = received is null
                    ? null
                    : await DispatchChunkAsync(
                        new SharpClawActionKey("provider.stream.chunk.transform"),
                        received,
                        cancellationToken);
                var sendCandidate = transformed ?? received;
                var sent = sendCandidate is null
                    ? null
                    : await DispatchChunkAsync(
                        new SharpClawActionKey("provider.stream.chunk.send"),
                        sendCandidate,
                        cancellationToken);

                if (rawChunk.IsFinished && rawChunk.Finished is not null)
                {
                    completion = transformed?.Finished ?? received?.Finished ?? rawChunk.Finished;
                    if (completion.HasToolCalls)
                        break;

                    if (sent is not null)
                        yield return sent;
                    yield break;
                }

                if (sent is not null)
                    yield return sent;
            }

            if (completion is null)
            {
                yield return ChatStreamChunk.Final(new ChatCompletionResult
                {
                    Refusal = "The provider stream ended without a completion.",
                    ToolCalls = Array.Empty<ChatToolCall>()
                });
                yield break;
            }

            messages.Add(ToolAwareMessage.AssistantWithToolCalls(
                completion.ToolCalls,
                completion.Content,
                completion.ProviderMetadataJson));

            foreach (var call in completion.ToolCalls ?? Array.Empty<ChatToolCall>())
            {
                var outcome = await toolPipeline.InvokeAsync(CreateInvocation(request, call), cancellationToken);
                if (outcome.Kind != ActionOutcomeKind.Completed)
                {
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

        yield return ChatStreamChunk.Final(new ChatCompletionResult
        {
            Refusal = "The provider round limit was reached.",
            ToolCalls = Array.Empty<ChatToolCall>()
        });
    }

    private async ValueTask<ChatStreamChunk?> DispatchChunkAsync(
        SharpClawActionKey key,
        ChatStreamChunk chunk,
        CancellationToken cancellationToken)
    {
        var descriptor = _graph.GetStandardAction(key);
        var outcome = await _dispatcher.RunAsync(
            descriptor,
            new KernelActionEnvelope(key, chunk),
            (action, _) => ValueTask.FromResult<object>(action.Payload ?? chunk),
            _graph.ActionSnapshot,
            cancellationToken);
        if (outcome.Kind != ActionOutcomeKind.Completed)
            return null;
        return outcome.Result switch
        {
            KernelActionEnvelope envelope when envelope.Payload is ChatStreamChunk value => value,
            ChatStreamChunk value => value,
            null => null,
            _ => throw new KernelActionExecutionException(
                $"Stream action '{key.Value}' returned an invalid chunk type.")
        };
    }

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
}
