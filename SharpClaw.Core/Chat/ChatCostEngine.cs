using SharpClaw.Core.State;
using SharpClaw.Contracts.DTOs.Chat;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Store-neutral token accounting projections for chat messages.
/// Hosts own message retrieval and cache storage; Core owns aggregation and
/// response semantics.
/// </summary>
public sealed class ChatCostEngine
{
    /// <summary>Builds aggregate token usage for one channel.</summary>
    public ChannelCostResponse BuildChannelCost(
        Guid channelId,
        IEnumerable<ChatMessageState> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var breakdown = BuildAgentBreakdown(messages);
        var totalPrompt = breakdown.Sum(static item => item.PromptTokens);
        var totalCompletion = breakdown.Sum(static item => item.CompletionTokens);

        return new ChannelCostResponse(
            channelId,
            totalPrompt,
            totalCompletion,
            totalPrompt + totalCompletion,
            breakdown);
    }

    /// <summary>Builds aggregate token usage for one thread.</summary>
    public ThreadCostResponse BuildThreadCost(
        Guid channelId,
        Guid threadId,
        IEnumerable<ChatMessageState> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var breakdown = BuildAgentBreakdown(messages);
        var totalPrompt = breakdown.Sum(static item => item.PromptTokens);
        var totalCompletion = breakdown.Sum(static item => item.CompletionTokens);

        return new ThreadCostResponse(
            threadId,
            channelId,
            totalPrompt,
            totalCompletion,
            totalPrompt + totalCompletion,
            breakdown);
    }

    /// <summary>
    /// Builds aggregate token usage for one agent across all channels.
    /// </summary>
    public AgentCostResponse BuildAgentCost(
        Guid agentId,
        string agentName,
        IEnumerable<ChatMessageState> messages)
    {
        ArgumentNullException.ThrowIfNull(agentName);
        ArgumentNullException.ThrowIfNull(messages);

        var channelBreakdown = messages
            .Where(static message => message.PromptTokens is not null)
            .GroupBy(static message => message.ChannelId)
            .Select(static group =>
            {
                var promptTokens = group.Sum(
                    static message => message.PromptTokens!.Value);
                var completionTokens = group.Sum(
                    static message => message.CompletionTokens ?? 0);

                return new AgentChannelTokenBreakdown(
                    group.Key,
                    promptTokens,
                    completionTokens,
                    promptTokens + completionTokens);
            })
            .OrderByDescending(static breakdown => breakdown.TotalTokens)
            .ToList();

        var totalPrompt = channelBreakdown.Sum(static item => item.PromptTokens);
        var totalCompletion = channelBreakdown.Sum(
            static item => item.CompletionTokens);

        return new AgentCostResponse(
            agentId,
            agentName,
            totalPrompt,
            totalCompletion,
            totalPrompt + totalCompletion,
            channelBreakdown);
    }

    /// <summary>
    /// Builds per-agent token usage for channel and thread aggregates.
    /// </summary>
    public List<AgentTokenBreakdown> BuildAgentBreakdown(
        IEnumerable<ChatMessageState> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return messages
            .Where(static message =>
                message.PromptTokens is not null
                && message.SenderAgentId.HasValue)
            .GroupBy(static message => new
            {
                message.SenderAgentId,
                message.SenderAgentName
            })
            .Select(static group =>
            {
                var promptTokens = group.Sum(
                    static message => message.PromptTokens!.Value);
                var completionTokens = group.Sum(
                    static message => message.CompletionTokens ?? 0);

                return new AgentTokenBreakdown(
                    group.Key.SenderAgentId!.Value,
                    group.Key.SenderAgentName ?? "Unknown",
                    promptTokens,
                    completionTokens,
                    promptTokens + completionTokens);
            })
            .OrderByDescending(static breakdown => breakdown.TotalTokens)
            .ToList();
    }
}
