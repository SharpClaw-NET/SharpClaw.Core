using SharpClaw.Core.State;
using SharpClaw.Contracts.DTOs.DefaultResources;
using SharpClaw.Core.Chat;

namespace SharpClaw.Core.Resources;

/// <summary>
/// Store-neutral default-resource administration workflow. Hosts provide
/// loaded channel/context rows and persistence hooks; Core owns merge,
/// creation, mutation, and cache invalidation decisions.
/// </summary>
public sealed class DefaultResourceAdministrationEngine(
    ChatRuntimeInvalidationPlanner invalidations)
{
    public DefaultResourceAdministrationEngine()
        : this(new ChatRuntimeInvalidationPlanner())
    {
    }

    public async Task<DefaultResourcesResponse?> GetForChannelAsync(
        Guid channelId,
        IDefaultResourceAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var channel = await host.LoadChannelWithDefaultResourcesAsync(
            channelId,
            ct);
        if (channel is null)
            return null;

        return DefaultResourceEngine.Merge(
            channel.Id,
            Snapshot(channel.DefaultResourceSet),
            Snapshot(channel.AgentContext?.DefaultResourceSet));
    }

    public async Task<DefaultResourcesResponse?> GetForContextAsync(
        Guid contextId,
        IDefaultResourceAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var context = await host.LoadContextWithDefaultResourcesAsync(
            contextId,
            ct);
        if (context is null)
            return null;

        return context.DefaultResourceSet is { } set
            ? DefaultResourceEngine.ToResponse(
                DefaultResourceSetSnapshot.FromDefaultResourceSet(set))
            : DefaultResourceEngine.EmptyResponse(Guid.Empty);
    }

    public async Task<DefaultResourcesResponse?> SetForChannelAsync(
        Guid channelId,
        SetDefaultResourcesRequest request,
        IDefaultResourceAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var channel = await host.LoadChannelWithDefaultResourcesAsync(
            channelId,
            ct);
        if (channel is null)
            return null;

        var set = EnsureChannelDefaultResourceSet(channel, host);
        DefaultResourceEngine.Apply(set, request, host.RemoveDefaultResourceEntry);
        await host.SaveAsync(
            () => invalidations.DefaultResourcesForChannelChanged(channelId),
            ct);

        return DefaultResourceEngine.ToResponse(
            DefaultResourceSetSnapshot.FromDefaultResourceSet(set));
    }

    public async Task<DefaultResourcesResponse?> SetForContextAsync(
        Guid contextId,
        SetDefaultResourcesRequest request,
        IDefaultResourceAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var context = await host.LoadContextWithDefaultResourcesAsync(
            contextId,
            ct);
        if (context is null)
            return null;

        var set = EnsureContextDefaultResourceSet(context, host);
        DefaultResourceEngine.Apply(set, request, host.RemoveDefaultResourceEntry);
        await SaveContextDefaultResourceChangeAsync(contextId, host, ct);

        return DefaultResourceEngine.ToResponse(
            DefaultResourceSetSnapshot.FromDefaultResourceSet(set));
    }

    public async Task<DefaultResourcesResponse?> SetKeyForChannelAsync(
        Guid channelId,
        string key,
        Guid resourceId,
        IDefaultResourceAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var channel = await host.LoadChannelWithDefaultResourcesAsync(
            channelId,
            ct);
        if (channel is null)
            return null;

        var set = EnsureChannelDefaultResourceSet(channel, host);
        DefaultResourceEngine.ApplyKey(
            set,
            key,
            resourceId,
            host.RemoveDefaultResourceEntry);
        await host.SaveAsync(
            () => invalidations.DefaultResourcesForChannelChanged(channelId),
            ct);

        return DefaultResourceEngine.ToResponse(
            DefaultResourceSetSnapshot.FromDefaultResourceSet(set));
    }

    public async Task<DefaultResourcesResponse?> ClearKeyForChannelAsync(
        Guid channelId,
        string key,
        IDefaultResourceAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var channel = await host.LoadChannelWithDefaultResourcesAsync(
            channelId,
            ct);
        if (channel is null)
            return null;
        if (channel.DefaultResourceSet is null)
            return DefaultResourceEngine.EmptyResponse(Guid.Empty);

        DefaultResourceEngine.ApplyKey(
            channel.DefaultResourceSet,
            key,
            null,
            host.RemoveDefaultResourceEntry);
        await host.SaveAsync(
            () => invalidations.DefaultResourcesForChannelChanged(channelId),
            ct);

        return DefaultResourceEngine.ToResponse(
            DefaultResourceSetSnapshot.FromDefaultResourceSet(
                channel.DefaultResourceSet));
    }

    public async Task<DefaultResourcesResponse?> SetKeyForContextAsync(
        Guid contextId,
        string key,
        Guid resourceId,
        IDefaultResourceAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var context = await host.LoadContextWithDefaultResourcesAsync(
            contextId,
            ct);
        if (context is null)
            return null;

        var set = EnsureContextDefaultResourceSet(context, host);
        DefaultResourceEngine.ApplyKey(
            set,
            key,
            resourceId,
            host.RemoveDefaultResourceEntry);
        await SaveContextDefaultResourceChangeAsync(contextId, host, ct);

        return DefaultResourceEngine.ToResponse(
            DefaultResourceSetSnapshot.FromDefaultResourceSet(set));
    }

    public async Task<DefaultResourcesResponse?> ClearKeyForContextAsync(
        Guid contextId,
        string key,
        IDefaultResourceAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var context = await host.LoadContextWithDefaultResourcesAsync(
            contextId,
            ct);
        if (context is null)
            return null;
        if (context.DefaultResourceSet is null)
            return DefaultResourceEngine.EmptyResponse(Guid.Empty);

        DefaultResourceEngine.ApplyKey(
            context.DefaultResourceSet,
            key,
            null,
            host.RemoveDefaultResourceEntry);
        await SaveContextDefaultResourceChangeAsync(contextId, host, ct);

        return DefaultResourceEngine.ToResponse(
            DefaultResourceSetSnapshot.FromDefaultResourceSet(
                context.DefaultResourceSet));
    }

    private async Task SaveContextDefaultResourceChangeAsync(
        Guid contextId,
        IDefaultResourceAdministrationHost host,
        CancellationToken ct)
    {
        var channelIds = await host.ListChannelIdsForContextAsync(contextId, ct);
        await host.SaveAsync(
            () => invalidations.DefaultResourcesForContextChanged(channelIds),
            ct);
    }

    private static DefaultResourceSetState EnsureChannelDefaultResourceSet(
        ChannelState channel,
        IDefaultResourceAdministrationHost host)
    {
        if (channel.DefaultResourceSet is not null)
            return channel.DefaultResourceSet;

        var set = new DefaultResourceSetState();
        channel.DefaultResourceSet = set;
        channel.DefaultResourceSetId = set.Id;
        host.TrackDefaultResourceSet(set);
        return set;
    }

    private static DefaultResourceSetState EnsureContextDefaultResourceSet(
        ChannelContextState context,
        IDefaultResourceAdministrationHost host)
    {
        if (context.DefaultResourceSet is not null)
            return context.DefaultResourceSet;

        var set = new DefaultResourceSetState();
        context.DefaultResourceSet = set;
        context.DefaultResourceSetId = set.Id;
        host.TrackDefaultResourceSet(set);
        return set;
    }

    private static DefaultResourceSetSnapshot? Snapshot(
        DefaultResourceSetState? set) =>
        set is null ? null : DefaultResourceSetSnapshot.FromDefaultResourceSet(set);
}

public interface IDefaultResourceAdministrationHost
{
    Task<ChannelState?> LoadChannelWithDefaultResourcesAsync(
        Guid channelId,
        CancellationToken ct);

    Task<ChannelContextState?> LoadContextWithDefaultResourcesAsync(
        Guid contextId,
        CancellationToken ct);

    Task<IReadOnlyList<Guid>> ListChannelIdsForContextAsync(
        Guid contextId,
        CancellationToken ct);

    void TrackDefaultResourceSet(DefaultResourceSetState defaultResourceSet);

    void RemoveDefaultResourceEntry(DefaultResourceEntryState entry);

    Task SaveAsync(
        Func<ChatRuntimeInvalidationPlan?>? buildInvalidationPlan,
        CancellationToken ct);
}
