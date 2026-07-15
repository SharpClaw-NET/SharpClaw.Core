using SharpClaw.Core.State;
using SharpClaw.Contracts.DTOs.DefaultResources;
using SharpClaw.Core.Permissions;

namespace SharpClaw.Core.Resources;

/// <summary>
/// Store-neutral implementation of SharpClaw default-resource semantics.
/// Hosts provide registry-derived keys and loaded snapshots; Core owns
/// normalization, merge precedence, and default resolution order.
/// </summary>
public sealed class DefaultResourceEngine
{
    /// <summary>Normalizes default-resource keys for storage and lookup.</summary>
    public static string NormalizeKey(string key) =>
        key.ToLowerInvariant();

    /// <summary>Returns an empty default-resource response with the supplied id.</summary>
    public static DefaultResourcesResponse EmptyResponse(Guid id) =>
        new(id, new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Converts a default-resource snapshot into the public response shape.</summary>
    public static DefaultResourcesResponse ToResponse(DefaultResourceSetSnapshot snapshot)
    {
        return new DefaultResourcesResponse(
            snapshot.Id,
            new Dictionary<string, Guid>(
                snapshot.Entries,
                StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Merges channel and context default-resource sets. Channel values take
    /// precedence and context values fill gaps.
    /// </summary>
    public static DefaultResourcesResponse Merge(
        Guid primaryId,
        DefaultResourceSetSnapshot? channel,
        DefaultResourceSetSnapshot? context)
    {
        if (channel is null && context is null)
            return EmptyResponse(Guid.Empty);

        if (channel is null)
            return ToResponse(context!);

        if (context is null)
            return ToResponse(channel!);

        var merged = new Dictionary<string, Guid>(
            context!.Entries,
            StringComparer.OrdinalIgnoreCase);

        foreach (var (key, resourceId) in channel!.Entries)
            merged[key] = resourceId;

        return new DefaultResourcesResponse(primaryId, merged);
    }

    /// <summary>
    /// Resolves the default resource for a per-resource action using the
    /// SharpClaw precedence order: channel defaults, context defaults, then
    /// ordered permission-set defaults.
    /// </summary>
    public Guid? ResolveDefaultResource(DefaultResourceResolutionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.DefaultResourceKey))
        {
            var channelDefault = FindDefaultResource(
                request.ChannelDefaults,
                request.DefaultResourceKey);
            if (channelDefault.HasValue)
                return channelDefault;

            var contextDefault = FindDefaultResource(
                request.ContextDefaults,
                request.DefaultResourceKey);
            if (contextDefault.HasValue)
                return contextDefault;
        }

        if (string.IsNullOrWhiteSpace(request.ResourceType))
            return null;

        foreach (var permissionSet in request.OrderedPermissionSets)
        {
            var permissionDefault = FindPermissionDefaultResource(
                permissionSet,
                request.ResourceType);
            if (permissionDefault.HasValue)
                return permissionDefault;
        }

        return null;
    }

    /// <summary>Finds a keyed default resource in a set snapshot.</summary>
    public static Guid? FindDefaultResource(
        DefaultResourceSetSnapshot? set,
        string? resourceKey)
    {
        if (set is null || string.IsNullOrWhiteSpace(resourceKey))
            return null;

        return set.Entries.TryGetValue(resourceKey, out var resourceId)
            ? resourceId
            : null;
    }

    /// <summary>Finds the default resource grant for a resource type.</summary>
    public static Guid? FindPermissionDefaultResource(
        PermissionSetSnapshot permissionSet,
        string resourceType)
    {
        return permissionSet.ResourceAccesses
            .FirstOrDefault(access =>
                access.ResourceType == resourceType && access.IsDefault)
            ?.ResourceId;
    }

    /// <summary>
    /// Applies a bulk default-resource write to the supplied set. A null
    /// value removes the matching entry; missing keys are left unchanged.
    /// </summary>
    public static void Apply(
        DefaultResourceSetState defaultResourceSet,
        SetDefaultResourcesRequest request,
        Action<DefaultResourceEntryState>? removeEntry = null)
    {
        ArgumentNullException.ThrowIfNull(defaultResourceSet);
        ArgumentNullException.ThrowIfNull(request);

        foreach (var (key, value) in request.Entries)
            ApplyKey(defaultResourceSet, key, value, removeEntry);
    }

    /// <summary>
    /// Applies a single default-resource key mutation to the supplied set.
    /// Existing keys are matched case-insensitively and stored normalized.
    /// </summary>
    public static void ApplyKey(
        DefaultResourceSetState defaultResourceSet,
        string key,
        Guid? value,
        Action<DefaultResourceEntryState>? removeEntry = null)
    {
        ArgumentNullException.ThrowIfNull(defaultResourceSet);

        var normalised = NormalizeKey(key);
        var existing = defaultResourceSet.Entries.FirstOrDefault(
            e => string.Equals(
                e.ResourceKey,
                normalised,
                StringComparison.OrdinalIgnoreCase));

        if (value is null)
        {
            if (existing is not null)
            {
                defaultResourceSet.Entries.Remove(existing);
                removeEntry?.Invoke(existing);
            }

            return;
        }

        if (existing is not null)
        {
            existing.ResourceId = value.Value;
        }
        else
        {
            defaultResourceSet.Entries.Add(new DefaultResourceEntryState
            {
                DefaultResourceSetId = defaultResourceSet.Id,
                ResourceKey = normalised,
                ResourceId = value.Value
            });
        }
    }

}

/// <summary>
/// Store-neutral default-resource resolution inputs supplied by the host.
/// </summary>
public sealed record DefaultResourceResolutionRequest(
    string? DefaultResourceKey,
    string? ResourceType,
    DefaultResourceSetSnapshot? ChannelDefaults,
    DefaultResourceSetSnapshot? ContextDefaults,
    IReadOnlyList<PermissionSetSnapshot> OrderedPermissionSets);
