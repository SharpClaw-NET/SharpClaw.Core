using SharpClaw.Core.State;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.DTOs.Channels;
using SharpClaw.Contracts.DTOs.Contexts;
using SharpClaw.Contracts.DTOs.Threads;

namespace SharpClaw.Core.Conversation;

/// <summary>
/// Store-neutral conversation topology rules for channels, contexts, and
/// threads.
/// </summary>
public sealed class ConversationTopologyEngine
{
    /// <summary>Returns whether unique-name enforcement should be active.</summary>
    public static bool IsUniqueNameEnforced(string? configurationValue)
    {
        return configurationValue is null
            || !bool.TryParse(configurationValue, out var enforced)
            || enforced;
    }

    /// <summary>Throws when a channel title already exists.</summary>
    public void EnsureChannelTitleAvailable(
        string title,
        IEnumerable<string> existingTitles)
    {
        EnsureNameAvailable(
            title,
            existingTitles,
            name => $"A channel named '{name}' already exists.");
    }

    /// <summary>Throws when a context name already exists.</summary>
    public void EnsureContextNameAvailable(
        string name,
        IEnumerable<string> existingNames)
    {
        EnsureNameAvailable(
            name,
            existingNames,
            value => $"A context named '{value}' already exists.");
    }

    /// <summary>Creates a channel entity from a request and loaded references.</summary>
    public ChannelState CreateChannel(
        CreateChannelRequest request,
        AgentState? agent,
        ChannelContextState? context,
        IEnumerable<AgentState>? allowedAgents,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (agent is null && context is null)
            throw new ArgumentException(
                "Either an AgentId or a ContextId (with an agent) is required.");

        var channel = new ChannelState
        {
            Title = request.Title ?? BuildDefaultChannelTitle(now),
            AgentId = agent?.Id,
            Agent = agent,
            AgentContextId = context?.Id,
            AgentContext = context,
            PermissionSetId = request.PermissionSetId,
            DisableChatHeader = request.DisableChatHeader ?? false,
            CustomId = request.CustomId,
            ToolAwarenessSetId = request.ToolAwarenessSetId,
            DisableToolSchemas = request.DisableToolSchemas ?? false
        };

        ReplaceAllowedAgents(channel.AllowedAgents, allowedAgents);
        return channel;
    }

    /// <summary>Applies an update request to a loaded channel entity.</summary>
    public void ApplyChannelUpdate(
        ChannelState channel,
        UpdateChannelRequest request,
        ChannelContextState? context,
        IEnumerable<AgentState>? replacementAllowedAgents)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Title is not null)
            channel.Title = request.Title;

        if (request.ContextId is not null)
        {
            if (request.ContextId == Guid.Empty)
            {
                channel.AgentContextId = null;
                channel.AgentContext = null;
            }
            else
            {
                if (context is null)
                    throw new ArgumentException(
                        $"Context {request.ContextId} not found.");

                channel.AgentContextId = context.Id;
                channel.AgentContext = context;
            }
        }

        if (request.PermissionSetId is not null)
            channel.PermissionSetId = request.PermissionSetId == Guid.Empty
                ? null
                : request.PermissionSetId;

        if (request.AllowedAgentIds is not null)
            ReplaceAllowedAgents(channel.AllowedAgents, replacementAllowedAgents);

        if (request.DisableChatHeader is not null)
            channel.DisableChatHeader = request.DisableChatHeader.Value;

        if (request.CustomId is not null)
            channel.CustomId = request.CustomId;

        if (request.CustomChatHeader is not null)
            channel.CustomChatHeader = request.CustomChatHeader.Length > 0
                ? request.CustomChatHeader
                : null;

        if (request.ToolAwarenessSetId is not null)
            channel.ToolAwarenessSetId = request.ToolAwarenessSetId == Guid.Empty
                ? null
                : request.ToolAwarenessSetId;

        if (request.DisableToolSchemas is not null)
            channel.DisableToolSchemas = request.DisableToolSchemas.Value;
    }

    /// <summary>Sets the default agent for a channel.</summary>
    public void SetChannelAgent(ChannelState channel, AgentState agent)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(agent);

        channel.AgentId = agent.Id;
        channel.Agent = agent;
    }

    /// <summary>
    /// Resolves the agent for a channel operation with an optional override.
    /// </summary>
    public AgentState ResolveRequestedAgent(
        ChannelState channel,
        Guid? requestedAgentId)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var defaultAgent = ResolveEffectiveAgent(channel);

        if (requestedAgentId is null || requestedAgentId == defaultAgent?.Id)
            return defaultAgent
                ?? throw new InvalidOperationException(
                    $"Channel {channel.Id} has no agent and no context agent.");

        var allowed = ResolveEffectiveAllowedAgents(channel)
            .FirstOrDefault(agent => agent.Id == requestedAgentId);

        return allowed
            ?? throw new InvalidOperationException(
                $"Agent {requestedAgentId} is not allowed on channel {channel.Id}. " +
                "Add it to the channel's or context's allowed agents first.");
    }

    /// <summary>Adds an allowed agent if it is not already present.</summary>
    public bool AddChannelAllowedAgent(ChannelState channel, AgentState agent)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(agent);

        if (channel.AllowedAgents.Any(existing => existing.Id == agent.Id))
            return false;

        channel.AllowedAgents.Add(agent);
        return true;
    }

    /// <summary>Removes a channel allowed agent when present.</summary>
    public bool RemoveChannelAllowedAgent(ChannelState channel, Guid agentId)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var agent = channel.AllowedAgents.FirstOrDefault(a => a.Id == agentId);
        if (agent is null)
            return false;

        channel.AllowedAgents.Remove(agent);
        return true;
    }

    /// <summary>Projects a loaded channel entity to its response shape.</summary>
    public ChannelResponse ToChannelResponse(ChannelState channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var effectiveAgent = ResolveEffectiveAgent(channel);
        var effectiveAllowed = ResolveEffectiveAllowedAgents(channel);

        return new ChannelResponse(
            channel.Id,
            channel.Title,
            effectiveAgent is not null ? ToAgentSummary(effectiveAgent) : null,
            channel.AgentContext?.Id,
            channel.AgentContext?.Name,
            channel.PermissionSetId,
            ResolveEffectivePermissionSetId(channel),
            effectiveAllowed.Select(ToAgentSummary).ToList(),
            channel.DisableChatHeader,
            channel.CreatedAt,
            channel.UpdatedAt,
            channel.CustomId,
            channel.CustomChatHeader,
            channel.ToolAwarenessSetId,
            channel.DisableToolSchemas);
    }

    /// <summary>Projects channel allowed-agent state to its response shape.</summary>
    public ChannelAllowedAgentsResponse ToChannelAllowedAgentsResponse(ChannelState channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var effectiveAgent = ResolveEffectiveAgent(channel);
        var effectiveAllowed = ResolveEffectiveAllowedAgents(channel);

        return new ChannelAllowedAgentsResponse(
            channel.Id,
            effectiveAgent is not null ? ToAgentSummary(effectiveAgent) : null,
            effectiveAllowed.Select(ToAgentSummary).ToList());
    }

    /// <summary>Creates a context entity from a request and loaded references.</summary>
    public ChannelContextState CreateContext(
        CreateContextRequest request,
        AgentState agent,
        IEnumerable<AgentState>? allowedAgents,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(agent);

        var context = new ChannelContextState
        {
            Name = request.Name ?? BuildDefaultContextName(now),
            AgentId = agent.Id,
            Agent = agent,
            PermissionSetId = request.PermissionSetId,
            DisableChatHeader = request.DisableChatHeader ?? false
        };

        ReplaceAllowedAgents(context.AllowedAgents, allowedAgents);
        return context;
    }

    /// <summary>Applies an update request to a loaded context entity.</summary>
    public void ApplyContextUpdate(
        ChannelContextState context,
        UpdateContextRequest request,
        IEnumerable<AgentState>? replacementAllowedAgents)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Name is not null)
            context.Name = request.Name;

        if (request.PermissionSetId is not null)
            context.PermissionSetId = request.PermissionSetId == Guid.Empty
                ? null
                : request.PermissionSetId;

        if (request.DisableChatHeader is not null)
            context.DisableChatHeader = request.DisableChatHeader.Value;

        if (request.AllowedAgentIds is not null)
            ReplaceAllowedAgents(context.AllowedAgents, replacementAllowedAgents);
    }

    /// <summary>Adds an allowed agent to a context if not already present.</summary>
    public bool AddContextAllowedAgent(ChannelContextState context, AgentState agent)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(agent);

        if (context.AllowedAgents.Any(existing => existing.Id == agent.Id))
            return false;

        context.AllowedAgents.Add(agent);
        return true;
    }

    /// <summary>Removes a context allowed agent when present.</summary>
    public bool RemoveContextAllowedAgent(ChannelContextState context, Guid agentId)
    {
        ArgumentNullException.ThrowIfNull(context);

        var agent = context.AllowedAgents.FirstOrDefault(a => a.Id == agentId);
        if (agent is null)
            return false;

        context.AllowedAgents.Remove(agent);
        return true;
    }

    /// <summary>Projects a loaded context entity to its response shape.</summary>
    public ContextResponse ToContextResponse(ChannelContextState context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new ContextResponse(
            context.Id,
            context.Name,
            ToAgentSummary(context.Agent),
            context.PermissionSetId,
            context.DisableChatHeader,
            context.AllowedAgents.Select(ToAgentSummary).ToList(),
            context.CreatedAt,
            context.UpdatedAt);
    }

    /// <summary>Projects context allowed-agent state to its response shape.</summary>
    public ContextAllowedAgentsResponse ToContextAllowedAgentsResponse(
        ChannelContextState context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new ContextAllowedAgentsResponse(
            context.Id,
            ToAgentSummary(context.Agent),
            context.AllowedAgents.Select(ToAgentSummary).ToList());
    }

    /// <summary>Creates a thread entity from a request and loaded channel id.</summary>
    public ChatThreadState CreateThread(
        Guid channelId,
        CreateThreadRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ChatThreadState
        {
            Name = request.Name ?? BuildDefaultThreadName(now),
            MaxMessages = request.MaxMessages,
            MaxCharacters = request.MaxCharacters,
            ChannelId = channelId,
            CustomId = request.CustomId
        };
    }

    /// <summary>Applies an update request to a loaded thread entity.</summary>
    public void ApplyThreadUpdate(ChatThreadState thread, UpdateThreadRequest request)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Name is not null)
            thread.Name = request.Name;

        if (request.MaxMessages is not null)
            thread.MaxMessages = request.MaxMessages.Value == 0
                ? null
                : request.MaxMessages;

        if (request.MaxCharacters is not null)
            thread.MaxCharacters = request.MaxCharacters.Value == 0
                ? null
                : request.MaxCharacters;

        if (request.CustomId is not null)
            thread.CustomId = request.CustomId;
    }

    /// <summary>Projects a loaded thread entity to its response shape.</summary>
    public ThreadResponse ToThreadResponse(ChatThreadState thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        return new ThreadResponse(
            thread.Id,
            thread.Name,
            thread.ChannelId,
            thread.MaxMessages,
            thread.MaxCharacters,
            thread.CreatedAt,
            thread.UpdatedAt,
            thread.CustomId);
    }

    /// <summary>Projects a loaded agent entity to its compact response shape.</summary>
    public AgentSummary ToAgentSummary(AgentState agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return new AgentSummary(
            agent.Id,
            agent.Name,
            agent.ModelId,
            agent.Model?.Name ?? "unknown",
            agent.Model?.Provider?.Name ?? "unknown",
            agent.RoleId,
            agent.Role?.Name,
            agent.MaxCompletionTokens,
            agent.CustomId,
            agent.Temperature,
            agent.TopP,
            agent.TopK,
            agent.FrequencyPenalty,
            agent.PresencePenalty,
            agent.Stop,
            agent.Seed,
            agent.ResponseFormat,
            agent.ReasoningEffort,
            agent.ProviderParameters,
            agent.CustomChatHeader,
            agent.ToolAwarenessSetId,
            agent.DisableToolSchemas);
    }

    /// <summary>Builds the default channel title for the supplied instant.</summary>
    public static string BuildDefaultChannelTitle(DateTimeOffset now) =>
        $"Channel {now:yyyy-MM-dd HH:mm}";

    /// <summary>Builds the default context name for the supplied instant.</summary>
    public static string BuildDefaultContextName(DateTimeOffset now) =>
        $"Context {now:yyyy-MM-dd HH:mm}";

    /// <summary>Builds the default thread name for the supplied instant.</summary>
    public static string BuildDefaultThreadName(DateTimeOffset now) =>
        $"Thread {now:yyyy-MM-dd HH:mm}";

    private static Guid? ResolveEffectivePermissionSetId(ChannelState channel) =>
        channel.PermissionSetId ?? channel.AgentContext?.PermissionSetId;

    private static AgentState? ResolveEffectiveAgent(ChannelState channel) =>
        channel.Agent ?? channel.AgentContext?.Agent;

    private static IEnumerable<AgentState> ResolveEffectiveAllowedAgents(
        ChannelState channel)
    {
        return channel.AllowedAgents.Count > 0
            ? channel.AllowedAgents
            : channel.AgentContext?.AllowedAgents ?? [];
    }

    private static void ReplaceAllowedAgents(
        ICollection<AgentState> target,
        IEnumerable<AgentState>? replacement)
    {
        target.Clear();

        if (replacement is null)
            return;

        foreach (var agent in replacement)
            target.Add(agent);
    }

    private static void EnsureNameAvailable(
        string name,
        IEnumerable<string> existingNames,
        Func<string, string> buildMessage)
    {
        ArgumentNullException.ThrowIfNull(existingNames);

        var normalized = name.Trim();
        if (existingNames.Any(existing =>
                existing.Trim().Equals(
                    normalized,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(buildMessage(name));
        }
    }
}
