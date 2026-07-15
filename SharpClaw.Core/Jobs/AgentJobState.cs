using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.Jobs;

/// <summary>
/// Host-independent state used by the job lifecycle engines. Runtime hosts
/// map this state to their selected persistence model at the port boundary.
/// </summary>
public sealed class AgentJobState
{
    public Guid Id { get; set; }

    public Guid AgentId { get; set; }

    public Guid ChannelId { get; set; }

    public Guid? CallerUserId { get; set; }

    public Guid? CallerAgentId { get; set; }

    public string? ActionKey { get; set; }

    public Guid? ResourceId { get; set; }

    public string? ScriptJson { get; set; }

    public string? WorkingDirectory { get; set; }

    public AgentJobStatus Status { get; set; } = AgentJobStatus.Queued;

    public PermissionClearance EffectiveClearance { get; set; } =
        PermissionClearance.Unset;

    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Guid? ApprovedByUserId { get; set; }

    public Guid? ApprovedByAgentId { get; set; }
}

/// <summary>
/// Host-supplied channel facts required to submit or approve a job. The
/// lifecycle engine does not receive a persistence entity or navigation graph.
/// </summary>
public sealed record AgentJobChannelContext(
    Guid ChannelId,
    Guid? AgentId,
    Guid? ContextAgentId,
    IReadOnlySet<Guid> AllowedAgentIds,
    Guid? PermissionSetId,
    Guid? ContextPermissionSetId);
