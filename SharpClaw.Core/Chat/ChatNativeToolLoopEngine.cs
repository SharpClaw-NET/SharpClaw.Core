using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Models;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Tools;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Store-neutral state machine for buffered native provider tool loops.
/// </summary>
public sealed class ChatNativeToolLoopEngine(
    ChatToolResultEngine toolResults,
    ILogger<ChatNativeToolLoopEngine>? logger = null)
{
    private readonly ChatToolResultEngine _toolResults = toolResults
        ?? throw new ArgumentNullException(nameof(toolResults));
    private readonly ILogger<ChatNativeToolLoopEngine> _logger =
        logger ?? NullLogger<ChatNativeToolLoopEngine>.Instance;

    /// <summary>
    /// Runs a buffered provider completion until the model stops asking for
    /// tools, the round cap is reached, cancellation is requested, or the
    /// local-inference envelope fails validation.
    /// </summary>
    public async Task<ChatNativeToolLoopResult> RunAsync(
        ChatNativeToolLoopRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Host);

        var messages = new List<ToolAwareMessage>(
            request.History.Count);
        foreach (var msg in request.History)
        {
            messages.Add(new ToolAwareMessage
            {
                Role = msg.Role,
                Content = msg.Content,
                ProviderMetadataJson = msg.ProviderMetadataJson
            });
        }

        var supportsVision = request.ModelCapabilityTags.Contains(
            WellKnownCapabilityKeys.Vision);
        var jobResults = new List<AgentJobResponse>();
        var toolNotation = new StringBuilder();
        var rounds = 0;
        var totalPromptTokens = 0;
        var totalCompletionTokens = 0;
        var roundJobIds = new List<Guid>();
        var logTiming = request.TimingRequestId is not null
            && _logger.IsEnabled(LogLevel.Debug);
        var providerRound = 0;
        var inlinePermissionCache =
            new Dictionary<ChatInlineToolPermissionCacheKey, AgentActionResult>();

        if (logTiming)
        {
            _logger.LogDebug(
                "Chat request {RequestId} resolved native tools. AgentId={AgentId} ChannelId={ChannelId} ThreadId={ThreadId} EffectiveTools={EffectiveTools} HistoryMessages={HistoryMessages} SupportsVision={SupportsVision} ElapsedMs={ElapsedMs}",
                request.TimingRequestId,
                request.AgentId,
                request.ChannelId,
                request.ThreadId,
                request.EffectiveTools.Count,
                request.History.Count,
                supportsVision,
                request.GetElapsedMilliseconds?.Invoke());
        }

        while (true)
        {
            providerRound++;
            var providerRoundTiming = Stopwatch.StartNew();
            ChatCompletionResult result;
            try
            {
                result = await request.Provider.CompleteWithToolsAsync(
                    new ChatProviderToolCompletionRequest(
                        request.ModelName,
                        request.SystemPrompt,
                        messages,
                        request.EffectiveTools,
                        request.MaxCompletionTokens,
                        request.ProviderParameters,
                        request.CompletionParameters),
                    request.CancellationToken);
            }
            catch (LocalInferenceEnvelopeException ex)
            {
                providerRoundTiming.Stop();
                _logger.LogWarning(
                    ex,
                    "Local-inference tool loop aborted for chat request {RequestId} after {ProviderRoundMs}ms: malformed envelope from model. Preview={Preview}",
                    request.TimingRequestId,
                    providerRoundTiming.ElapsedMilliseconds,
                    ex.PayloadPreview);

                return BuildEnvelopeFailureResult(
                    toolNotation,
                    jobResults,
                    totalPromptTokens,
                    totalCompletionTokens,
                    ex);
            }
            providerRoundTiming.Stop();

            if (result.Usage is { } usage)
            {
                totalPromptTokens += usage.PromptTokens;
                totalCompletionTokens += usage.CompletionTokens;
            }

            if (logTiming)
            {
                _logger.LogDebug(
                    "Chat request {RequestId} native provider round {Round} completed in {ProviderRoundMs}ms. ToolCalls={ToolCalls} PromptTokens={PromptTokens} CompletionTokens={CompletionTokens} ContentChars={ContentChars} ElapsedMs={ElapsedMs}",
                    request.TimingRequestId,
                    providerRound,
                    providerRoundTiming.ElapsedMilliseconds,
                    result.ToolCalls.Count,
                    result.Usage?.PromptTokens ?? 0,
                    result.Usage?.CompletionTokens ?? 0,
                    result.Content?.Length ?? 0,
                    request.GetElapsedMilliseconds?.Invoke());
            }

            if (!result.HasToolCalls || ++rounds > request.MaxToolCallRounds)
            {
                var finalContent = _toolResults.BuildFinalAssistantContent(
                    toolNotation.ToString(),
                    result.Content);
                return new ChatNativeToolLoopResult(
                    finalContent,
                    jobResults,
                    totalPromptTokens,
                    totalCompletionTokens,
                    result.ProviderMetadataJson);
            }

            messages.Add(ToolAwareMessage.AssistantWithToolCalls(
                result.ToolCalls,
                result.Content,
                result.ProviderMetadataJson));

            var anyUnresolvableApproval = false;
            roundJobIds.Clear();

            foreach (var toolCall in result.ToolCalls)
            {
                var (handled, taskResult) =
                    await request.Host.TryHandleTaskToolAsync(
                        toolCall,
                        request.TaskContext,
                        request.CancellationToken);
                if (handled)
                {
                    messages.Add(ToolAwareMessage.ToolResult(
                        toolCall.Id,
                        taskResult ?? ""));
                    toolNotation.Append(
                        ToolNotationFormatter.ForTaskTool(toolCall.Name));
                    continue;
                }

                if (request.Host.IsInlineTool(toolCall.Name))
                {
                    var inlineResult =
                        await request.Host.ExecuteInlineToolAsync(
                            toolCall,
                            request.AgentId,
                            request.ChannelId,
                            request.ThreadId,
                            inlinePermissionCache,
                            request.CancellationToken);
                    messages.Add(ToolAwareMessage.ToolResult(
                        toolCall.Id,
                        inlineResult));
                    toolNotation.Append(
                        ToolNotationFormatter.ForInlineTool(toolCall.Name));
                    continue;
                }

                var nativeTool =
                    await request.Host.ExecuteNativeJobToolAsync(
                        toolCall,
                        request.AgentId,
                        request.ChannelId,
                        supportsVision,
                        emitStreamEvents: false,
                        request.ApprovalCallback,
                        request.CancellationToken);

                messages.Add(nativeTool.ToolResultMessage);
                if (!nativeTool.Parsed)
                    continue;

                if (nativeTool.SubmittedJobId is { } submittedJobId)
                    roundJobIds.Add(submittedJobId);

                if (nativeTool.JobResponse is not null)
                    jobResults.Add(nativeTool.JobResponse);

                toolNotation.Append(nativeTool.ToolNotation);

                if (nativeTool.AwaitingUnresolvableApproval)
                    anyUnresolvableApproval = true;
            }

            if (roundJobIds.Count > 0 && result.Usage is { } roundUsage)
            {
                await request.Host.RecordRoundTokenUsageAsync(
                    roundJobIds,
                    roundUsage.PromptTokens,
                    roundUsage.CompletionTokens,
                    request.CancellationToken);
                _toolResults.ApplyRoundTokenUsageToJobResponses(
                    jobResults,
                    roundJobIds,
                    roundUsage.PromptTokens,
                    roundUsage.CompletionTokens);
            }

            if (anyUnresolvableApproval)
            {
                ChatCompletionResult finalResult;
                var finalApprovalTiming = Stopwatch.StartNew();
                try
                {
                    finalResult = await request.Provider.CompleteWithToolsAsync(
                        new ChatProviderToolCompletionRequest(
                            request.ModelName,
                            request.SystemPrompt,
                            messages,
                            request.EffectiveTools,
                            request.MaxCompletionTokens,
                            request.ProviderParameters,
                            request.CompletionParameters),
                        request.CancellationToken);
                }
                catch (LocalInferenceEnvelopeException ex)
                {
                    finalApprovalTiming.Stop();
                    _logger.LogWarning(
                        ex,
                        "Local-inference final approval round aborted for chat request {RequestId} after {ProviderRoundMs}ms: malformed envelope from model. Preview={Preview}",
                        request.TimingRequestId,
                        finalApprovalTiming.ElapsedMilliseconds,
                        ex.PayloadPreview);

                    return BuildEnvelopeFailureResult(
                        toolNotation,
                        jobResults,
                        totalPromptTokens,
                        totalCompletionTokens,
                        ex);
                }
                finalApprovalTiming.Stop();

                if (finalResult.Usage is { } finalUsage)
                {
                    totalPromptTokens += finalUsage.PromptTokens;
                    totalCompletionTokens += finalUsage.CompletionTokens;
                }

                if (logTiming)
                {
                    _logger.LogDebug(
                        "Chat request {RequestId} final approval provider round completed in {ProviderRoundMs}ms. PromptTokens={PromptTokens} CompletionTokens={CompletionTokens} ContentChars={ContentChars} ElapsedMs={ElapsedMs}",
                        request.TimingRequestId,
                        finalApprovalTiming.ElapsedMilliseconds,
                        finalResult.Usage?.PromptTokens ?? 0,
                        finalResult.Usage?.CompletionTokens ?? 0,
                        finalResult.Content?.Length ?? 0,
                        request.GetElapsedMilliseconds?.Invoke());
                }

                var finalContent = _toolResults.BuildFinalAssistantContent(
                    toolNotation.ToString(),
                    finalResult.Content);
                return new ChatNativeToolLoopResult(
                    finalContent,
                    jobResults,
                    totalPromptTokens,
                    totalCompletionTokens,
                    finalResult.ProviderMetadataJson);
            }
        }
    }

    /// <summary>
    /// Streams provider output and native tool-loop events until the model
    /// stops asking for tools, the round cap is reached, or cancellation is
    /// requested.
    /// </summary>
    public async IAsyncEnumerable<ChatNativeToolStreamingLoopEvent> StreamAsync(
        ChatNativeToolLoopRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Host);

        var cancellationToken = ct.CanBeCanceled
            ? ct
            : request.CancellationToken;
        var messages = new List<ToolAwareMessage>(
            request.History.Count);
        foreach (var msg in request.History)
        {
            messages.Add(new ToolAwareMessage
            {
                Role = msg.Role,
                Content = msg.Content,
                ProviderMetadataJson = msg.ProviderMetadataJson
            });
        }

        var supportsVision = request.ModelCapabilityTags.Contains(
            WellKnownCapabilityKeys.Vision);
        var jobResults = new List<AgentJobResponse>();
        var fullContent = new StringBuilder();
        var rounds = 0;
        var totalPromptTokens = 0;
        var totalCompletionTokens = 0;
        var roundJobIds = new List<Guid>();
        string? finalProviderMetadataJson = null;
        var logTiming = request.TimingRequestId is not null
            && _logger.IsEnabled(LogLevel.Debug);
        var providerRound = 0;
        var inlinePermissionCache =
            new Dictionary<ChatInlineToolPermissionCacheKey, AgentActionResult>();

        while (true)
        {
            providerRound++;
            var providerRoundTiming = Stopwatch.StartNew();
            ChatCompletionResult? roundResult = null;
            var roundDeltaContent = new StringBuilder();

            await foreach (var chunk in request.Provider.StreamWithToolsAsync(
                new ChatProviderToolCompletionRequest(
                    request.ModelName,
                    request.SystemPrompt,
                    messages,
                    request.EffectiveTools,
                    request.MaxCompletionTokens,
                    request.ProviderParameters,
                    request.CompletionParameters),
                cancellationToken))
            {
                if (chunk.Delta is not null)
                {
                    roundDeltaContent.Append(chunk.Delta);
                    yield return ChatNativeToolStreamingLoopEvent.TextDelta(
                        chunk.Delta);
                }

                if (chunk.IsFinished)
                    roundResult = chunk.Finished;
            }

            if (roundResult is null)
            {
                fullContent.Append(roundDeltaContent);
                providerRoundTiming.Stop();
                if (logTiming)
                {
                    _logger.LogDebug(
                        "Streaming chat request {RequestId} provider round {Round} ended without a finished result after {ProviderRoundMs}ms. ElapsedMs={ElapsedMs}",
                        request.TimingRequestId,
                        providerRound,
                        providerRoundTiming.ElapsedMilliseconds,
                        request.GetElapsedMilliseconds?.Invoke());
                }

                break;
            }
            providerRoundTiming.Stop();

            if (roundResult.Usage is { } roundUsage)
            {
                totalPromptTokens += roundUsage.PromptTokens;
                totalCompletionTokens += roundUsage.CompletionTokens;
            }

            if (logTiming)
            {
                _logger.LogDebug(
                    "Streaming chat request {RequestId} provider round {Round} completed in {ProviderRoundMs}ms. ToolCalls={ToolCalls} PromptTokens={PromptTokens} CompletionTokens={CompletionTokens} ContentChars={ContentChars} ElapsedMs={ElapsedMs}",
                    request.TimingRequestId,
                    providerRound,
                    providerRoundTiming.ElapsedMilliseconds,
                    roundResult.ToolCalls.Count,
                    roundResult.Usage?.PromptTokens ?? 0,
                    roundResult.Usage?.CompletionTokens ?? 0,
                    roundResult.Content?.Length ?? 0,
                    request.GetElapsedMilliseconds?.Invoke());
            }

            var roundContent = roundResult.Content;
            if (string.IsNullOrEmpty(roundContent)
                && roundDeltaContent.Length > 0)
            {
                roundContent = roundDeltaContent.ToString();
            }

            fullContent.Append(roundContent ?? "");

            if (!roundResult.HasToolCalls
                || ++rounds > request.MaxToolCallRounds)
            {
                finalProviderMetadataJson = roundResult.ProviderMetadataJson;
                break;
            }

            messages.Add(ToolAwareMessage.AssistantWithToolCalls(
                roundResult.ToolCalls,
                roundResult.Content,
                roundResult.ProviderMetadataJson));

            fullContent.Clear();
            roundJobIds.Clear();

            foreach (var toolCall in roundResult.ToolCalls)
            {
                var (handled, taskResult) =
                    await request.Host.TryHandleTaskToolAsync(
                        toolCall,
                        request.TaskContext,
                        cancellationToken);
                if (handled)
                {
                    messages.Add(ToolAwareMessage.ToolResult(
                        toolCall.Id,
                        taskResult ?? ""));
                    var taskNotation =
                        ToolNotationFormatter.ForTaskTool(toolCall.Name);
                    fullContent.Append(taskNotation);
                    yield return ChatNativeToolStreamingLoopEvent.TextDelta(
                        taskNotation);
                    continue;
                }

                if (request.Host.IsInlineTool(toolCall.Name))
                {
                    var inlineResult =
                        await request.Host.ExecuteInlineToolAsync(
                            toolCall,
                            request.AgentId,
                            request.ChannelId,
                            request.ThreadId,
                            inlinePermissionCache,
                            cancellationToken);
                    messages.Add(ToolAwareMessage.ToolResult(
                        toolCall.Id,
                        inlineResult));
                    var inlineNotation =
                        ToolNotationFormatter.ForInlineTool(toolCall.Name);
                    fullContent.Append(inlineNotation);
                    yield return ChatNativeToolStreamingLoopEvent.TextDelta(
                        inlineNotation);
                    continue;
                }

                var nativeTool =
                    await request.Host.ExecuteNativeJobToolAsync(
                        toolCall,
                        request.AgentId,
                        request.ChannelId,
                        supportsVision,
                        emitStreamEvents: true,
                        request.ApprovalCallback,
                        cancellationToken);

                messages.Add(nativeTool.ToolResultMessage);
                if (!nativeTool.Parsed)
                    continue;

                if (nativeTool.SubmittedJobId is { } submittedJobId)
                    roundJobIds.Add(submittedJobId);

                foreach (var streamEvent in nativeTool.StreamEvents)
                {
                    yield return ChatNativeToolStreamingLoopEvent.StreamEvent(
                        streamEvent);
                }

                if (nativeTool.JobResponse is not null)
                    jobResults.Add(nativeTool.JobResponse);

                fullContent.Append(nativeTool.ToolNotation);
                yield return ChatNativeToolStreamingLoopEvent.BufferedText(
                    nativeTool.ToolNotation);
            }

            if (roundJobIds.Count > 0 && roundResult.Usage is { } usage)
            {
                await request.Host.RecordRoundTokenUsageAsync(
                    roundJobIds,
                    usage.PromptTokens,
                    usage.CompletionTokens,
                    cancellationToken);
                _toolResults.ApplyRoundTokenUsageToJobResponses(
                    jobResults,
                    roundJobIds,
                    usage.PromptTokens,
                    usage.CompletionTokens);
            }
        }

        yield return ChatNativeToolStreamingLoopEvent.Completed(
            new ChatNativeToolStreamingLoopResult(
                fullContent.ToString(),
                jobResults,
                totalPromptTokens,
                totalCompletionTokens,
                finalProviderMetadataJson,
                providerRound));
    }

    private ChatNativeToolLoopResult BuildEnvelopeFailureResult(
        StringBuilder toolNotation,
        IReadOnlyList<AgentJobResponse> jobResults,
        int totalPromptTokens,
        int totalCompletionTokens,
        LocalInferenceEnvelopeException ex)
    {
        return new ChatNativeToolLoopResult(
            _toolResults.BuildMalformedEnvelopeAssistantContent(
                toolNotation.ToString(),
                ex.PayloadPreview),
            jobResults,
            totalPromptTokens,
            totalCompletionTokens);
    }
}

/// <summary>
/// Request data for the Core buffered native chat tool loop.
/// </summary>
public sealed record ChatNativeToolLoopRequest(
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
    IReadOnlyList<ChatToolDefinition> EffectiveTools,
    IChatNativeToolLoopHost Host,
    CancellationToken CancellationToken,
    Func<AgentJobResponse, CancellationToken, Task<bool>>? ApprovalCallback = null,
    TaskChatContext? TaskContext = null,
    Dictionary<string, bool>? ToolAwareness = null,
    Guid? ThreadId = null,
    string? TimingRequestId = null,
    Func<long?>? GetElapsedMilliseconds = null,
    int MaxToolCallRounds = 50);

/// <summary>
/// Host callbacks required by the Core native chat tool loop.
/// </summary>
public interface IChatNativeToolLoopHost
{
    /// <summary>Returns true when a tool name is an inline module tool.</summary>
    bool IsInlineTool(string toolName);

    /// <summary>
    /// Gives task-scoped tools a chance to handle one provider tool call.
    /// </summary>
    Task<(bool Handled, string? Result)> TryHandleTaskToolAsync(
        ChatToolCall toolCall,
        TaskChatContext? taskContext,
        CancellationToken ct);

    /// <summary>Executes one inline module tool call.</summary>
    Task<string> ExecuteInlineToolAsync(
        ChatToolCall toolCall,
        Guid agentId,
        Guid channelId,
        Guid? threadId,
        IDictionary<ChatInlineToolPermissionCacheKey, AgentActionResult> permissionCache,
        CancellationToken ct);

    /// <summary>Executes one native SharpClaw job tool call.</summary>
    Task<ChatNativeJobToolExecutionResult> ExecuteNativeJobToolAsync(
        ChatToolCall toolCall,
        Guid agentId,
        Guid channelId,
        bool supportsVision,
        bool emitStreamEvents,
        Func<AgentJobResponse, CancellationToken, Task<bool>>? approvalCallback,
        CancellationToken ct);

    /// <summary>
    /// Persists one provider round's token usage against jobs submitted in
    /// that round.
    /// </summary>
    Task RecordRoundTokenUsageAsync(
        IReadOnlyList<Guid> jobIds,
        int promptTokens,
        int completionTokens,
        CancellationToken ct);
}

/// <summary>
/// Result of running the Core buffered native chat tool loop.
/// </summary>
public sealed record ChatNativeToolLoopResult(
    string AssistantContent,
    IReadOnlyList<AgentJobResponse> JobResults,
    int TotalPromptTokens = 0,
    int TotalCompletionTokens = 0,
    string? ProviderMetadataJson = null);

/// <summary>
/// Stream item emitted by the Core native chat streaming loop.
/// </summary>
public sealed record ChatNativeToolStreamingLoopEvent(
    ChatNativeToolStreamingLoopEventKind Kind,
    string? Text = null,
    ChatStreamEvent? StreamEventValue = null,
    ChatNativeToolStreamingLoopResult? Result = null)
{
    /// <summary>Emits text that should be sent to the chat stream client.</summary>
    public static ChatNativeToolStreamingLoopEvent TextDelta(string text) =>
        new(ChatNativeToolStreamingLoopEventKind.TextDelta, Text: text);

    /// <summary>
    /// Emits text that should be retained for partial persistence but not
    /// sent as a text delta.
    /// </summary>
    public static ChatNativeToolStreamingLoopEvent BufferedText(string text) =>
        new(ChatNativeToolStreamingLoopEventKind.BufferedText, Text: text);

    /// <summary>Emits a non-text chat stream event.</summary>
    public static ChatNativeToolStreamingLoopEvent StreamEvent(
        ChatStreamEvent streamEvent) =>
        new(
            ChatNativeToolStreamingLoopEventKind.StreamEvent,
            StreamEventValue: streamEvent);

    /// <summary>Emits the final streaming loop result.</summary>
    public static ChatNativeToolStreamingLoopEvent Completed(
        ChatNativeToolStreamingLoopResult result) =>
        new(ChatNativeToolStreamingLoopEventKind.Completed, Result: result);
}

/// <summary>
/// Kind of native chat streaming loop item.
/// </summary>
public enum ChatNativeToolStreamingLoopEventKind
{
    /// <summary>Text sent to the stream client and retained for persistence.</summary>
    TextDelta,

    /// <summary>Text retained for persistence without client streaming.</summary>
    BufferedText,

    /// <summary>A structured chat stream event.</summary>
    StreamEvent,

    /// <summary>The final native streaming loop result.</summary>
    Completed,
}

/// <summary>
/// Result of a completed Core native chat streaming loop.
/// </summary>
public sealed record ChatNativeToolStreamingLoopResult(
    string AssistantContent,
    IReadOnlyList<AgentJobResponse> JobResults,
    int TotalPromptTokens = 0,
    int TotalCompletionTokens = 0,
    string? ProviderMetadataJson = null,
    int ProviderRounds = 0);
