using SharpClaw.Core.State;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.Permissions;

/// <summary>
/// Store-neutral permission facts used by the Core permission evaluator.
/// Hosts may build this from EF entities, JSON records, remote APIs, or tests.
/// </summary>
public sealed record PermissionSetSnapshot(
    IReadOnlyList<GlobalFlagPermissionGrant> GlobalFlags,
    IReadOnlyList<ResourcePermissionGrant> ResourceAccesses,
    IReadOnlySet<Guid> ClearanceUserWhitelist,
    IReadOnlySet<Guid> ClearanceAgentWhitelist)
{
    /// <summary>An empty permission set with no grants or approver whitelists.</summary>
    public static PermissionSetSnapshot Empty { get; } = new(
        [],
        [],
        new HashSet<Guid>(),
        new HashSet<Guid>());

    /// <summary>
    /// Creates an immutable evaluation snapshot from neutral Core state.
    /// </summary>
    public static PermissionSetSnapshot FromPermissionSet(PermissionSetState permissionSet)
    {
        ArgumentNullException.ThrowIfNull(permissionSet);

        return new PermissionSetSnapshot(
            permissionSet.GlobalFlags
                .Select(flag => new GlobalFlagPermissionGrant(flag.FlagKey, flag.Clearance))
                .ToList(),
            permissionSet.ResourceAccesses
                .Select(access => new ResourcePermissionGrant(
                    access.ResourceType,
                    access.ResourceId,
                    access.Clearance,
                    access.SubType,
                    access.AccessLevel,
                    access.IsDefault))
                .ToList(),
            permissionSet.ClearanceUserWhitelist.ToHashSet(),
            permissionSet.ClearanceAgentWhitelist.ToHashSet());
    }
}

/// <summary>A single global flag grant and its configured clearance.</summary>
public sealed record GlobalFlagPermissionGrant(
    string FlagKey,
    PermissionClearance Clearance);

/// <summary>A single resource grant and its configured clearance.</summary>
public sealed record ResourcePermissionGrant(
    string ResourceType,
    Guid ResourceId,
    PermissionClearance Clearance,
    string SubType = "",
    string? AccessLevel = null,
    bool IsDefault = false);
