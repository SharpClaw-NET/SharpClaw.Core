using SharpClaw.Core.State;
using SharpClaw.Contracts.DTOs.Tools;

namespace SharpClaw.Core.Tools;

/// <summary>
/// Store-neutral tool-awareness set creation, mutation, and projection rules.
/// </summary>
public sealed class ToolAwarenessSetEngine
{
    /// <summary>Creates a tool-awareness set entity from a request.</summary>
    public ToolAwarenessSetState Create(CreateToolAwarenessSetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ToolAwarenessSetState
        {
            Name = request.Name,
            Tools = request.Tools ?? []
        };
    }

    /// <summary>Applies an update request to a loaded tool-awareness set.</summary>
    public void ApplyUpdate(
        ToolAwarenessSetState entity,
        UpdateToolAwarenessSetRequest request)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Name is not null)
            entity.Name = request.Name;

        if (request.Tools is not null)
            entity.Tools = request.Tools;
    }

    /// <summary>Projects a loaded entity to its response shape.</summary>
    public ToolAwarenessSetResponse ToResponse(ToolAwarenessSetState entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new ToolAwarenessSetResponse(
            entity.Id,
            entity.Name,
            entity.Tools,
            entity.CreatedAt,
            entity.UpdatedAt);
    }
}
