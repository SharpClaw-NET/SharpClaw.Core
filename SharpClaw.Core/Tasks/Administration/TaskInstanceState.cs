using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.Tasks.Administration;

/// <summary>
/// Host-independent state for one task execution. Runtime hosts map this
/// state to their selected persistence model at the administration port.
/// </summary>
public sealed class TaskInstanceState
{
    public Guid Id { get; set; }

    public Guid TaskDefinitionId { get; set; }

    public TaskInstanceStatus Status { get; set; } = TaskInstanceStatus.Queued;

    public string? ParameterValuesJson { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Guid? CallerUserId { get; set; }

    public Guid? CallerAgentId { get; set; }

    public Guid? ChannelId { get; set; }

    public Guid? ContextId { get; set; }
}
