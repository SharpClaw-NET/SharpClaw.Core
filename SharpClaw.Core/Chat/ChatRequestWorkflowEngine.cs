using SharpClaw.Core.State;
using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Store-neutral orchestration for the common SharpClaw chat request
/// lifecycle. Hosts provide persistence, header expansion, thread activity,
/// and token accounting through <see cref="IChatRequestWorkflowHost"/>.
/// </summary>
public sealed class ChatRequestWorkflowEngine(ChatMessageEngine messages)
{
    public ChatRequestWorkflowEngine()
        : this(new ChatMessageEngine())
    {
    }

    /// <summary>
    /// Acquires thread activity, loads provider history, applies the
    /// model-facing chat header, and persists the user message before provider
    /// inference starts.
    /// </summary>
    public async Task<ChatPreparedRequestState> BeginPreparedRequestAsync(
        ChatPreparedRequest request,
        IChatRequestWorkflowHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        IDisposable? threadProcessing = null;
        try
        {
            if (request.ThreadId is { } concreteThreadId)
            {
                threadProcessing = await host.BeginThreadProcessingAsync(
                    concreteThreadId,
                    request.ChatRequest.ClientType,
                    ct);
            }

            var history = new List<ChatCompletionMessage>();
            int? maxHistoryMessages = null;
            int? maxHistoryCharacters = null;
            if (request.ThreadId is { } threadId)
            {
                var historyLoad = await host.LoadProviderThreadHistoryAsync(
                    threadId,
                    ct);
                history.AddRange(historyLoad.Messages);
                maxHistoryMessages = historyLoad.MaxMessages;
                maxHistoryCharacters = historyLoad.MaxCharacters;
            }

            history.Add(new ChatCompletionMessage(
                ChatRoles.User,
                request.ChatRequest.Message));

            var chatHeader = await host.BuildChatHeaderAsync(
                request.Channel,
                request.Agent,
                request.ChatRequest,
                request.Plan,
                ct);
            if (chatHeader is not null)
            {
                history[^1] = new ChatCompletionMessage(
                    ChatRoles.User,
                    chatHeader + request.ChatRequest.Message);
            }

            var userMessage = await CreateUserMessageAsync(
                request.ChannelId,
                request.ThreadId,
                request.ChatRequest,
                host,
                ct);

            await host.PersistChatMessagesAsync(
                [userMessage],
                CancellationToken.None);

            return new ChatPreparedRequestState(
                request.ChannelId,
                request.ThreadId,
                request.Channel,
                request.Agent,
                request.Plan,
                history,
                userMessage,
                maxHistoryMessages,
                maxHistoryCharacters,
                threadProcessing);
        }
        catch
        {
            threadProcessing?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Persists the assistant message for a completed provider exchange,
    /// records token usage, publishes thread activity, and returns the public
    /// response with cost data.
    /// </summary>
    public async Task<ChatExchangePersistenceResult> PersistCompletedExchangeAsync(
        ChatCompletedExchange request,
        IChatRequestWorkflowHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var assistantMessage = messages.CreateAssistantMessage(
            request.ChannelId,
            request.ThreadId,
            request.ChatRequest,
            request.Agent,
            request.AssistantContent,
            request.TotalPromptTokens,
            request.TotalCompletionTokens,
            request.ProviderMetadataJson);

        await host.PersistChatMessagesAsync(
            [assistantMessage],
            CancellationToken.None);

        await host.RecordTokensForCurrentExecutionAsync(
            request.TotalPromptTokens,
            request.TotalCompletionTokens,
            ct);

        host.RecordAssistantTokens(
            request.ChannelId,
            request.ThreadId,
            request.Agent.Id,
            request.Agent.Name,
            request.TotalPromptTokens,
            request.TotalCompletionTokens);

        if (request.ThreadId is { } threadId)
            host.PublishNewMessages(threadId, request.ChatRequest.ClientType);

        var costs = await host.GetResponseCostsAsync(
            request.ChannelId,
            request.ThreadId,
            request.Agent.Id,
            request.Agent.Name,
            ct);

        var response = new ChatResponse(
            messages.ToResponse(request.UserMessage),
            messages.ToResponse(assistantMessage),
            request.JobResults.Count > 0 ? request.JobResults : null,
            costs.ChannelCost,
            costs.ThreadCost,
            costs.AgentCost);

        return new ChatExchangePersistenceResult(
            response,
            assistantMessage,
            costs);
    }

    /// <summary>
    /// Best-effort persistence for assistant text already emitted before a
    /// stream was interrupted.
    /// </summary>
    public async Task<ChatMessagePersistenceResult> TryPersistPartialAssistantMessageAsync(
        ChatPartialAssistantPersistenceRequest request,
        IChatRequestWorkflowHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        try
        {
            var assistantMessage = messages.CreateAssistantMessage(
                request.ChannelId,
                request.ThreadId,
                request.ChatRequest,
                request.Agent,
                request.Content,
                request.TotalPromptTokens,
                request.TotalCompletionTokens,
                request.ProviderMetadataJson);

            await host.PersistChatMessagesAsync(
                [assistantMessage],
                CancellationToken.None);

            if (request.ThreadId is { } threadId)
                host.PublishNewMessages(threadId, request.ChatRequest.ClientType);

            return new ChatMessagePersistenceResult(
                true,
                assistantMessage,
                null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new ChatMessagePersistenceResult(false, null, ex);
        }
    }

    /// <summary>
    /// Best-effort persistence for exceptions raised after the app accepted a
    /// chat request.
    /// </summary>
    public async Task<ChatMessagesPersistenceResult> TryPersistExceptionErrorAsync(
        ChatExceptionPersistenceRequest request,
        IChatRequestWorkflowHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        return await TryPersistErrorMessagesAsync(
            request.ChannelId,
            request.ThreadId,
            request.ChatRequest,
            request.Exception.Message,
            request.UserMessageAlreadyPersisted,
            host,
            ct);
    }

    /// <summary>
    /// Best-effort persistence for public stream/API error handlers that need
    /// the user prompt and visible system error in history.
    /// </summary>
    public async Task<ChatMessagesPersistenceResult> TryPersistPublicErrorAsync(
        ChatPublicErrorPersistenceRequest request,
        IChatRequestWorkflowHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var userAlreadySaved = await host.HasUserMessageAsync(
            request.ChannelId,
            request.ThreadId,
            request.ChatRequest.Message,
            ct);

        return await TryPersistErrorMessagesAsync(
            request.ChannelId,
            request.ThreadId,
            request.ChatRequest,
            request.ErrorMessage,
            userAlreadySaved,
            host,
            ct);
    }

    private async Task<ChatMessagesPersistenceResult> TryPersistErrorMessagesAsync(
        Guid channelId,
        Guid? threadId,
        ChatRequest request,
        string errorMessage,
        bool userMessageAlreadyPersisted,
        IChatRequestWorkflowHost host,
        CancellationToken ct)
    {
        try
        {
            var toPersist = new List<ChatMessageState>(
                userMessageAlreadyPersisted ? 1 : 2);

            if (!userMessageAlreadyPersisted)
            {
                toPersist.Add(await CreateUserMessageAsync(
                    channelId,
                    threadId,
                    request,
                    host,
                    ct));
            }

            toPersist.Add(messages.CreateSystemErrorMessage(
                channelId,
                threadId,
                request,
                errorMessage));

            await host.PersistChatMessagesAsync(toPersist, ct);
            return new ChatMessagesPersistenceResult(true, toPersist, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new ChatMessagesPersistenceResult(false, [], ex);
        }
    }

    private async Task<ChatMessageState> CreateUserMessageAsync(
        Guid channelId,
        Guid? threadId,
        ChatRequest request,
        IChatRequestWorkflowHost host,
        CancellationToken ct)
    {
        var senderUserId = host.GetSessionUserId();
        var senderSnapshot = await host.LoadSenderSnapshotAsync(
            senderUserId,
            request.ExternalDisplayName,
            request.ExternalUsername,
            ct);

        return messages.CreateUserMessage(
            channelId,
            threadId,
            request,
            senderUserId,
            senderSnapshot.Username,
            senderSnapshot.RoleId,
            senderSnapshot.RoleName);
    }
}

public interface IChatRequestWorkflowHost
{
    Task<IDisposable?> BeginThreadProcessingAsync(
        Guid threadId,
        string clientType,
        CancellationToken ct);

    Task<ChatProviderHistoryResult> LoadProviderThreadHistoryAsync(
        Guid threadId,
        CancellationToken ct);

    Task<string?> BuildChatHeaderAsync(
        ChannelState channel,
        AgentState agent,
        ChatRequest request,
        ChatRequestPlan plan,
        CancellationToken ct);

    Guid? GetSessionUserId();

    Task<ChatSenderSnapshot> LoadSenderSnapshotAsync(
        Guid? senderUserId,
        string? externalDisplayName,
        string? externalUsername,
        CancellationToken ct);

    Task PersistChatMessagesAsync(
        IReadOnlyList<ChatMessageState> messages,
        CancellationToken ct);

    Task<bool> HasUserMessageAsync(
        Guid channelId,
        Guid? threadId,
        string content,
        CancellationToken ct);

    Task RecordTokensForCurrentExecutionAsync(
        int promptTokens,
        int completionTokens,
        CancellationToken ct);

    void RecordAssistantTokens(
        Guid channelId,
        Guid? threadId,
        Guid agentId,
        string agentName,
        int promptTokens,
        int completionTokens);

    void PublishNewMessages(Guid threadId, string clientType);

    Task<ChatResponseCostResult> GetResponseCostsAsync(
        Guid channelId,
        Guid? threadId,
        Guid agentId,
        string agentName,
        CancellationToken ct);
}

public sealed record ChatPreparedRequest(
    Guid ChannelId,
    Guid? ThreadId,
    ChannelState Channel,
    AgentState Agent,
    ChatRequestPlan Plan,
    ChatRequest ChatRequest);

public sealed class ChatPreparedRequestState : IDisposable
{
    private readonly IDisposable? _threadProcessing;

    public ChatPreparedRequestState(
        Guid channelId,
        Guid? threadId,
        ChannelState channel,
        AgentState agent,
        ChatRequestPlan plan,
        List<ChatCompletionMessage> history,
        ChatMessageState userMessage,
        int? maxHistoryMessages,
        int? maxHistoryCharacters,
        IDisposable? threadProcessing)
    {
        ChannelId = channelId;
        ThreadId = threadId;
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        History = history ?? throw new ArgumentNullException(nameof(history));
        UserMessage = userMessage
            ?? throw new ArgumentNullException(nameof(userMessage));
        MaxHistoryMessages = maxHistoryMessages;
        MaxHistoryCharacters = maxHistoryCharacters;
        _threadProcessing = threadProcessing;
    }

    public Guid ChannelId { get; }
    public Guid? ThreadId { get; }
    public ChannelState Channel { get; }
    public AgentState Agent { get; }
    public ChatRequestPlan Plan { get; }
    public List<ChatCompletionMessage> History { get; }
    public ChatMessageState UserMessage { get; }
    public int? MaxHistoryMessages { get; }
    public int? MaxHistoryCharacters { get; }

    public void Dispose() => _threadProcessing?.Dispose();
}

public sealed record ChatCompletedExchange(
    Guid ChannelId,
    Guid? ThreadId,
    ChatRequest ChatRequest,
    AgentState Agent,
    ChatMessageState UserMessage,
    string AssistantContent,
    IReadOnlyList<AgentJobResponse> JobResults,
    int TotalPromptTokens,
    int TotalCompletionTokens,
    string? ProviderMetadataJson);

public sealed record ChatExchangePersistenceResult(
    ChatResponse Response,
    ChatMessageState AssistantMessage,
    ChatResponseCostResult Costs);

public sealed record ChatPartialAssistantPersistenceRequest(
    Guid ChannelId,
    Guid? ThreadId,
    ChatRequest ChatRequest,
    AgentState Agent,
    string Content,
    int? TotalPromptTokens,
    int? TotalCompletionTokens,
    string? ProviderMetadataJson);

public sealed record ChatExceptionPersistenceRequest(
    Guid ChannelId,
    Guid? ThreadId,
    ChatRequest ChatRequest,
    Exception Exception,
    bool UserMessageAlreadyPersisted);

public sealed record ChatPublicErrorPersistenceRequest(
    Guid ChannelId,
    Guid? ThreadId,
    ChatRequest ChatRequest,
    string ErrorMessage);

public sealed record ChatSenderSnapshot(
    string? Username,
    Guid? RoleId,
    string? RoleName);

public sealed record ChatMessagePersistenceResult(
    bool Succeeded,
    ChatMessageState? Message,
    Exception? Exception);

public sealed record ChatMessagesPersistenceResult(
    bool Succeeded,
    IReadOnlyList<ChatMessageState> Messages,
    Exception? Exception);
