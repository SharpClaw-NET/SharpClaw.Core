using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.Permissions;

/// <summary>
/// Store-neutral admin permission seeding rules. Hosts provide registered
/// module permission keys and map the returned descriptors onto their own
/// persistence rows.
/// </summary>
public sealed class AdminPermissionSeedEngine
{
    private const PermissionClearance AdminClearance =
        PermissionClearance.Independent;

    public AdminPermissionSeedPlan BuildCreatePlan(
        IEnumerable<string> registeredGlobalFlagKeys,
        IEnumerable<string> registeredResourceTypes)
    {
        ArgumentNullException.ThrowIfNull(registeredGlobalFlagKeys);
        ArgumentNullException.ThrowIfNull(registeredResourceTypes);

        return new AdminPermissionSeedPlan(
            registeredGlobalFlagKeys
                .Select(flagKey => new AdminGlobalFlagGrantDescriptor(
                    flagKey,
                    AdminClearance))
                .ToList(),
            registeredResourceTypes
                .Select(resourceType => new AdminWildcardResourceGrantDescriptor(
                    resourceType,
                    AdminClearance))
                .ToList());
    }

    public AdminPermissionReconcilePlan BuildReconcilePlan(
        IEnumerable<string> registeredGlobalFlagKeys,
        IEnumerable<string> registeredResourceTypes,
        IReadOnlyList<AdminGlobalFlagGrantFact> existingGlobalFlags,
        IReadOnlyList<AdminWildcardResourceGrantFact> existingWildcardResources)
    {
        ArgumentNullException.ThrowIfNull(registeredGlobalFlagKeys);
        ArgumentNullException.ThrowIfNull(registeredResourceTypes);
        ArgumentNullException.ThrowIfNull(existingGlobalFlags);
        ArgumentNullException.ThrowIfNull(existingWildcardResources);

        var missingFlags = new List<AdminGlobalFlagGrantDescriptor>();
        var flagUpdates = new List<AdminGlobalFlagGrantUpdate>();
        foreach (var flagKey in registeredGlobalFlagKeys)
        {
            var existing = existingGlobalFlags.FirstOrDefault(
                flag => flag.FlagKey == flagKey);
            if (existing is null)
            {
                missingFlags.Add(new AdminGlobalFlagGrantDescriptor(
                    flagKey,
                    AdminClearance));
            }
            else if (existing.Clearance != AdminClearance)
            {
                flagUpdates.Add(new AdminGlobalFlagGrantUpdate(
                    flagKey,
                    AdminClearance));
            }
        }

        var missingResources = new List<AdminWildcardResourceGrantDescriptor>();
        var resourceUpdates = new List<AdminWildcardResourceGrantUpdate>();
        foreach (var resourceType in registeredResourceTypes)
        {
            var existing = existingWildcardResources.FirstOrDefault(
                resource => resource.ResourceType == resourceType);
            if (existing is null)
            {
                missingResources.Add(new AdminWildcardResourceGrantDescriptor(
                    resourceType,
                    AdminClearance));
            }
            else if (existing.Clearance != AdminClearance)
            {
                resourceUpdates.Add(new AdminWildcardResourceGrantUpdate(
                    resourceType,
                    AdminClearance));
            }
        }

        return new AdminPermissionReconcilePlan(
            missingFlags,
            flagUpdates,
            missingResources,
            resourceUpdates);
    }
}

public sealed record AdminPermissionSeedPlan(
    IReadOnlyList<AdminGlobalFlagGrantDescriptor> GlobalFlags,
    IReadOnlyList<AdminWildcardResourceGrantDescriptor> WildcardResources);

public sealed record AdminPermissionReconcilePlan(
    IReadOnlyList<AdminGlobalFlagGrantDescriptor> MissingGlobalFlags,
    IReadOnlyList<AdminGlobalFlagGrantUpdate> GlobalFlagUpdates,
    IReadOnlyList<AdminWildcardResourceGrantDescriptor> MissingWildcardResources,
    IReadOnlyList<AdminWildcardResourceGrantUpdate> WildcardResourceUpdates)
{
    public bool HasChanges =>
        MissingGlobalFlags.Count > 0
        || GlobalFlagUpdates.Count > 0
        || MissingWildcardResources.Count > 0
        || WildcardResourceUpdates.Count > 0;
}

public sealed record AdminGlobalFlagGrantDescriptor(
    string FlagKey,
    PermissionClearance Clearance);

public sealed record AdminWildcardResourceGrantDescriptor(
    string ResourceType,
    PermissionClearance Clearance);

public sealed record AdminGlobalFlagGrantFact(
    string FlagKey,
    PermissionClearance Clearance);

public sealed record AdminWildcardResourceGrantFact(
    string ResourceType,
    PermissionClearance Clearance);

public sealed record AdminGlobalFlagGrantUpdate(
    string FlagKey,
    PermissionClearance Clearance);

public sealed record AdminWildcardResourceGrantUpdate(
    string ResourceType,
    PermissionClearance Clearance);
