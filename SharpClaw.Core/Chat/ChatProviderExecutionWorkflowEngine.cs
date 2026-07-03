using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Store-neutral workflow for executing one buffered provider chat turn.
/// </summary>
public sealed class ChatProviderExecutionWorkflowEngine(
    ChatNativeToolLoopEngine nativeToolLoop,
    ChatToolWorkflowEngine tools)
{
    private readonly ChatNativeToolLoopEngine _nativeToolLoop = nativeToolLoop
        ?? throw new ArgumentNullException(nameof(nativeToolLoop));
    private readonly ChatToolWorkflowEngine _tools = tools
        ?? throw new ArgumentNullException(nameof(tools));

    /// <summary>
    /// Executes a buffered provider call. When tools are enabled this resolves
    /// the effective tool surface and runs the native SharpClaw tool loop.
    /// Otherwise it performs one plain provider completion and normalizes the
    /// result into the same return shape.
    /// </summary>
    public async Task<ChatBufferedProviderExecutionResult> RunBufferedAsync(
        ChatBufferedProviderExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.NativeToolHost);

        if (request.EnableTools)
        {
            var effectiveTools = await _tools.GetEffectiveToolsAsync(
                new ChatEffectiveToolRequest(
                    request.TaskContext,
                    request.ToolAwareness,
                    request.AgentId),
                request.CancellationToken);

            var result = await _nativeToolLoop.RunAsync(
                new ChatNativeToolLoopRequest(
                    request.Provider,
                    request.ModelName,
                    request.SystemPrompt,
                    request.History,
                    request.AgentId,
                    request.ChannelId,
                    request.ModelCapabilityTags,
                    request.MaxCompletionTokens,
                    request.ProviderParameters,
                    request.CompletionParameters,
                    effectiveTools,
                    request.NativeToolHost,
                    request.CancellationToken,
                    request.ApprovalCallback,
                    request.TaskContext,
                    request.ToolAwareness,
                    request.ThreadId,
                    request.TimingRequestId,
                    request.GetElapsedMilliseconds,
                    request.MaxToolCallRounds));

            return new ChatBufferedProviderExecutionResult(
                result.AssistantContent,
                result.JobResults,
                result.TotalPromptTokens,
                result.TotalCompletionTokens,
                result.ProviderMetadataJson);
        }

        var plain = await request.Provider.CompleteAsync(
            new ChatProviderCompletionRequest(
                request.ModelName,
                request.SystemPrompt,
                request.History,
                request.MaxCompletionTokens,
                request.ProviderParameters,
                request.CompletionParameters),
            request.CancellationToken);

        return new ChatBufferedProviderExecutionResult(
            plain.Content ?? "",
            [],
            plain.Usage?.PromptTokens ?? 0,
            plain.Usage?.CompletionTokens ?? 0,
            plain.ProviderMetadataJson);
    }

    /// <summary>
    /// Streams a provider chat turn. This resolves the effective tool surface
    /// when tools are enabled, then delegates the streaming state machine to
    /// the native SharpClaw tool loop.
    /// </summary>
    public async IAsyncEnumerable<ChatNativeToolStreamingLoopEvent> StreamAsync(
        ChatStreamingProviderExecutionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.NativeToolHost);

        var cancellationToken = ct.CanBeCanceled
            ? ct
            : request.CancellationToken;
        var effectiveTools = request.EnableTools
            ? await _tools.GetEffectiveToolsAsync(
                new ChatEffectiveToolRequest(
                    request.TaskContext,
                    request.ToolAwareness,
                    request.AgentId),
                cancellationToken)
            : [];

        await foreach (var loopEvent in _nativeToolLoop.StreamAsync(
            new ChatNativeToolLoopRequest(
                request.Provider,
                request.ModelName,
                request.SystemPrompt,
                request.History,
                request.AgentId,
                request.ChannelId,
                request.ModelCapabilityTags,
                request.MaxCompletionTokens,
                request.ProviderParameters,
                request.CompletionParameters,
                effectiveTools,
                request.NativeToolHost,
                cancellationToken,
                request.ApprovalCallback,
                request.TaskContext,
                request.ToolAwareness,
                request.ThreadId,
                request.TimingRequestId,
                request.GetElapsedMilliseconds,
                request.MaxToolCallRounds),
            cancellationToken))
        {
            yield return loopEvent;
        }
    }
}

public sealed record ChatBufferedProviderExecutionRequest(
    IChatProviderRoundExecutor Provider,
    string ModelName,
    string? SystemPrompt,
    IReadOnlyList<ChatCompletionMessage> History,
    Guid AgentId,
    Guid ChannelId,
    IReadOnlySet<string> ModelCapabilityTags,
    int? MaxCompletionTokens,
    Dictionary<string, JsonElement>? ProviderParameters,
    CompletionParameters? CompletionParameters,
    bool EnableTools,
    IChatNativeToolLoopHost NativeToolHost,
    CancellationToken CancellationToken,
    Func<AgentJobResponse, CancellationToken, Task<bool>>? ApprovalCallback = null,
    TaskChatContext? TaskContext = null,
    Dictionary<string, bool>? ToolAwareness = null,
    Guid? ThreadId = null,
    string? TimingRequestId = null,
    Func<long?>? GetElapsedMilliseconds = null,
    int MaxToolCallRounds = 50);

public sealed record ChatBufferedProviderExecutionResult(
    string AssistantContent,
    IReadOnlyList<AgentJobResponse> JobResults,
    int TotalPromptTokens = 0,
    int TotalCompletionTokens = 0,
    string? ProviderMetadataJson = null);

public sealed record ChatStreamingProviderExecutionRequest(
    IChatProviderRoundExecutor Provider,
    string ModelName,
    string? SystemPrompt,
    IReadOnlyList<ChatCompletionMessage> History,
    Guid AgentId,
    Guid ChannelId,
    IReadOnlySet<string> ModelCapabilityTags,
    int? MaxCompletionTokens,
    Dictionary<string, JsonElement>? ProviderParameters,
    CompletionParameters? CompletionParameters,
    bool EnableTools,
    IChatNativeToolLoopHost NativeToolHost,
    CancellationToken CancellationToken,
    Func<AgentJobResponse, CancellationToken, Task<bool>>? ApprovalCallback = null,
    TaskChatContext? TaskContext = null,
    Dictionary<string, bool>? ToolAwareness = null,
    Guid? ThreadId = null,
    string? TimingRequestId = null,
    Func<long?>? GetElapsedMilliseconds = null,
    int MaxToolCallRounds = 50);

public interface IChatProviderRoundExecutor
{
    Task<ChatCompletionResult> CompleteAsync(
        ChatProviderCompletionRequest request,
        CancellationToken ct);

    Task<ChatCompletionResult> CompleteWithToolsAsync(
        ChatProviderToolCompletionRequest request,
        CancellationToken ct);

    IAsyncEnumerable<ChatStreamChunk> StreamWithToolsAsync(
        ChatProviderToolCompletionRequest request,
        CancellationToken ct);
}

public sealed record ChatProviderCompletionRequest(
    string ModelName,
    string? SystemPrompt,
    IReadOnlyList<ChatCompletionMessage> History,
    int? MaxCompletionTokens,
    Dictionary<string, JsonElement>? ProviderParameters,
    CompletionParameters? CompletionParameters);

public sealed record ChatProviderToolCompletionRequest(
    string ModelName,
    string? SystemPrompt,
    IReadOnlyList<ToolAwareMessage> Messages,
    IReadOnlyList<ChatToolDefinition> Tools,
    int? MaxCompletionTokens,
    Dictionary<string, JsonElement>? ProviderParameters,
    CompletionParameters? CompletionParameters);
