using SharpClaw.Core.State;
using SharpClaw.Contracts.DTOs.Channels;
using SharpClaw.Contracts.DTOs.Contexts;
using SharpClaw.Contracts.DTOs.Threads;
using SharpClaw.Core.Chat;

namespace SharpClaw.Core.Conversation;

/// <summary>
/// Store-neutral CRUD orchestration for channels, contexts, and threads.
/// Hosts provide persistence and cache application; Core owns the operation
/// order, validation rules, mutation semantics, and invalidation decisions.
/// </summary>
public sealed class ConversationAdministrationEngine(
    ConversationTopologyEngine conversation,
    ChatRuntimeInvalidationPlanner invalidations)
{
    public async Task<ChannelResponse?> GetChannelAsync(
        Guid channelId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var channel = await host.LoadChannelAsync(channelId, ct);
        return channel is not null
            ? conversation.ToChannelResponse(channel)
            : null;
    }

    public async Task<IReadOnlyList<ChannelResponse>> ListChannelsAsync(
        Guid? agentId,
        Guid? contextId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var channels = await host.ListChannelsAsync(agentId, contextId, ct);
        return channels
            .OrderByDescending(channel => channel.UpdatedAt)
            .Select(conversation.ToChannelResponse)
            .ToList();
    }

    public async Task<ChannelResponse?> GetLatestActiveChannelAsync(
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        ChannelState? channel = null;
        var latestChannelId = await host.LoadLatestMessageChannelIdAsync(ct);
        if (latestChannelId is not null)
            channel = await host.LoadChannelAsync(latestChannelId.Value, ct);

        channel ??= await host.LoadMostRecentlyCreatedChannelAsync(ct);
        return channel is not null
            ? conversation.ToChannelResponse(channel)
            : null;
    }

    public async Task<ChannelAllowedAgentsResponse?> GetChannelAllowedAgentsAsync(
        Guid channelId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var channel = await host.LoadChannelAsync(channelId, ct);
        return channel is not null
            ? conversation.ToChannelAllowedAgentsResponse(channel)
            : null;
    }

    public async Task<ChannelResponse> CreateChannelAsync(
        CreateChannelRequest request,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        AgentState? agent = null;
        if (request.AgentId is { } agentId)
            agent = await host.LoadAgentAsync(agentId, ct)
                ?? throw new ArgumentException($"Agent {agentId} not found.");

        ChannelContextState? context = null;
        if (request.ContextId is { } contextId)
            context = await host.LoadContextAsync(contextId, ct)
                ?? throw new ArgumentException($"Context {contextId} not found.");

        var now = DateTimeOffset.UtcNow;
        var title = request.Title
            ?? ConversationTopologyEngine.BuildDefaultChannelTitle(now);

        if (host.UniqueChannelNamesEnforced)
            await EnsureChannelTitleUniqueAsync(title, null, host, ct);

        var allowedAgents = await LoadOptionalAgentsAsync(
            request.AllowedAgentIds,
            host,
            ct);

        var channel = conversation.CreateChannel(
            request with { Title = title },
            agent,
            context,
            allowedAgents,
            now);

        host.TrackChannel(channel);
        await host.SaveAsync(null, ct);
        return conversation.ToChannelResponse(channel);
    }

    public async Task<ChannelResponse?> UpdateChannelAsync(
        Guid channelId,
        UpdateChannelRequest request,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var channel = await host.LoadChannelAsync(channelId, ct);
        if (channel is null)
            return null;

        if (request.Title is not null
            && host.UniqueChannelNamesEnforced
            && !request.Title.Trim().Equals(
                channel.Title.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            await EnsureChannelTitleUniqueAsync(request.Title, channelId, host, ct);
        }

        ChannelContextState? context = null;
        if (request.ContextId is not null && request.ContextId != Guid.Empty)
            context = await host.LoadContextAsync(request.ContextId.Value, ct)
                ?? throw new ArgumentException(
                    $"Context {request.ContextId} not found.");

        var allowedAgents = await LoadOptionalAgentsAsync(
            request.AllowedAgentIds,
            host,
            ct);

        conversation.ApplyChannelUpdate(
            channel,
            request,
            context,
            allowedAgents);

        await host.SaveAsync(
            () => invalidations.ChannelChanged(channelId),
            ct);
        return conversation.ToChannelResponse(channel);
    }

    public async Task<bool> DeleteChannelAsync(
        Guid channelId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var channel = await host.LoadChannelAsync(channelId, ct);
        if (channel is null)
            return false;

        host.RemoveChannel(channel);
        await host.SaveAsync(
            () => invalidations.ChannelChanged(channelId),
            ct);
        return true;
    }

    public async Task<ChannelResponse?> SetChannelAgentAsync(
        Guid channelId,
        Guid agentId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var channel = await host.LoadChannelAsync(channelId, ct);
        if (channel is null)
            return null;

        var agent = await host.LoadAgentAsync(agentId, ct)
            ?? throw new ArgumentException($"Agent {agentId} not found.");

        conversation.SetChannelAgent(channel, agent);
        await host.SaveAsync(
            () => invalidations.ChannelChanged(channelId),
            ct);
        return conversation.ToChannelResponse(channel);
    }

    public async Task<ChannelAllowedAgentsResponse?> AddChannelAllowedAgentAsync(
        Guid channelId,
        Guid agentId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var channel = await host.LoadChannelAsync(channelId, ct);
        if (channel is null)
            return null;

        var agent = await host.LoadAgentAsync(agentId, ct)
            ?? throw new ArgumentException($"Agent {agentId} not found.");

        if (conversation.AddChannelAllowedAgent(channel, agent))
        {
            await host.SaveAsync(
                () => invalidations.ChannelChanged(channelId),
                ct);
        }

        return conversation.ToChannelAllowedAgentsResponse(channel);
    }

    public async Task<ChannelAllowedAgentsResponse?> RemoveChannelAllowedAgentAsync(
        Guid channelId,
        Guid agentId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var channel = await host.LoadChannelAsync(channelId, ct);
        if (channel is null)
            return null;

        if (conversation.RemoveChannelAllowedAgent(channel, agentId))
        {
            await host.SaveAsync(
                () => invalidations.ChannelChanged(channelId),
                ct);
        }

        return conversation.ToChannelAllowedAgentsResponse(channel);
    }

    public async Task<ContextResponse?> GetContextAsync(
        Guid contextId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var context = await host.LoadContextAsync(contextId, ct);
        return context is not null
            ? conversation.ToContextResponse(context)
            : null;
    }

    public async Task<IReadOnlyList<ContextResponse>> ListContextsAsync(
        Guid? agentId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var contexts = await host.ListContextsAsync(agentId, ct);
        return contexts
            .OrderByDescending(context => context.UpdatedAt)
            .Select(conversation.ToContextResponse)
            .ToList();
    }

    public async Task<ContextAllowedAgentsResponse?> GetContextAllowedAgentsAsync(
        Guid contextId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var context = await host.LoadContextAsync(contextId, ct);
        return context is not null
            ? conversation.ToContextAllowedAgentsResponse(context)
            : null;
    }

    public async Task<ContextResponse> CreateContextAsync(
        CreateContextRequest request,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        if (request.Name is not null && host.UniqueContextNamesEnforced)
            await EnsureContextNameUniqueAsync(request.Name, null, host, ct);

        var agent = await host.LoadAgentAsync(request.AgentId, ct)
            ?? throw new ArgumentException($"Agent {request.AgentId} not found.");

        var allowedAgents = await LoadOptionalAgentsAsync(
            request.AllowedAgentIds,
            host,
            ct);

        var context = conversation.CreateContext(
            request,
            agent,
            allowedAgents,
            DateTimeOffset.UtcNow);

        host.TrackContext(context);
        await host.SaveAsync(null, ct);
        return conversation.ToContextResponse(context);
    }

    public async Task<ContextResponse?> UpdateContextAsync(
        Guid contextId,
        UpdateContextRequest request,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var context = await host.LoadContextAsync(contextId, ct);
        if (context is null)
            return null;

        if (request.Name is not null
            && host.UniqueContextNamesEnforced
            && !request.Name.Trim().Equals(
                context.Name.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            await EnsureContextNameUniqueAsync(request.Name, contextId, host, ct);
        }

        var allowedAgents = await LoadOptionalAgentsAsync(
            request.AllowedAgentIds,
            host,
            ct);

        conversation.ApplyContextUpdate(context, request, allowedAgents);
        await SaveContextChangeAsync(contextId, host, ct);
        return conversation.ToContextResponse(context);
    }

    public async Task<bool> DeleteContextAsync(
        Guid contextId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var context = await host.LoadContextAsync(contextId, ct);
        if (context is null)
            return false;

        var channelIds = await host.ListChannelIdsForContextAsync(contextId, ct);
        host.RemoveContext(context);
        await host.SaveAsync(
            () => invalidations.ContextChanged(channelIds),
            ct);
        return true;
    }

    public async Task<ContextAllowedAgentsResponse?> AddContextAllowedAgentAsync(
        Guid contextId,
        Guid agentId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var context = await host.LoadContextAsync(contextId, ct);
        if (context is null)
            return null;

        var agent = await host.LoadAgentAsync(agentId, ct)
            ?? throw new ArgumentException($"Agent {agentId} not found.");

        if (conversation.AddContextAllowedAgent(context, agent))
            await SaveContextChangeAsync(contextId, host, ct);

        return conversation.ToContextAllowedAgentsResponse(context);
    }

    public async Task<ContextAllowedAgentsResponse?> RemoveContextAllowedAgentAsync(
        Guid contextId,
        Guid agentId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var context = await host.LoadContextAsync(contextId, ct);
        if (context is null)
            return null;

        if (conversation.RemoveContextAllowedAgent(context, agentId))
            await SaveContextChangeAsync(contextId, host, ct);

        return conversation.ToContextAllowedAgentsResponse(context);
    }

    public async Task<ThreadResponse> CreateThreadAsync(
        Guid channelId,
        CreateThreadRequest request,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        if (!await host.ChannelExistsAsync(channelId, ct))
            throw new ArgumentException($"Channel {channelId} not found.");

        var thread = conversation.CreateThread(
            channelId,
            request,
            DateTimeOffset.UtcNow);

        host.TrackThread(thread);
        await host.SaveAsync(
            () => invalidations.ThreadChanged(thread.Id),
            ct);
        return conversation.ToThreadResponse(thread);
    }

    public async Task<ThreadResponse?> GetThreadAsync(
        Guid threadId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var thread = await host.LoadThreadAsync(threadId, ct);
        return thread is not null
            ? conversation.ToThreadResponse(thread)
            : null;
    }

    public async Task<IReadOnlyList<ThreadResponse>> ListThreadsAsync(
        Guid channelId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var threads = await host.ListThreadsAsync(channelId, ct);
        return threads
            .OrderByDescending(thread => thread.UpdatedAt)
            .Select(conversation.ToThreadResponse)
            .ToList();
    }

    public async Task<ThreadResponse?> UpdateThreadAsync(
        Guid threadId,
        UpdateThreadRequest request,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var thread = await host.LoadThreadAsync(threadId, ct);
        if (thread is null)
            return null;

        conversation.ApplyThreadUpdate(thread, request);
        await host.SaveAsync(
            () => invalidations.ThreadChanged(threadId),
            ct);
        return conversation.ToThreadResponse(thread);
    }

    public async Task<bool> DeleteThreadAsync(
        Guid threadId,
        IConversationAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var thread = await host.LoadThreadAsync(threadId, ct);
        if (thread is null)
            return false;

        host.RemoveThread(thread);
        await host.SaveAsync(
            () => invalidations.ThreadChanged(threadId),
            ct);
        return true;
    }

    private async Task SaveContextChangeAsync(
        Guid contextId,
        IConversationAdministrationHost host,
        CancellationToken ct)
    {
        var channelIds = await host.ListChannelIdsForContextAsync(contextId, ct);
        await host.SaveAsync(
            () => invalidations.ContextChanged(channelIds),
            ct);
    }

    private async Task EnsureChannelTitleUniqueAsync(
        string title,
        Guid? excludeId,
        IConversationAdministrationHost host,
        CancellationToken ct)
    {
        var titles = await host.ListChannelTitlesAsync(excludeId, ct);
        conversation.EnsureChannelTitleAvailable(title, titles);
    }

    private async Task EnsureContextNameUniqueAsync(
        string name,
        Guid? excludeId,
        IConversationAdministrationHost host,
        CancellationToken ct)
    {
        var names = await host.ListContextNamesAsync(excludeId, ct);
        conversation.EnsureContextNameAvailable(name, names);
    }

    private static async Task<IReadOnlyList<AgentState>?> LoadOptionalAgentsAsync(
        IReadOnlyList<Guid>? agentIds,
        IConversationAdministrationHost host,
        CancellationToken ct)
    {
        if (agentIds is null)
            return null;

        return agentIds.Count > 0
            ? await host.LoadAgentsAsync(agentIds, ct)
            : [];
    }
}

/// <summary>
/// Persistence and cache boundary used by conversation administration.
/// </summary>
public interface IConversationAdministrationHost
{
    bool UniqueChannelNamesEnforced { get; }

    bool UniqueContextNamesEnforced { get; }

    Task<AgentState?> LoadAgentAsync(Guid agentId, CancellationToken ct);

    Task<IReadOnlyList<AgentState>> LoadAgentsAsync(
        IReadOnlyCollection<Guid> agentIds,
        CancellationToken ct);

    Task<ChannelState?> LoadChannelAsync(Guid channelId, CancellationToken ct);

    Task<ChannelContextState?> LoadContextAsync(
        Guid contextId,
        CancellationToken ct);

    Task<ChatThreadState?> LoadThreadAsync(Guid threadId, CancellationToken ct);

    Task<bool> ChannelExistsAsync(Guid channelId, CancellationToken ct);

    Task<IReadOnlyList<ChannelState>> ListChannelsAsync(
        Guid? agentId,
        Guid? contextId,
        CancellationToken ct);

    Task<Guid?> LoadLatestMessageChannelIdAsync(CancellationToken ct);

    Task<ChannelState?> LoadMostRecentlyCreatedChannelAsync(CancellationToken ct);

    Task<IReadOnlyList<ChannelContextState>> ListContextsAsync(
        Guid? agentId,
        CancellationToken ct);

    Task<IReadOnlyList<ChatThreadState>> ListThreadsAsync(
        Guid channelId,
        CancellationToken ct);

    Task<IReadOnlyList<string>> ListChannelTitlesAsync(
        Guid? excludeId,
        CancellationToken ct);

    Task<IReadOnlyList<string>> ListContextNamesAsync(
        Guid? excludeId,
        CancellationToken ct);

    Task<IReadOnlyList<Guid>> ListChannelIdsForContextAsync(
        Guid contextId,
        CancellationToken ct);

    void TrackChannel(ChannelState channel);

    void TrackContext(ChannelContextState context);

    void TrackThread(ChatThreadState thread);

    void RemoveChannel(ChannelState channel);

    void RemoveContext(ChannelContextState context);

    void RemoveThread(ChatThreadState thread);

    Task SaveAsync(
        Func<ChatRuntimeInvalidationPlan?>? buildInvalidationPlan,
        CancellationToken ct);
}
