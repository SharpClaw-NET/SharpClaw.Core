using SharpClaw.Core.State;
using SharpClaw.Contracts.DTOs.Tools;
using SharpClaw.Core.Chat;

namespace SharpClaw.Core.Tools;

/// <summary>
/// Store-neutral tool-awareness administration workflow. Core owns the
/// entity mutation and invalidation semantics; hosts own persistence.
/// </summary>
public sealed class ToolAwarenessAdministrationEngine(
    ToolAwarenessSetEngine toolAwareness,
    ChatRuntimeInvalidationPlanner invalidations)
{
    public ToolAwarenessAdministrationEngine()
        : this(new ToolAwarenessSetEngine(), new ChatRuntimeInvalidationPlanner())
    {
    }

    /// <summary>
    /// Loads one tool-awareness set and projects it to the public response.
    /// </summary>
    public async Task<ToolAwarenessSetResponse?> GetByIdAsync(
        Guid id,
        IToolAwarenessAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var entity = await host.LoadToolAwarenessSetAsync(id, ct);
        return entity is null ? null : toolAwareness.ToResponse(entity);
    }

    /// <summary>
    /// Lists tool-awareness sets using Core projection semantics.
    /// </summary>
    public async Task<IReadOnlyList<ToolAwarenessSetResponse>> ListAsync(
        IToolAwarenessAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var entities = await host.ListToolAwarenessSetsAsync(ct);
        return entities
            .Select(toolAwareness.ToResponse)
            .ToList();
    }

    public async Task<ToolAwarenessSetResponse> CreateAsync(
        CreateToolAwarenessSetRequest request,
        IToolAwarenessAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var entity = toolAwareness.Create(request);
        host.TrackToolAwarenessSet(entity);
        await host.SaveAsync(null, ct);

        return toolAwareness.ToResponse(entity);
    }

    public async Task<ToolAwarenessSetResponse?> UpdateAsync(
        Guid id,
        UpdateToolAwarenessSetRequest request,
        IToolAwarenessAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var entity = await host.LoadToolAwarenessSetAsync(id, ct);
        if (entity is null)
            return null;

        toolAwareness.ApplyUpdate(entity, request);
        await host.SaveAsync(invalidations.ToolAwarenessSetsChanged, ct);

        return toolAwareness.ToResponse(entity);
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        IToolAwarenessAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var entity = await host.LoadToolAwarenessSetAsync(id, ct);
        if (entity is null)
            return false;

        host.RemoveToolAwarenessSet(entity);
        await host.SaveAsync(invalidations.ToolAwarenessSetsChanged, ct);

        return true;
    }
}

public interface IToolAwarenessAdministrationHost
{
    Task<ToolAwarenessSetState?> LoadToolAwarenessSetAsync(
        Guid id,
        CancellationToken ct);

    /// <summary>Loads all tool-awareness sets for public projection.</summary>
    Task<IReadOnlyList<ToolAwarenessSetState>> ListToolAwarenessSetsAsync(
        CancellationToken ct);

    void TrackToolAwarenessSet(ToolAwarenessSetState entity);

    void RemoveToolAwarenessSet(ToolAwarenessSetState entity);

    Task SaveAsync(
        Func<ChatRuntimeInvalidationPlan?>? buildInvalidationPlan,
        CancellationToken ct);
}
