using SharpClaw.Core.State;
using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Chat;

public sealed class ChatQueryWorkflowEngine(
    ChatMessageEngine messages,
    ChatHistoryEngine history,
    ChatCostEngine costs)
{
    public ChatQueryWorkflowEngine()
        : this(new ChatMessageEngine(), new ChatHistoryEngine(), new ChatCostEngine())
    {
    }

    public async Task<IReadOnlyList<ChatMessageResponse>> GetHistoryAsync(
        Guid channelId,
        Guid? threadId,
        int limit,
        IChatQueryHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (limit <= 0)
            return [];

        var rows = await host.ListHistoryMessagesAsync(
            channelId,
            threadId,
            limit,
            ct);
        return messages.ToOrderedHistoryResponses(rows);
    }

    public async Task<ChatProviderHistoryResult> GetProviderThreadHistoryAsync(
        Guid threadId,
        IChatQueryHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var limits = await host.GetOrCreateThreadHistoryLimitsAsync(
            threadId,
            innerCt => LoadThreadHistoryLimitsAsync(threadId, host, innerCt),
            ct);
        var resolvedLimits = limits ?? history.ResolveLimits(null, null);
        var rows = await host.ListThreadHistoryMessagesAsync(
            threadId,
            resolvedLimits.MaxMessages,
            ct);
        var providerHistory = history.BuildProviderHistory(
                rows.Select(static row => new ChatHistoryMessage(
                    row.CreatedAt,
                    row.Role,
                    row.Content,
                    row.ProviderMetadataJson)),
                resolvedLimits)
            .ToList();

        return new ChatProviderHistoryResult(
            providerHistory,
            resolvedLimits.MaxMessages,
            resolvedLimits.MaxCharacters);
    }

    public async Task<ChannelCostResponse> GetChannelCostAsync(
        Guid channelId,
        IChatQueryHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        return await host.GetOrCreateChannelCostAsync(
            channelId,
            async innerCt =>
            {
                var rows = await host.ListChannelCostMessagesAsync(
                    channelId,
                    innerCt);
                return costs.BuildChannelCost(channelId, rows);
            },
            ct);
    }

    public async Task<ThreadCostResponse?> GetThreadCostAsync(
        Guid channelId,
        Guid threadId,
        IChatQueryHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        return await host.GetOrCreateThreadCostAsync(
            channelId,
            threadId,
            async innerCt =>
            {
                if (!await host.ThreadBelongsToChannelAsync(
                        channelId,
                        threadId,
                        innerCt))
                {
                    return null;
                }

                var rows = await host.ListThreadCostMessagesAsync(
                    threadId,
                    innerCt);
                return costs.BuildThreadCost(channelId, threadId, rows);
            },
            ct);
    }

    public async Task<AgentCostResponse?> GetAgentCostAsync(
        Guid agentId,
        IChatQueryHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var agentName = await host.LoadAgentNameAsync(agentId, ct);
        return agentName is null
            ? null
            : await GetKnownAgentCostAsync(agentId, agentName, host, ct);
    }

    public async Task<AgentCostResponse?> GetKnownAgentCostAsync(
        Guid agentId,
        string agentName,
        IChatQueryHost host,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(host);

        return await host.GetOrCreateAgentCostAsync(
            agentId,
            async innerCt =>
            {
                var rows = await host.ListAgentCostMessagesAsync(
                    agentId,
                    innerCt);
                return costs.BuildAgentCost(agentId, agentName, rows);
            },
            ct);
    }

    public async Task<ChatResponseCostResult> GetResponseCostsAsync(
        Guid channelId,
        Guid? threadId,
        Guid agentId,
        string agentName,
        IChatQueryHost host,
        CancellationToken ct = default)
    {
        var channelCost = await GetChannelCostAsync(channelId, host, ct);
        var threadCost = threadId is { } concreteThreadId
            ? await GetThreadCostAsync(channelId, concreteThreadId, host, ct)
            : null;
        var agentCost = await GetKnownAgentCostAsync(agentId, agentName, host, ct);

        return new ChatResponseCostResult(channelCost, threadCost, agentCost);
    }

    private async Task<ChatHistoryLimits> LoadThreadHistoryLimitsAsync(
        Guid threadId,
        IChatQueryHost host,
        CancellationToken ct)
    {
        var limits = await host.LoadThreadHistoryLimitValuesAsync(threadId, ct);
        return history.ResolveLimits(
            limits?.MaxMessages,
            limits?.MaxCharacters);
    }
}

public interface IChatQueryHost
{
    Task<IReadOnlyList<ChatMessageState>> ListHistoryMessagesAsync(
        Guid channelId,
        Guid? threadId,
        int limit,
        CancellationToken ct);

    Task<ChatThreadHistoryLimitValues?> LoadThreadHistoryLimitValuesAsync(
        Guid threadId,
        CancellationToken ct);

    Task<ChatHistoryLimits?> GetOrCreateThreadHistoryLimitsAsync(
        Guid threadId,
        Func<CancellationToken, Task<ChatHistoryLimits>> loader,
        CancellationToken ct);

    Task<IReadOnlyList<ChatMessageState>> ListThreadHistoryMessagesAsync(
        Guid threadId,
        int limit,
        CancellationToken ct);

    Task<ChannelCostResponse> GetOrCreateChannelCostAsync(
        Guid channelId,
        Func<CancellationToken, Task<ChannelCostResponse>> loader,
        CancellationToken ct);

    Task<ThreadCostResponse?> GetOrCreateThreadCostAsync(
        Guid channelId,
        Guid threadId,
        Func<CancellationToken, Task<ThreadCostResponse?>> loader,
        CancellationToken ct);

    Task<AgentCostResponse?> GetOrCreateAgentCostAsync(
        Guid agentId,
        Func<CancellationToken, Task<AgentCostResponse?>> loader,
        CancellationToken ct);

    Task<IReadOnlyList<ChatMessageState>> ListChannelCostMessagesAsync(
        Guid channelId,
        CancellationToken ct);

    Task<bool> ThreadBelongsToChannelAsync(
        Guid channelId,
        Guid threadId,
        CancellationToken ct);

    Task<IReadOnlyList<ChatMessageState>> ListThreadCostMessagesAsync(
        Guid threadId,
        CancellationToken ct);

    Task<string?> LoadAgentNameAsync(Guid agentId, CancellationToken ct);

    Task<IReadOnlyList<ChatMessageState>> ListAgentCostMessagesAsync(
        Guid agentId,
        CancellationToken ct);
}

public sealed record ChatProviderHistoryResult(
    IReadOnlyList<ChatCompletionMessage> Messages,
    int MaxMessages,
    int MaxCharacters);

public sealed record ChatThreadHistoryLimitValues(
    int? MaxMessages,
    int? MaxCharacters);

public sealed record ChatResponseCostResult(
    ChannelCostResponse ChannelCost,
    ThreadCostResponse? ThreadCost,
    AgentCostResponse? AgentCost);
