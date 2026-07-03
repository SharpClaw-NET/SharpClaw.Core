using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.Permissions;

/// <summary>
/// Store-neutral rules for reconciling module-provided permission grants into
/// existing wildcard permission sets. Hosts load permission facts and map the
/// returned descriptors onto their own persistence rows.
/// </summary>
public sealed class ModulePermissionReconciliationEngine
{
    private const PermissionClearance ReconciledClearance =
        PermissionClearance.Independent;

    public ModulePermissionReconciliationPlan BuildPlan(
        IEnumerable<string> moduleGlobalFlagKeys,
        IEnumerable<string> moduleResourceTypes,
        IReadOnlyList<ModulePermissionSetReconciliationFact> permissionSets)
    {
        ArgumentNullException.ThrowIfNull(moduleGlobalFlagKeys);
        ArgumentNullException.ThrowIfNull(moduleResourceTypes);
        ArgumentNullException.ThrowIfNull(permissionSets);

        var flagKeys = moduleGlobalFlagKeys.ToList();
        var resourceTypes = moduleResourceTypes.ToList();

        if (flagKeys.Count == 0 && resourceTypes.Count == 0)
            return ModulePermissionReconciliationPlan.Empty;

        var permissionSetPlans =
            new List<ModulePermissionSetReconciliationPlan>();

        foreach (var permissionSet in permissionSets)
        {
            var existingWildcardResources =
                new HashSet<string>(
                    permissionSet.ExistingWildcardResourceTypes,
                    StringComparer.Ordinal);
            if (existingWildcardResources.Count == 0)
                continue;

            var existingFlags =
                new HashSet<string>(
                    permissionSet.ExistingGlobalFlagKeys,
                    StringComparer.Ordinal);

            var missingResources =
                BuildMissingResourceGrants(
                    resourceTypes,
                    existingWildcardResources);
            var missingFlags =
                BuildMissingGlobalFlagGrants(flagKeys, existingFlags);

            if (missingResources.Count == 0 && missingFlags.Count == 0)
                continue;

            permissionSetPlans.Add(
                new ModulePermissionSetReconciliationPlan(
                    permissionSet.PermissionSetId,
                    missingResources,
                    missingFlags));
        }

        return new ModulePermissionReconciliationPlan(permissionSetPlans);
    }

    private static IReadOnlyList<ModuleWildcardResourceGrantDescriptor>
        BuildMissingResourceGrants(
            IReadOnlyList<string> moduleResourceTypes,
            HashSet<string> existingWildcardResources)
    {
        var missing = new List<ModuleWildcardResourceGrantDescriptor>();
        foreach (var resourceType in moduleResourceTypes)
        {
            if (existingWildcardResources.Contains(resourceType))
                continue;

            missing.Add(
                new ModuleWildcardResourceGrantDescriptor(
                    resourceType,
                    ReconciledClearance));
            existingWildcardResources.Add(resourceType);
        }

        return missing;
    }

    private static IReadOnlyList<ModuleGlobalFlagGrantDescriptor>
        BuildMissingGlobalFlagGrants(
            IReadOnlyList<string> moduleGlobalFlagKeys,
            HashSet<string> existingFlags)
    {
        var missing = new List<ModuleGlobalFlagGrantDescriptor>();
        foreach (var flagKey in moduleGlobalFlagKeys)
        {
            if (existingFlags.Contains(flagKey))
                continue;

            missing.Add(
                new ModuleGlobalFlagGrantDescriptor(
                    flagKey,
                    ReconciledClearance));
            existingFlags.Add(flagKey);
        }

        return missing;
    }
}

public sealed record ModulePermissionSetReconciliationFact(
    Guid PermissionSetId,
    IReadOnlyList<string> ExistingGlobalFlagKeys,
    IReadOnlyList<string> ExistingWildcardResourceTypes);

public sealed record ModulePermissionReconciliationPlan(
    IReadOnlyList<ModulePermissionSetReconciliationPlan> PermissionSets)
{
    public static ModulePermissionReconciliationPlan Empty { get; } = new([]);

    public bool HasChanges => PermissionSets.Count > 0;
}

public sealed record ModulePermissionSetReconciliationPlan(
    Guid PermissionSetId,
    IReadOnlyList<ModuleWildcardResourceGrantDescriptor> MissingWildcardResources,
    IReadOnlyList<ModuleGlobalFlagGrantDescriptor> MissingGlobalFlags)
{
    public bool HasChanges =>
        MissingWildcardResources.Count > 0 || MissingGlobalFlags.Count > 0;
}

public sealed record ModuleWildcardResourceGrantDescriptor(
    string ResourceType,
    PermissionClearance Clearance);

public sealed record ModuleGlobalFlagGrantDescriptor(
    string FlagKey,
    PermissionClearance Clearance);
