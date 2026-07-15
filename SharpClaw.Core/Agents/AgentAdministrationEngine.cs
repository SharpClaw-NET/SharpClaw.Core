using SharpClaw.Core.State;
using System.Linq.Expressions;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Clients;

namespace SharpClaw.Core.Agents;

/// <summary>
/// Store-neutral agent administration rules used by SharpClaw runtimes.
/// Hosts supply loaded entities and persistence; Core owns the state
/// transitions, validation, and public projections.
/// </summary>
public sealed class AgentAdministrationEngine
{
    /// <summary>Returns whether unique-name enforcement should be active.</summary>
    public static bool IsUniqueAgentNameEnforced(string? configurationValue)
    {
        return configurationValue is null
            || !bool.TryParse(configurationValue, out var enforced)
            || enforced;
    }

    /// <summary>Throws when an agent name already exists.</summary>
    public void EnsureAgentNameAvailable(
        string name,
        IEnumerable<string> existingAgentNames)
    {
        ArgumentNullException.ThrowIfNull(existingAgentNames);

        var normalized = name.Trim();
        if (existingAgentNames.Any(existing =>
                existing.Trim().Equals(
                    normalized,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"An agent named '{name}' already exists.");
        }
    }

    /// <summary>Creates an agent entity from a validated create request.</summary>
    public AgentState Create(
        CreateAgentRequest request,
        ModelState model,
        ICompletionParameterSpec parameterSpec)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(parameterSpec);

        ValidateCompletionParameters(
            ToCompletionParameters(request),
            parameterSpec,
            model.Provider.ProviderKey);

        return new AgentState
        {
            Name = request.Name,
            SystemPrompt = request.SystemPrompt,
            MaxCompletionTokens = request.MaxCompletionTokens,
            ModelId = model.Id,
            Model = model,
            CustomId = request.CustomId,
            Temperature = request.Temperature,
            TopP = request.TopP,
            TopK = request.TopK,
            FrequencyPenalty = request.FrequencyPenalty,
            PresencePenalty = request.PresencePenalty,
            Stop = request.Stop,
            Seed = request.Seed,
            ResponseFormat = request.ResponseFormat,
            ReasoningEffort = request.ReasoningEffort,
            ProviderParameters = request.ProviderParameters,
            ToolAwarenessSetId = request.ToolAwarenessSetId,
            DisableToolSchemas = request.DisableToolSchemas ?? false,
        };
    }

    /// <summary>Applies an update request to an existing agent entity.</summary>
    public void ApplyUpdate(
        AgentState agent,
        UpdateAgentRequest request,
        ModelState? replacementModel,
        ICompletionParameterSpec parameterSpec,
        bool enforceUniqueNames,
        IEnumerable<string> existingAgentNames)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(parameterSpec);
        ArgumentNullException.ThrowIfNull(existingAgentNames);

        if (request.Name is not null)
        {
            if (enforceUniqueNames
                && !request.Name.Trim().Equals(
                    agent.Name.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                EnsureAgentNameAvailable(request.Name, existingAgentNames);
            }

            agent.Name = request.Name;
        }

        if (request.SystemPrompt is not null)
            agent.SystemPrompt = request.SystemPrompt;
        if (request.MaxCompletionTokens is not null)
            agent.MaxCompletionTokens = request.MaxCompletionTokens;
        if (request.CustomId is not null)
            agent.CustomId = request.CustomId;
        if (request.Temperature is not null)
            agent.Temperature = request.Temperature;
        if (request.TopP is not null)
            agent.TopP = request.TopP;
        if (request.TopK is not null)
            agent.TopK = request.TopK;
        if (request.FrequencyPenalty is not null)
            agent.FrequencyPenalty = request.FrequencyPenalty;
        if (request.PresencePenalty is not null)
            agent.PresencePenalty = request.PresencePenalty;
        if (request.Stop is not null)
            agent.Stop = request.Stop.Length > 0 ? request.Stop : null;
        if (request.Seed is not null)
            agent.Seed = request.Seed;
        if (request.ResponseFormat is not null)
            agent.ResponseFormat = request.ResponseFormat;
        if (request.ReasoningEffort is not null)
            agent.ReasoningEffort = request.ReasoningEffort;
        if (request.ProviderParameters is not null)
            agent.ProviderParameters = request.ProviderParameters.Count > 0
                ? request.ProviderParameters
                : null;
        if (request.CustomChatHeader is not null)
            agent.CustomChatHeader = request.CustomChatHeader.Length > 0
                ? request.CustomChatHeader
                : null;
        if (request.ToolAwarenessSetId is not null)
            agent.ToolAwarenessSetId = request.ToolAwarenessSetId == Guid.Empty
                ? null
                : request.ToolAwarenessSetId;
        if (request.DisableToolSchemas is not null)
            agent.DisableToolSchemas = request.DisableToolSchemas.Value;
        if (request.ModelId is { } modelId)
        {
            var model = replacementModel
                ?? throw new ArgumentException($"Model {modelId} not found.");
            agent.ModelId = model.Id;
            agent.Model = model;
        }

        ValidateCompletionParameters(
            ToCompletionParameters(agent),
            parameterSpec,
            agent.Model.Provider.ProviderKey);
    }

    /// <summary>Assigns or removes a role on an agent.</summary>
    public void AssignRole(
        AgentState agent,
        Guid roleId,
        RoleState? role,
        Guid? callerRoleId,
        PermissionSetState? callerPermissionSet,
        PermissionSetState? targetPermissionSet,
        IEnumerable<string> registeredResourceTypes)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(registeredResourceTypes);

        if (roleId == Guid.Empty)
        {
            agent.RoleId = null;
            agent.Role = null;
            return;
        }

        if (role is null)
            throw new ArgumentException($"Role {roleId} not found.");

        if (callerRoleId != role.Id)
        {
            ValidateCallerCoversTargetRole(
                callerPermissionSet,
                targetPermissionSet,
                role.Name,
                registeredResourceTypes);
        }

        agent.RoleId = role.Id;
        agent.Role = role;
    }

    /// <summary>Assigns or removes a role on a user.</summary>
    public void AssignUserRole(
        UserState user,
        Guid roleId,
        RoleState? role,
        PermissionSetState? callerPermissionSet,
        PermissionSetState? targetPermissionSet,
        IEnumerable<string> registeredResourceTypes)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(registeredResourceTypes);

        if (roleId == Guid.Empty)
        {
            user.RoleId = null;
            user.Role = null;
            return;
        }

        if (role is null)
            throw new ArgumentException($"Role {roleId} not found.");

        if (user.RoleId != role.Id)
        {
            ValidateCallerCoversTargetRole(
                callerPermissionSet,
                targetPermissionSet,
                role.Name,
                registeredResourceTypes);
        }

        user.RoleId = role.Id;
        user.Role = role;
    }

    /// <summary>Builds the stable default-agent name for a model.</summary>
    public string BuildDefaultAgentName(string modelName, string providerSuffix)
    {
        return $"default-{modelName}-{providerSuffix}";
    }

    /// <summary>
    /// Creates a default agent when the synthesized name is not already known.
    /// </summary>
    public AgentState? CreateDefaultAgentIfMissing(
        ModelState model,
        string providerSuffix,
        ISet<string> knownAgentNames)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(knownAgentNames);

        var agentName = BuildDefaultAgentName(model.Name, providerSuffix);
        if (knownAgentNames.Contains(agentName))
            return null;

        knownAgentNames.Add(agentName);
        return new AgentState
        {
            Name = agentName,
            ModelId = model.Id,
            Model = model
        };
    }

    /// <summary>Projects an agent entity into its public response shape.</summary>
    public AgentResponse ToResponse(AgentState agent, ModelState? model = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        model ??= agent.Model;

        return new AgentResponse(
            agent.Id,
            agent.Name,
            agent.SystemPrompt,
            model.Id,
            model.Name,
            model.Provider.Name,
            agent.RoleId,
            agent.Role?.Name,
            agent.MaxCompletionTokens,
            agent.CustomId,
            agent.Temperature,
            agent.TopP,
            agent.TopK,
            agent.FrequencyPenalty,
            agent.PresencePenalty,
            agent.Stop,
            agent.Seed,
            agent.ResponseFormat,
            agent.ReasoningEffort,
            agent.ProviderParameters,
            agent.CustomChatHeader,
            agent.ToolAwarenessSetId,
            agent.DisableToolSchemas);
    }

    /// <summary>
    /// Returns an EF-translatable projection for list/read paths that should
    /// avoid materializing full relation graphs.
    /// </summary>
    public Expression<Func<AgentState, AgentResponse>> ToResponseProjection() =>
        agent => new AgentResponse(
            agent.Id,
            agent.Name,
            agent.SystemPrompt,
            agent.ModelId,
            agent.Model.Name,
            agent.Model.Provider.Name,
            agent.RoleId,
            agent.Role != null ? agent.Role.Name : null,
            agent.MaxCompletionTokens,
            agent.CustomId,
            agent.Temperature,
            agent.TopP,
            agent.TopK,
            agent.FrequencyPenalty,
            agent.PresencePenalty,
            agent.Stop,
            agent.Seed,
            agent.ResponseFormat,
            agent.ReasoningEffort,
            agent.ProviderParameters,
            agent.CustomChatHeader,
            agent.ToolAwarenessSetId,
            agent.DisableToolSchemas);

    /// <summary>Projects an agent entity into its compact summary shape.</summary>
    public AgentSummary ToSummary(AgentState agent, ModelState? model = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        model ??= agent.Model;

        return new AgentSummary(
            agent.Id,
            agent.Name,
            model.Id,
            model.Name,
            model.Provider.Name,
            agent.RoleId,
            agent.Role?.Name,
            agent.MaxCompletionTokens,
            agent.CustomId,
            agent.Temperature,
            agent.TopP,
            agent.TopK,
            agent.FrequencyPenalty,
            agent.PresencePenalty,
            agent.Stop,
            agent.Seed,
            agent.ResponseFormat,
            agent.ReasoningEffort,
            agent.ProviderParameters,
            agent.CustomChatHeader,
            agent.ToolAwarenessSetId,
            agent.DisableToolSchemas);
    }

    /// <summary>
    /// Verifies that the caller covers every target role permission at the
    /// same or higher clearance level.
    /// </summary>
    public void ValidateCallerCoversTargetRole(
        PermissionSetState? callerPermissionSet,
        PermissionSetState? targetPermissionSet,
        string roleName,
        IEnumerable<string> registeredResourceTypes)
    {
        ArgumentNullException.ThrowIfNull(registeredResourceTypes);

        if (targetPermissionSet is null)
            return;

        if (callerPermissionSet is null)
            throw new UnauthorizedAccessException(
                $"You have no permissions \u2014 cannot assign the '{roleName}' role.");

        foreach (var targetFlag in targetPermissionSet.GlobalFlags)
        {
            if (!callerPermissionSet.GlobalFlags.Any(f => f.FlagKey == targetFlag.FlagKey))
                Deny(roleName, targetFlag.FlagKey);
        }

        foreach (var resourceType in registeredResourceTypes)
        {
            ValidateResourceCoverage(
                roleName,
                resourceType,
                targetPermissionSet.ResourceAccesses,
                callerPermissionSet.ResourceAccesses);
        }
    }

    /// <summary>Validates completion parameters against provider constraints.</summary>
    public void ValidateCompletionParameters(
        CompletionParameters parameters,
        ICompletionParameterSpec parameterSpec,
        string providerKey)
    {
        CompletionParameterValidator.ValidateOrThrow(
            parameters,
            parameterSpec,
            providerKey);
    }

    private static CompletionParameters ToCompletionParameters(
        CreateAgentRequest request) =>
        new()
        {
            Temperature = request.Temperature,
            TopP = request.TopP,
            TopK = request.TopK,
            FrequencyPenalty = request.FrequencyPenalty,
            PresencePenalty = request.PresencePenalty,
            Stop = request.Stop,
            Seed = request.Seed,
            ResponseFormat = request.ResponseFormat,
            ReasoningEffort = request.ReasoningEffort,
        };

    private static CompletionParameters ToCompletionParameters(AgentState agent) =>
        new()
        {
            Temperature = agent.Temperature,
            TopP = agent.TopP,
            TopK = agent.TopK,
            FrequencyPenalty = agent.FrequencyPenalty,
            PresencePenalty = agent.PresencePenalty,
            Stop = agent.Stop,
            Seed = agent.Seed,
            ResponseFormat = agent.ResponseFormat,
            ReasoningEffort = agent.ReasoningEffort,
        };

    private static void ValidateResourceCoverage(
        string roleName,
        string resourceType,
        ICollection<ResourceAccessState> targetAccesses,
        ICollection<ResourceAccessState> callerAccesses)
    {
        var targetFiltered = targetAccesses
            .Where(a => a.ResourceType == resourceType)
            .ToList();
        var callerFiltered = callerAccesses
            .Where(a => a.ResourceType == resourceType)
            .ToList();

        if (targetFiltered.Count > 0 && callerFiltered.Count == 0)
            throw new UnauthorizedAccessException(
                $"Cannot assign '{roleName}': you hold no {resourceType} grants.");

        if (targetFiltered.Count == 0)
            return;

        var callerMap = new Dictionary<Guid, PermissionClearance>();
        foreach (var access in callerFiltered)
            callerMap[access.ResourceId] = access.Clearance;

        var callerHasWildcard = callerMap.TryGetValue(
            WellKnownIds.AllResources,
            out var wildcardClearance);

        foreach (var target in targetFiltered)
        {
            PermissionClearance callerClearance;
            if (callerMap.TryGetValue(target.ResourceId, out var exact))
                callerClearance = exact;
            else if (callerHasWildcard)
                callerClearance = wildcardClearance;
            else
                throw new UnauthorizedAccessException(
                    $"Cannot assign '{roleName}': you lack {resourceType} " +
                    $"for resource {target.ResourceId}.");

            if (target.Clearance > callerClearance)
                throw new UnauthorizedAccessException(
                    $"Cannot assign '{roleName}': {resourceType} for resource " +
                    $"{target.ResourceId} requires clearance {target.Clearance} but " +
                    $"you only have {callerClearance}.");
        }
    }

    private static void Deny(string roleName, string flag) =>
        throw new UnauthorizedAccessException(
            $"Cannot assign '{roleName}': you do not hold {flag}.");
}
