using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Modules;

namespace SharpClaw.Core.Jobs;

/// <summary>
/// Store-neutral orchestration for job submission, approval, execution, and
/// lifecycle persistence timing.
/// </summary>
public sealed class AgentJobRuntimeEngine(
    AgentJobLifecycleEngine lifecycle,
    AgentJobAdministrationEngine jobs)
{
    public async Task<AgentJobResponse> SubmitAsync(
        Guid channelId,
        SubmitAgentJobRequest request,
        IAgentJobRuntimeHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var channel = await host.LoadSubmissionChannelAsync(channelId, ct)
            ?? throw new InvalidOperationException(
                $"Channel {channelId} not found.");

        var agentId = jobs.ResolveSubmissionAgent(
            channel,
            channelId,
            request.AgentId);

        var effectiveResourceId = request.ResourceId;
        if (!effectiveResourceId.HasValue
            && jobs.IsPerResourceAction(host.ModuleRegistry, request.ActionKey))
        {
            effectiveResourceId = await host.ResolveDefaultResourceIdAsync(
                request.ActionKey,
                channelId,
                agentId,
                ct);
        }

        var job = jobs.CreateSubmissionState(
            channelId,
            agentId,
            request,
            host.SessionUserId,
            effectiveResourceId);

        host.TrackJob(job);
        await ApplyAndPersistAsync(
            job,
            lifecycle.Queue(request.ActionKey),
            host,
            ct);

        var caller = new ActionCaller(
            host.SessionUserId,
            request.CallerAgentId);
        var permission = await host.DispatchPermissionCheckAsync(
            agentId,
            job.ResourceId,
            caller,
            job.ActionKey,
            channel.PermissionSetId,
            channel.ContextPermissionSetId,
            ct);

        job.EffectiveClearance = permission.EffectiveClearance;
        var channelPreauthorized =
            permission.Verdict == ClearanceVerdict.PendingApproval
            && await host.HasChannelAuthorizationAsync(
                channelId,
                job.ResourceId,
                permission.EffectiveClearance,
                host.SessionUserId,
                job.ActionKey,
                ct);

        var decision = lifecycle.ResolveSubmissionPermission(
            permission,
            channelPreauthorized);
        await ApplyAndPersistAsync(job, decision, host, ct);

        var outcome = decision.ShouldExecute
            ? await ExecuteAsync(job, host, ct)
            : AgentJobExecutionOutcome.Empty;
        return jobs.ToResponse(job, outcome);
    }

    public async Task<AgentJobResponse> ApproveAsync(
        AgentJobState job,
        ApproveAgentJobRequest request,
        IAgentJobRuntimeHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        if (job.Status != AgentJobStatus.AwaitingApproval)
        {
            await ApplyAndPersistAsync(
                job,
                lifecycle.RejectApprovalForStatus(job.Status),
                host,
                ct);
            return jobs.ToResponse(job);
        }

        var approver = new ActionCaller(
            host.SessionUserId,
            request.ApproverAgentId);
        var channel = await host.LoadApprovalChannelAsync(job.ChannelId, ct);
        var permission = await host.DispatchPermissionCheckAsync(
            job.AgentId,
            job.ResourceId,
            approver,
            job.ActionKey,
            channel?.PermissionSetId,
            channel?.ContextPermissionSetId,
            ct);

        var decision = lifecycle.ResolveApproval(
            permission,
            approver,
            DateTimeOffset.UtcNow);
        if (decision.ShouldExecute)
        {
            job.ApprovedByUserId = host.SessionUserId;
            job.ApprovedByAgentId = request.ApproverAgentId;
        }

        await ApplyAndPersistAsync(job, decision, host, ct);
        var outcome = decision.ShouldExecute
            ? await ExecuteAsync(job, host, ct)
            : AgentJobExecutionOutcome.Empty;
        return jobs.ToResponse(job, outcome);
    }

    /// <summary>
    /// Executes a job while the host owns dispatch diagnostics and durable
    /// persistence of every emitted lifecycle decision.
    /// </summary>
    public async Task<AgentJobExecutionOutcome> ExecuteAsync(
        AgentJobState job,
        IAgentJobRuntimeHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(host);

        await ApplyAndPersistAsync(
            job,
            lifecycle.BeginExecution(DateTimeOffset.UtcNow),
            host,
            ct);

        try
        {
            var execution = await host.DispatchExecutionAsync(job, ct);
            var completion = lifecycle.CompleteExecution(
                execution.ResultData,
                execution.CompletionBehavior,
                DateTimeOffset.UtcNow);
            await ApplyAndPersistAsync(job, completion, host, ct);

            if (execution.CompletionBehavior
                == ModuleJobCompletionBehavior.RemainExecuting)
            {
                host.LogLongRunningExecutionStarted(job);
            }

            return new AgentJobExecutionOutcome(
                execution.ResultData,
                ErrorCode: null,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            var failure = lifecycle.FailExecution(
                ex.Message,
                ex.ToString(),
                DateTimeOffset.UtcNow);
            await ApplyAndPersistAsync(job, failure, host, ct);
            host.LogExecutionFailed(job, ex);
            return new AgentJobExecutionOutcome(
                ResultData: null,
                failure.ErrorCode,
                failure.ErrorMessage);
        }
    }

    private async Task ApplyAndPersistAsync(
        AgentJobState job,
        AgentJobLifecycleDecision decision,
        IAgentJobRuntimeHost host,
        CancellationToken ct)
    {
        jobs.ApplyLifecycleState(job, decision);
        await host.PersistDecisionAsync(job, decision, ct);
    }
}

/// <summary>
/// Host capabilities required by the storage-neutral job runtime.
/// </summary>
public interface IAgentJobRuntimeHost
{
    Guid? SessionUserId { get; }

    ModuleRegistry ModuleRegistry { get; }

    Task<AgentJobChannelContext?> LoadSubmissionChannelAsync(
        Guid channelId,
        CancellationToken ct);

    Task<AgentJobChannelContext?> LoadApprovalChannelAsync(
        Guid channelId,
        CancellationToken ct);

    Task<Guid?> ResolveDefaultResourceIdAsync(
        string? actionKey,
        Guid channelId,
        Guid agentId,
        CancellationToken ct);

    void TrackJob(AgentJobState job);

    Task PersistDecisionAsync(
        AgentJobState job,
        AgentJobLifecycleDecision decision,
        CancellationToken ct);

    Task<AgentActionResult> DispatchPermissionCheckAsync(
        Guid agentId,
        Guid? resourceId,
        ActionCaller caller,
        string? actionKey,
        Guid? channelPermissionSetId,
        Guid? contextPermissionSetId,
        CancellationToken ct);

    Task<bool> HasChannelAuthorizationAsync(
        Guid channelId,
        Guid? resourceId,
        PermissionClearance agentClearance,
        Guid? callerUserId,
        string? actionKey,
        CancellationToken ct);

    Task<AgentJobExecutionDispatchResult> DispatchExecutionAsync(
        AgentJobState job,
        CancellationToken ct);

    void LogLongRunningExecutionStarted(AgentJobState job);

    void LogExecutionFailed(AgentJobState job, Exception exception);
}

/// <summary>Transient execution data returned only to the invoking caller.</summary>
public sealed record AgentJobExecutionOutcome(
    string? ResultData,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static AgentJobExecutionOutcome Empty { get; } =
        new(null, null, null);
}

/// <summary>Store-neutral result of host-owned module dispatch.</summary>
public sealed record AgentJobExecutionDispatchResult(
    string? ResultData,
    ModuleJobCompletionBehavior CompletionBehavior);
