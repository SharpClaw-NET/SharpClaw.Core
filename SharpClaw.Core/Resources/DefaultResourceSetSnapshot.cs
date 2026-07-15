using SharpClaw.Core.State;

namespace SharpClaw.Core.Resources;

/// <summary>
/// Store-neutral default-resource set used by Core resource resolution.
/// </summary>
public sealed record DefaultResourceSetSnapshot(
    Guid Id,
    IReadOnlyDictionary<string, Guid> Entries)
{
    /// <summary>Creates a snapshot from the shared contracts entity shape.</summary>
    public static DefaultResourceSetSnapshot FromDefaultResourceSet(
        DefaultResourceSetState defaultResourceSet)
    {
        ArgumentNullException.ThrowIfNull(defaultResourceSet);

        return new DefaultResourceSetSnapshot(
            defaultResourceSet.Id,
            defaultResourceSet.Entries.ToDictionary(
                entry => entry.ResourceKey,
                entry => entry.ResourceId,
                StringComparer.OrdinalIgnoreCase));
    }
}
