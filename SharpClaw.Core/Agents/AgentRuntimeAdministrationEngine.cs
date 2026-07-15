using SharpClaw.Core.State;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.DTOs.Auth;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Modules;

namespace SharpClaw.Core.Agents;

/// <summary>
/// Store-neutral agent administration workflow. Hosts supply persistence,
/// session, provider, and module facts; Core owns operation ordering,
/// authorization checks, default-agent synthesis, and invalidation decisions.
/// </summary>
public sealed class AgentRuntimeAdministrationEngine(
    AgentAdministrationEngine agents,
    ChatRuntimeInvalidationPlanner invalidations)
{
    public async Task<AgentResponse> CreateAsync(
        CreateAgentRequest request,
        IAgentAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        if (host.UniqueAgentNamesEnforced)
            await EnsureAgentNameUniqueAsync(request.Name, null, host, ct);

        var model = await host.LoadModelAsync(request.ModelId, ct)
            ?? throw new ArgumentException($"Model {request.ModelId} not found.");

        var agent = agents.Create(
            request,
            model,
            host.GetParameterSpec(model.Provider.ProviderKey));

        host.TrackAgent(agent);
        await host.SaveAsync(null, ct);
        return agents.ToResponse(agent, model);
    }

    public async Task<AgentResponse?> UpdateAsync(
        Guid agentId,
        UpdateAgentRequest request,
        IAgentAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var agent = await host.LoadAgentAsync(agentId, ct);
        if (agent is null)
            return null;

        ModelState? replacementModel = null;
        if (request.ModelId is { } modelId)
            replacementModel = await host.LoadModelAsync(modelId, ct)
                ?? throw new ArgumentException($"Model {modelId} not found.");

        var effectiveModel = replacementModel ?? agent.Model;
        var existingNames = host.UniqueAgentNamesEnforced
            ? await host.ListAgentNamesAsync(agentId, ct)
            : Array.Empty<string>();

        agents.ApplyUpdate(
            agent,
            request,
            replacementModel,
            host.GetParameterSpec(effectiveModel.Provider.ProviderKey),
            host.UniqueAgentNamesEnforced,
            existingNames);

        await host.SaveAsync(
            () => invalidations.AgentChanged(agentId),
            ct);
        return agents.ToResponse(agent, agent.Model);
    }

    public async Task<AgentResponse?> AssignRoleAsync(
        Guid agentId,
        Guid roleId,
        IAgentAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var agent = await host.LoadAgentAsync(agentId, ct);
        if (agent is null)
            return null;

        RoleState? role = null;
        Guid? callerRoleId = null;
        PermissionSetState? callerPermissions = null;
        PermissionSetState? targetPermissions = null;

        if (roleId != Guid.Empty)
        {
            role = await host.LoadRoleAsync(roleId, ct)
                ?? throw new ArgumentException($"Role {roleId} not found.");

            var callerUserId = host.SessionUserId
                ?? throw new UnauthorizedAccessException(
                    "A logged-in user is required to assign roles.");

            var caller = await host.LoadUserAsync(callerUserId, ct);
            callerRoleId = caller?.RoleId;

            if (callerRoleId != role.Id)
            {
                targetPermissions = role.PermissionSetId.HasValue
                    ? await host.LoadFullPermissionSetAsync(role.PermissionSetId.Value, ct)
                    : null;

                callerPermissions = caller?.Role?.PermissionSetId is { } callerPermissionSetId
                    ? await host.LoadFullPermissionSetAsync(callerPermissionSetId, ct)
                    : null;
            }
        }

        agents.AssignRole(
            agent,
            roleId,
            role,
            callerRoleId,
            callerPermissions,
            targetPermissions,
            host.ModuleRegistry.GetAllRegisteredResourceTypes());

        await host.SaveAsync(
            () => invalidations.AgentChanged(agentId),
            ct);
        return agents.ToResponse(agent, agent.Model);
    }

    public async Task<MeResponse?> AssignUserRoleAsync(
        Guid roleId,
        IAgentAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var userId = host.SessionUserId
            ?? throw new UnauthorizedAccessException("A logged-in user is required.");

        var user = await host.LoadUserAsync(userId, ct);
        if (user is null)
            return null;

        RoleState? role = null;
        PermissionSetState? callerPermissions = null;
        PermissionSetState? targetPermissions = null;

        if (roleId != Guid.Empty)
        {
            role = await host.LoadRoleAsync(roleId, ct)
                ?? throw new ArgumentException($"Role {roleId} not found.");

            if (user.RoleId != role.Id)
            {
                targetPermissions = role.PermissionSetId.HasValue
                    ? await host.LoadFullPermissionSetAsync(role.PermissionSetId.Value, ct)
                    : null;

                callerPermissions = user.Role?.PermissionSetId is { } callerPermissionSetId
                    ? await host.LoadFullPermissionSetAsync(callerPermissionSetId, ct)
                    : null;
            }
        }

        agents.AssignUserRole(
            user,
            roleId,
            role,
            callerPermissions,
            targetPermissions,
            host.ModuleRegistry.GetAllRegisteredResourceTypes());

        await host.SaveAsync(
            () => invalidations.UserHeaderChanged(userId),
            ct);
        return new MeResponse(
            user.Id,
            user.Username,
            user.Bio,
            user.RoleId,
            user.Role?.Name);
    }

    public async Task<IReadOnlyList<AgentResponse>> SyncWithModelsAsync(
        IAgentAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var models = await host.LoadChatCapableModelsAsync(ct);
        var existingNames = await host.ListAgentNamesAsync(null, ct);
        var nameSet = new HashSet<string>(
            existingNames,
            StringComparer.OrdinalIgnoreCase);
        var created = new List<AgentResponse>();

        foreach (var model in models)
        {
            var plugin = host.GetProviderPlugin(model.Provider.ProviderKey)
                ?? throw new InvalidOperationException(
                    $"Cannot synthesise default agent for model '{model.Name}' "
                    + $"(provider '{model.Provider.Name}', key '{model.Provider.ProviderKey}'): "
                    + "no provider plugin is registered. Ensure the owning module is "
                    + "loaded and enabled before running agent sync.");

            var providerSuffix = await plugin.GetAgentIdentifierSuffixAsync(
                model.Provider.Name,
                model.Id,
                ct);

            var agent = agents.CreateDefaultAgentIfMissing(
                model,
                providerSuffix,
                nameSet);
            if (agent is null)
                continue;

            host.TrackAgent(agent);
            await host.SaveAsync(null, ct);
            created.Add(agents.ToResponse(agent, model));
        }

        return created;
    }

    public async Task<bool> DeleteAsync(
        Guid agentId,
        IAgentAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var agent = await host.LoadAgentAsync(agentId, ct);
        if (agent is null)
            return false;

        host.RemoveAgent(agent);
        await host.SaveAsync(
            () => invalidations.AgentChanged(agentId),
            ct);
        return true;
    }

    private async Task EnsureAgentNameUniqueAsync(
        string name,
        Guid? excludeId,
        IAgentAdministrationHost host,
        CancellationToken ct)
    {
        var names = await host.ListAgentNamesAsync(excludeId, ct);
        agents.EnsureAgentNameAvailable(name, names);
    }
}

/// <summary>
/// Persistence and integration boundary used by agent administration workflow.
/// </summary>
public interface IAgentAdministrationHost
{
    bool UniqueAgentNamesEnforced { get; }

    Guid? SessionUserId { get; }

    ModuleRegistry ModuleRegistry { get; }

    ICompletionParameterSpec GetParameterSpec(string providerKey);

    IProviderPlugin? GetProviderPlugin(string providerKey);

    Task<ModelState?> LoadModelAsync(Guid modelId, CancellationToken ct);

    Task<AgentState?> LoadAgentAsync(Guid agentId, CancellationToken ct);

    Task<RoleState?> LoadRoleAsync(Guid roleId, CancellationToken ct);

    Task<UserState?> LoadUserAsync(Guid userId, CancellationToken ct);

    Task<PermissionSetState?> LoadFullPermissionSetAsync(
        Guid permissionSetId,
        CancellationToken ct);

    Task<IReadOnlyList<ModelState>> LoadChatCapableModelsAsync(
        CancellationToken ct);

    Task<IReadOnlyList<string>> ListAgentNamesAsync(
        Guid? excludeId,
        CancellationToken ct);

    void TrackAgent(AgentState agent);

    void RemoveAgent(AgentState agent);

    Task SaveAsync(
        Func<ChatRuntimeInvalidationPlan?>? buildInvalidationPlan,
        CancellationToken ct);
}
