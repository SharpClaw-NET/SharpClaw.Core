using SharpClaw.Core.State;

namespace SharpClaw.Core.Tasks.Runtime;

/// <summary>
/// Store-neutral provisioning rules used by task-host bridge calls exposed to
/// modules.
/// </summary>
public sealed class TaskHostBridgeProvisioningEngine
{
    /// <summary>
    /// Creates a new task-scoped agent or updates the loaded matching agent.
    /// </summary>
    public AgentProvisioningResult ApplyAgentProvisioning(
        AgentState? existingAgent,
        string name,
        Guid modelId,
        string? systemPrompt,
        string? customId)
    {
        if (existingAgent is null)
        {
            return new AgentProvisioningResult(
                new AgentState
                {
                    Name = name,
                    ModelId = modelId,
                    SystemPrompt = systemPrompt,
                    CustomId = customId
                },
                Created: true);
        }

        existingAgent.Name = name;
        existingAgent.ModelId = modelId;
        existingAgent.SystemPrompt = systemPrompt;
        return new AgentProvisioningResult(existingAgent, Created: false);
    }

    /// <summary>
    /// Applies the update rules used when a bridge-created channel already
    /// exists by custom id or title.
    /// </summary>
    public void ApplyExistingChannelProvisioning(
        ChannelState channel,
        string title,
        Guid agentId,
        string? customId,
        Guid? contextId)
    {
        ArgumentNullException.ThrowIfNull(channel);

        channel.Title = title;
        channel.AgentId = agentId;

        if (!string.IsNullOrEmpty(customId))
            channel.CustomId = customId;

        if (contextId.HasValue)
            channel.AgentContextId = contextId;
    }

    /// <summary>
    /// Creates a task thread using the bridge's task-specific default name.
    /// </summary>
    public ChatThreadState CreateThread(
        Guid channelId,
        string? threadName,
        DateTimeOffset now)
        => new()
        {
            Name = threadName ?? BuildDefaultThreadName(now),
            ChannelId = channelId
        };

    /// <summary>Adds an allowed agent to a channel if it is not present.</summary>
    public bool AddChannelAllowedAgent(ChannelState channel, AgentState agent)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(agent);

        if (channel.AllowedAgents.Any(existing => existing.Id == agent.Id))
            return false;

        channel.AllowedAgents.Add(agent);
        return true;
    }

    /// <summary>
    /// Links a task instance to its first bridge-created channel.
    /// </summary>
    /// <summary>
    /// Resolves a channel id or throws the canonical bridge error.
    /// </summary>
    public Guid RequireInstanceChannel(Guid instanceId, Guid? channelId)
        => channelId
           ?? throw new InvalidOperationException(
               $"Task instance {instanceId} has no channel yet. " +
               "Call CreateChannel before using Chat, CreateThread, or other channel-dependent steps.");

    /// <summary>Builds the default thread name used by task bridge calls.</summary>
    public static string BuildDefaultThreadName(DateTimeOffset now)
        => $"Task Thread {now:HH:mm}";

    /// <summary>Builds the canonical CreateAgent task log message.</summary>
    public static string BuildCreateAgentLog(string name, Guid agentId)
        => $"CreateAgent '{name}' \u2192 {agentId}";

    /// <summary>Builds the canonical CreateThread task log message.</summary>
    public static string BuildCreateThreadLog(string name, Guid threadId)
        => $"CreateThread '{name}' \u2192 {threadId}";

    /// <summary>Builds the canonical CreateChannel task log message.</summary>
    public static string BuildCreateChannelLog(string title, Guid channelId)
        => $"CreateChannel '{title}' \u2192 {channelId}";

    /// <summary>Builds the canonical AddAllowedAgent task log message.</summary>
    public static string BuildAddAllowedAgentLog(Guid agentId, Guid channelId)
        => $"AddAllowedAgent agent={agentId} \u2192 channel={channelId}";
}

/// <summary>
/// Result of task bridge agent provisioning.
/// </summary>
public sealed record AgentProvisioningResult(AgentState Agent, bool Created);
