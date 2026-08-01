using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Kernel;

public sealed class ProviderRoundLoop : IProviderRoundLoop
{
    private readonly IKernelProviderTransport _transport;
    private readonly int _maximumRounds;

    public ProviderRoundLoop(IKernelProviderTransport transport, int maximumRounds = 8)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
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
        var messages = new List<ToolAwareMessage> { ToolAwareMessage.User(request.Turn.Input.Message) };
        messages.AddRange(request.Context.Messages.Select(ToToolAwareMessage));

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
                var arguments = ParseArguments(call.ArgumentsJson);
                var invocation = new ToolInvocation(
                    Guid.NewGuid(),
                    request.Turn.Conversation.ConversationId,
                    call.Id,
                    call.Name,
                    arguments,
                    request.Turn.Input.Caller ?? RequestPrincipal.Anonymous,
                    request.Turn.Input.Features ?? ExtensionFeatureSet.Empty);
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
        var messages = new List<ToolAwareMessage> { ToolAwareMessage.User(request.Turn.Input.Message) };
        messages.AddRange(request.Context.Messages.Select(ToToolAwareMessage));

        for (var round = 0; round < _maximumRounds; round++)
        {
            ChatCompletionResult? completion = null;
            await foreach (var chunk in _transport.StreamAsync(request, messages, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                if (!chunk.IsFinished || chunk.Finished is null)
                {
                    yield return chunk;
                    continue;
                }

                completion = chunk.Finished;
                if (!completion.HasToolCalls)
                {
                    yield return chunk;
                    yield break;
                }

                break;
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
                var invocation = new ToolInvocation(
                    Guid.NewGuid(),
                    request.Turn.Conversation.ConversationId,
                    call.Id,
                    call.Name,
                    ParseArguments(call.ArgumentsJson),
                    request.Turn.Input.Caller ?? RequestPrincipal.Anonymous,
                    request.Turn.Input.Features ?? ExtensionFeatureSet.Empty);
                var outcome = await toolPipeline.InvokeAsync(invocation, cancellationToken);
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
