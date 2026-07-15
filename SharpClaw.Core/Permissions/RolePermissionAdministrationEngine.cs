using SharpClaw.Core.State;
using System.Linq.Expressions;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.Roles;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.Permissions;

/// <summary>
/// Store-neutral role permission administration rules.
/// </summary>
public sealed class RolePermissionAdministrationEngine
{
    /// <summary>
    /// Creates a role with an empty permission set attached.
    /// </summary>
    public RoleState CreateRole(string name)
    {
        return new RoleState
        {
            Name = NormalizeRoleName(name),
            PermissionSet = new PermissionSetState()
        };
    }

    /// <summary>
    /// Creates and attaches a permission set for a role that has none.
    /// </summary>
    public PermissionSetState CreatePermissionSetForRole(RoleState role)
    {
        ArgumentNullException.ThrowIfNull(role);

        var permissionSet = new PermissionSetState();
        role.PermissionSet = permissionSet;
        return permissionSet;
    }

    /// <summary>
    /// Applies a validated role rename.
    /// </summary>
    public void RenameRole(RoleState role, string newName)
    {
        ArgumentNullException.ThrowIfNull(role);

        role.Name = NormalizeRoleName(newName);
    }

    /// <summary>
    /// Plans deletion for a role and detaches assigned users.
    /// </summary>
    public RoleDeletionPlan PlanDeleteRole(RoleState role)
    {
        ArgumentNullException.ThrowIfNull(role);

        foreach (var user in role.Users)
        {
            user.RoleId = null;
            user.Role = null;
        }

        var permissionSet = role.PermissionSet;
        return new RoleDeletionPlan(
            role,
            permissionSet,
            permissionSet?.GlobalFlags.ToList() ?? [],
            permissionSet?.ResourceAccesses.ToList() ?? []);
    }

    /// <summary>
    /// Projects a role entity to its public response shape.
    /// </summary>
    public RoleResponse ToResponse(RoleState role)
    {
        ArgumentNullException.ThrowIfNull(role);

        return new RoleResponse(role.Id, role.Name, role.PermissionSetId);
    }

    /// <summary>
    /// Projects roles to their public response shape.
    /// </summary>
    public Expression<Func<RoleState, RoleResponse>> ToResponseProjection() =>
        role => new RoleResponse(role.Id, role.Name, role.PermissionSetId);

    /// <summary>
    /// Projects a role and permission set to a full permissions response.
    /// </summary>
    public RolePermissionsResponse ToPermissionsResponse(
        RoleState role,
        PermissionSetState? permissionSet)
    {
        ArgumentNullException.ThrowIfNull(role);

        return new RolePermissionsResponse(
            RoleId: role.Id,
            RoleName: role.Name,
            GlobalFlags: permissionSet?.GlobalFlags
                .ToDictionary(flag => flag.FlagKey, flag => flag.Clearance)
                ?? new Dictionary<string, PermissionClearance>(),
            ResourceGrants: permissionSet is null
                ? new Dictionary<string, IReadOnlyList<ResourceGrant>>()
                : permissionSet.ResourceAccesses
                    .GroupBy(access => access.ResourceType)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<ResourceGrant>)group
                            .Select(access => new ResourceGrant(
                                access.ResourceId,
                                access.Clearance))
                            .ToList()));
    }

    /// <summary>
    /// Validates that the caller may grant every requested permission.
    /// </summary>
    public void ValidateRequestedGrants(
        SetRolePermissionsRequest request,
        PermissionSetState? callerPermissions)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateGlobalFlags(request, callerPermissions);
        ValidateResourceGrants(request, callerPermissions);
    }

    /// <summary>
    /// Reconciles a target permission set with the requested role state.
    /// </summary>
    public void ReconcilePermissionSet(
        PermissionSetState permissionSet,
        SetRolePermissionsRequest request)
    {
        ArgumentNullException.ThrowIfNull(permissionSet);
        ArgumentNullException.ThrowIfNull(request);

        ReconcileGlobalFlags(permissionSet, request.GlobalFlags);
        ReconcileResourceAccesses(permissionSet, request.ResourceGrants);
    }

    /// <summary>Returns whether unique-name enforcement should be active.</summary>
    public static bool IsUniqueRoleNameEnforced(string? configurationValue)
    {
        return configurationValue is null
            || !bool.TryParse(configurationValue, out var enforced)
            || enforced;
    }

    /// <summary>Throws when a role name already exists.</summary>
    public void EnsureRoleNameAvailable(string name, IEnumerable<string> existingRoleNames)
    {
        ArgumentNullException.ThrowIfNull(existingRoleNames);

        var normalized = NormalizeRoleName(name);
        if (existingRoleNames.Any(existing =>
                existing.Trim().Equals(
                    normalized,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"A role named '{name}' already exists.");
        }
    }

    private static void ValidateGlobalFlags(
        SetRolePermissionsRequest request,
        PermissionSetState? callerPermissions)
    {
        if (request.GlobalFlags is null or { Count: 0 })
            return;

        if (callerPermissions is null)
            throw new UnauthorizedAccessException(
                "You have no permissions - cannot grant any global flags.");

        foreach (var (flagKey, _) in request.GlobalFlags)
        {
            if (!callerPermissions.GlobalFlags.Any(flag => flag.FlagKey == flagKey))
                throw new UnauthorizedAccessException(
                    $"Cannot grant {flagKey} - you do not hold this permission.");
        }
    }

    private static void ValidateResourceGrants(
        SetRolePermissionsRequest request,
        PermissionSetState? callerPermissions)
    {
        if (request.ResourceGrants is null or { Count: 0 })
            return;

        foreach (var (resourceType, grants) in request.ResourceGrants)
            ValidateGrants(resourceType, resourceType, grants, callerPermissions);
    }

    private static void ValidateGrants(
        string name,
        string resourceType,
        IReadOnlyList<ResourceGrant>? requested,
        PermissionSetState? callerPermissions)
    {
        if (requested is null or { Count: 0 })
            return;

        var callerGrants = callerPermissions?.ResourceAccesses
            .Where(access => access.ResourceType == resourceType)
            .Select(access => access.ResourceId)
            .ToHashSet();

        if (callerGrants is null or { Count: 0 })
            throw new UnauthorizedAccessException(
                $"Cannot grant {name} - you hold no grants of this type.");

        var hasWildcard = callerGrants.Contains(WellKnownIds.AllResources);

        foreach (var grant in requested)
        {
            if (hasWildcard)
                continue;

            if (!callerGrants.Contains(grant.ResourceId))
                throw new UnauthorizedAccessException(
                    $"Cannot grant {name} for resource {grant.ResourceId} " +
                    "- you do not hold this permission.");
        }
    }

    private static void ReconcileGlobalFlags(
        PermissionSetState permissionSet,
        IReadOnlyDictionary<string, PermissionClearance>? requested)
    {
        var requestedMap = requested ?? new Dictionary<string, PermissionClearance>();

        foreach (var existing in permissionSet.GlobalFlags.ToList())
        {
            if (!requestedMap.TryGetValue(existing.FlagKey, out var newClearance))
                permissionSet.GlobalFlags.Remove(existing);
            else if (existing.Clearance != newClearance)
                existing.Clearance = newClearance;
        }

        var existingKeys = permissionSet.GlobalFlags
            .Select(flag => flag.FlagKey)
            .ToHashSet();

        foreach (var (key, clearance) in requestedMap)
        {
            if (!existingKeys.Contains(key))
            {
                permissionSet.GlobalFlags.Add(new GlobalFlagState
                {
                    FlagKey = key,
                    Clearance = clearance
                });
            }
        }
    }

    private static void ReconcileResourceAccesses(
        PermissionSetState permissionSet,
        IReadOnlyDictionary<string, IReadOnlyList<ResourceGrant>>? requested)
    {
        var requestedMap = requested?
            .SelectMany(kvp => kvp.Value.Select(grant =>
                (ResourceType: kvp.Key, grant.ResourceId, grant.Clearance)))
            .ToDictionary(
                item => (item.ResourceType, item.ResourceId),
                item => item.Clearance)
            ?? [];

        foreach (var access in permissionSet.ResourceAccesses.ToList())
        {
            var key = (access.ResourceType, access.ResourceId);
            if (!requestedMap.TryGetValue(key, out var newClearance))
            {
                if (access.ResourceId == WellKnownIds.AllResources)
                    throw new InvalidOperationException(
                        $"Wildcard grant for '{access.ResourceType}' is immutable and cannot be removed.");

                permissionSet.ResourceAccesses.Remove(access);
            }
            else if (access.Clearance != newClearance)
            {
                access.Clearance = newClearance;
            }
        }

        var existingKeys = permissionSet.ResourceAccesses
            .Select(access => (access.ResourceType, access.ResourceId))
            .ToHashSet();

        foreach (var (resourceType, grants) in requested
                 ?? new Dictionary<string, IReadOnlyList<ResourceGrant>>())
        {
            foreach (var grant in grants)
            {
                if (!existingKeys.Contains((resourceType, grant.ResourceId)))
                {
                    permissionSet.ResourceAccesses.Add(new ResourceAccessState
                    {
                        ResourceType = resourceType,
                        ResourceId = grant.ResourceId,
                        Clearance = grant.Clearance
                    });
                }
            }
        }
    }

    private static string NormalizeRoleName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "Role name cannot be empty.",
                nameof(name));

        return name.Trim();
    }
}

/// <summary>
/// Store-neutral description of rows affected by a role delete.
/// </summary>
/// <param name="Role">The role being deleted.</param>
/// <param name="PermissionSet">The owned permission set, when present.</param>
/// <param name="GlobalFlags">Global flag rows owned by the permission set.</param>
/// <param name="ResourceAccesses">Resource grant rows owned by the permission set.</param>
public sealed record RoleDeletionPlan(
    RoleState Role,
    PermissionSetState? PermissionSet,
    IReadOnlyList<GlobalFlagState> GlobalFlags,
    IReadOnlyList<ResourceAccessState> ResourceAccesses);
