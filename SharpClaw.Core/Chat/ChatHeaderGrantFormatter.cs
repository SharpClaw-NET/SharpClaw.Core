using SharpClaw.Core.State;
using SharpClaw.Contracts;
using SharpClaw.Core.Modules;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Formats permission-set grants for chat headers and agent self-awareness.
/// </summary>
public sealed class ChatHeaderGrantFormatter(ModuleRegistry moduleRegistry)
{
    /// <summary>
    /// Formats global and per-resource grant names without expanding resource IDs.
    /// </summary>
    public IReadOnlyList<string> FormatGrantNames(PermissionSetState permissionSet)
    {
        ArgumentNullException.ThrowIfNull(permissionSet);

        var grants = new List<string>();

        foreach (var flag in permissionSet.GlobalFlags)
        {
            grants.Add(flag.FlagKey.StartsWith("Can", StringComparison.Ordinal)
                ? flag.FlagKey[3..]
                : flag.FlagKey);
        }

        foreach (var desc in moduleRegistry.GetAllResourceTypeDescriptors())
        {
            if (permissionSet.ResourceAccesses.Any(a => a.ResourceType == desc.ResourceType))
                grants.Add(desc.GrantLabel);
        }

        return grants;
    }

    /// <summary>
    /// Formats grant names and expands wildcard resource grants into concrete IDs.
    /// </summary>
    public async Task<IReadOnlyList<string>> FormatGrantNamesWithResourcesAsync(
        PermissionSetState permissionSet,
        IServiceProvider serviceProvider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(permissionSet);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var grants = new List<string>();

        foreach (var flag in permissionSet.GlobalFlags)
        {
            grants.Add(flag.FlagKey.StartsWith("Can", StringComparison.Ordinal)
                ? flag.FlagKey[3..]
                : flag.FlagKey);
        }

        foreach (var desc in moduleRegistry.GetAllResourceTypeDescriptors())
        {
            var grantedIds = permissionSet.ResourceAccesses
                .Where(a => a.ResourceType == desc.ResourceType)
                .Select(a => a.ResourceId)
                .ToList();

            await AppendResourceGrantAsync(
                grants,
                desc.GrantLabel,
                grantedIds,
                () => desc.LoadAllIds(serviceProvider, ct));
        }

        return grants;
    }

    private static async Task AppendResourceGrantAsync(
        List<string> grants,
        string grantName,
        IEnumerable<Guid> grantedIds,
        Func<Task<List<Guid>>> loadAllIdsAsync)
    {
        var ids = grantedIds.ToList();
        if (ids.Count == 0)
            return;

        List<Guid> resolved;
        if (ids.Any(id => id == WellKnownIds.AllResources))
            resolved = await loadAllIdsAsync();
        else
            resolved = ids;

        if (resolved.Count == 0)
        {
            grants.Add(grantName);
            return;
        }

        var idList = string.Join(",", resolved.Select(id => id.ToString("D")));
        grants.Add($"{grantName}[{idList}]");
    }
}
