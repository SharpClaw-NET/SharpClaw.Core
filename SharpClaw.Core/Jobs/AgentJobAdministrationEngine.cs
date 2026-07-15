using SharpClaw.Core.State;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Permissions;

namespace SharpClaw.Core.Jobs;

/// <summary>
/// Store-neutral job administration rules used by SharpClaw runtimes.
/// Hosts own persistence, module dispatch, and cache writes; Core owns job
/// state construction, effective-agent checks, projection, lifecycle mutation,
/// channel-preauthorization gates, and token allocation.
/// </summary>
public sealed class AgentJobAdministrationEngine
{
    /// <summary>
    /// Resolves the agent that should execute a submitted job for a channel.
    /// </summary>
    public Guid ResolveSubmissionAgent(
        AgentJobChannelContext channel,
        Guid channelId,
        Guid? requestedAgentId)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var agentId = channel.AgentId ?? channel.ContextAgentId
            ?? throw new InvalidOperationException(
                $"Channel {channelId} has no agent and no context agent.");

        if (requestedAgentId is not { } requestedAgent || requestedAgent == agentId)
            return agentId;

        if (!channel.AllowedAgentIds.Contains(requestedAgent))
            throw new InvalidOperationException(
                $"Agent {requestedAgent} is not allowed on channel {channelId}. " +
                "Add it to the channel's or context's allowed agents first.");

        return requestedAgent;
    }

    /// <summary>Creates host-independent state for a submitted action.</summary>
    public AgentJobState CreateSubmissionState(
        Guid channelId,
        Guid agentId,
        SubmitAgentJobRequest request,
        Guid? callerUserId,
        Guid? effectiveResourceId)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AgentJobState
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            AgentId = agentId,
            ChannelId = channelId,
            CallerUserId = callerUserId,
            CallerAgentId = request.CallerAgentId,
            ActionKey = request.ActionKey,
            ResourceId = effectiveResourceId,
            ScriptJson = request.ScriptJson,
            WorkingDirectory = request.WorkingDirectory,
        };
    }

    /// <summary>
    /// Determines whether a registered action key requires a per-resource grant.
    /// </summary>
    public bool IsPerResourceAction(
        ModuleRegistry moduleRegistry,
        string? actionKey)
    {
        ArgumentNullException.ThrowIfNull(moduleRegistry);

        if (string.IsNullOrWhiteSpace(actionKey))
            return false;

        if (!moduleRegistry.TryResolve(actionKey, out var moduleId, out var toolName))
            return false;

        var descriptor = moduleRegistry.GetPermissionDescriptor(moduleId, toolName);
        return descriptor?.IsPerResource ?? false;
    }

    /// <summary>Resolves the delegated permission method for an action key.</summary>
    public string? ResolveDelegateTo(
        ModuleRegistry moduleRegistry,
        string? actionKey)
    {
        ArgumentNullException.ThrowIfNull(moduleRegistry);

        if (string.IsNullOrWhiteSpace(actionKey))
            return null;

        if (!moduleRegistry.TryResolve(actionKey, out var moduleId, out var toolName))
            return null;

        var descriptor = moduleRegistry.GetPermissionDescriptor(moduleId, toolName);
        return descriptor?.DelegateTo;
    }

    /// <summary>
    /// Returns whether a permission set contains a grant matching the action key.
    /// </summary>
    public bool HasMatchingGrant(
        ModuleRegistry moduleRegistry,
        PermissionSetState permissionSet,
        Guid? resourceId,
        string? actionKey)
    {
        ArgumentNullException.ThrowIfNull(moduleRegistry);
        ArgumentNullException.ThrowIfNull(permissionSet);

        var delegateName = ResolveDelegateTo(moduleRegistry, actionKey);
        return delegateName is not null
            && HasGrantByDelegateName(
                moduleRegistry,
                permissionSet,
                delegateName,
                resourceId);
    }

    /// <summary>
    /// Returns whether a permission set contains the grant mapped by a delegate name.
    /// </summary>
    public bool HasGrantByDelegateName(
        ModuleRegistry moduleRegistry,
        PermissionSetState permissionSet,
        string delegateName,
        Guid? resourceId)
    {
        ArgumentNullException.ThrowIfNull(moduleRegistry);
        ArgumentNullException.ThrowIfNull(permissionSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(delegateName);

        var snapshot = PermissionSetSnapshot.FromPermissionSet(permissionSet);
        var plan = PermissionDelegatePlanner.BuildPlan(
            delegateName,
            resourceId,
            moduleRegistry);

        return PermissionDelegatePlanner.HasGrant(snapshot, plan);
    }

    /// <summary>
    /// Validates the module stale-job action prefix with the historical host
    /// callback exception text.
    /// </summary>
    public void EnsureModuleCallbackActionPrefix(string actionKeyPrefix)
    {
        if (string.IsNullOrWhiteSpace(actionKeyPrefix))
            throw new ArgumentException(
                "Action key prefix is required.",
                nameof(actionKeyPrefix));
    }

    /// <summary>Returns whether a job action key matches a prefix.</summary>
    public bool JobMatchesActionPrefix(
        AgentJobState? job,
        string actionKeyPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKeyPrefix);
        return job?.ActionKey?.StartsWith(
            actionKeyPrefix,
            StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Applies only compact lifecycle state to the in-memory job. Result,
    /// failure, and log payloads remain on the decision for the host port.
    /// </summary>
    public void ApplyLifecycleState(
        AgentJobState job,
        AgentJobLifecycleDecision decision)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Status is { } status)
            job.Status = status;
        if (decision.UpdateStartedAt)
            job.StartedAt = decision.StartedAt;
        if (decision.UpdateCompletedAt)
            job.CompletedAt = decision.CompletedAt;
    }

    /// <summary>
    /// Returns whether a channel/context permission set can preauthorize a
    /// pending user-facing approval for the requested clearance.
    /// </summary>
    public bool CanUseChannelPreauthorization(
        PermissionClearance agentClearance)
    {
        return agentClearance is PermissionClearance.ApprovedBySameLevelUser
            or PermissionClearance.ApprovedByWhitelistedUser
            or PermissionClearance.ApprovedByWhitelistedAgent;
    }

    /// <summary>
    /// Returns whether the caller must personally hold the same grant before
    /// channel/context preauthorization can be used.
    /// </summary>
    public bool RequiresCallerGrantForChannelPreauthorization(
        PermissionClearance agentClearance)
    {
        return agentClearance == PermissionClearance.ApprovedBySameLevelUser;
    }

    /// <summary>
    /// Resolves whether channel/context grants can preauthorize a pending job.
    /// </summary>
    public AgentJobChannelPreauthorizationDecision EvaluateChannelPreauthorization(
        PermissionClearance agentClearance,
        bool callerHasGrant,
        bool channelHasGrant,
        bool contextHasGrant)
    {
        if (!CanUseChannelPreauthorization(agentClearance))
        {
            return new AgentJobChannelPreauthorizationDecision(
                IsPreauthorized: false,
                Source: AgentJobChannelPreauthorizationSource.NotApplicable,
                RequiresCallerGrant: false);
        }

        var requiresCallerGrant =
            RequiresCallerGrantForChannelPreauthorization(agentClearance);
        if (requiresCallerGrant && !callerHasGrant)
        {
            return new AgentJobChannelPreauthorizationDecision(
                IsPreauthorized: false,
                Source: AgentJobChannelPreauthorizationSource.CallerGrantMissing,
                RequiresCallerGrant: true);
        }

        if (channelHasGrant)
        {
            return new AgentJobChannelPreauthorizationDecision(
                IsPreauthorized: true,
                Source: AgentJobChannelPreauthorizationSource.Channel,
                RequiresCallerGrant: requiresCallerGrant);
        }

        if (contextHasGrant)
        {
            return new AgentJobChannelPreauthorizationDecision(
                IsPreauthorized: true,
                Source: AgentJobChannelPreauthorizationSource.Context,
                RequiresCallerGrant: requiresCallerGrant);
        }

        return new AgentJobChannelPreauthorizationDecision(
            IsPreauthorized: false,
            Source: AgentJobChannelPreauthorizationSource.NoGrant,
            RequiresCallerGrant: requiresCallerGrant);
    }

    /// <summary>Projects compact state and a transient outcome for its caller.</summary>
    public AgentJobResponse ToResponse(
        AgentJobState job,
        AgentJobExecutionOutcome? outcome = null)
    {
        ArgumentNullException.ThrowIfNull(job);

        var jobCost = job.PromptTokens is not null || job.CompletionTokens is not null
            ? new TokenUsageResponse(
                job.PromptTokens ?? 0,
                job.CompletionTokens ?? 0,
                (job.PromptTokens ?? 0) + (job.CompletionTokens ?? 0))
            : null;

        return new AgentJobResponse(
            Id: job.Id,
            ChannelId: job.ChannelId,
            AgentId: job.AgentId,
            ActionKey: job.ActionKey,
            ResourceId: job.ResourceId,
            Status: job.Status,
            EffectiveClearance: job.EffectiveClearance,
            ResultData: outcome?.ResultData,
            ErrorCode: outcome?.ErrorCode,
            ErrorMessage: outcome?.ErrorMessage,
            CreatedAt: job.CreatedAt,
            StartedAt: job.StartedAt,
            CompletedAt: job.CompletedAt,
            ScriptJson: job.ScriptJson,
            WorkingDirectory: job.WorkingDirectory,
            JobCost: jobCost);
    }

    /// <summary>Projects a job into the lightweight summary response.</summary>
    public AgentJobSummaryResponse ToSummaryResponse(AgentJobState job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new AgentJobSummaryResponse(
            job.Id,
            job.ChannelId,
            job.AgentId,
            job.ActionKey,
            job.ResourceId,
            job.Status,
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt);
    }

    /// <summary>
    /// Splits one LLM round's token usage across the jobs that participated
    /// in that round. Any remainder is assigned to the first job.
    /// </summary>
    public void ApplyTokenUsage(
        IReadOnlyList<AgentJobState> jobs,
        int promptTokens,
        int completionTokens)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        if (promptTokens < 0)
            throw new ArgumentOutOfRangeException(
                nameof(promptTokens),
                promptTokens,
                "Prompt tokens cannot be negative.");
        if (completionTokens < 0)
            throw new ArgumentOutOfRangeException(
                nameof(completionTokens),
                completionTokens,
                "Completion tokens cannot be negative.");
        if (jobs.Count == 0)
            return;

        var promptPer = promptTokens / jobs.Count;
        var completionPer = completionTokens / jobs.Count;
        var promptRemainder = promptTokens % jobs.Count;
        var completionRemainder = completionTokens % jobs.Count;

        for (var i = 0; i < jobs.Count; i++)
        {
            jobs[i].PromptTokens =
                (jobs[i].PromptTokens ?? 0)
                + promptPer
                + (i == 0 ? promptRemainder : 0);
            jobs[i].CompletionTokens =
                (jobs[i].CompletionTokens ?? 0)
                + completionPer
                + (i == 0 ? completionRemainder : 0);
        }
    }
}

/// <summary>
/// Store-neutral channel/context job preauthorization result.
/// </summary>
public sealed record AgentJobChannelPreauthorizationDecision(
    bool IsPreauthorized,
    AgentJobChannelPreauthorizationSource Source,
    bool RequiresCallerGrant);

/// <summary>
/// Explains how a channel/context job preauthorization decision was reached.
/// </summary>
public enum AgentJobChannelPreauthorizationSource
{
    /// <summary>The requested clearance cannot be channel-preauthorized.</summary>
    NotApplicable,

    /// <summary>The caller lacked the same grant required for level-one preauthorization.</summary>
    CallerGrantMissing,

    /// <summary>The channel permission set preauthorized the job.</summary>
    Channel,

    /// <summary>The parent context permission set preauthorized the job.</summary>
    Context,

    /// <summary>No channel or context grant matched the job action.</summary>
    NoGrant
}
