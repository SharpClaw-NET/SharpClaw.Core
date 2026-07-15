using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.Tasks.Runtime;

/// <summary>
/// Produces the canonical log and output-event plans for task runtime lifecycle
/// transitions.
/// </summary>
public sealed class TaskRuntimeLifecycleEngine
{
    /// <summary>
    /// Builds the side effects emitted after a task instance enters Running.
    /// </summary>
    public TaskRuntimeEventPlan BuildStartedPlan()
        => new(
            "Task started.",
            JobLogLevels.Info,
            [new TaskRuntimeOutputEventPlan(TaskOutputEventType.StatusChange, "Running")]);

    /// <summary>
    /// Builds the side effects emitted after a running task instance is paused.
    /// </summary>
    public TaskRuntimeEventPlan BuildPausedPlan()
        => new(
            "Task paused.",
            JobLogLevels.Info,
            [new TaskRuntimeOutputEventPlan(TaskOutputEventType.StatusChange, "Paused")]);

    /// <summary>
    /// Builds the side effects emitted after a paused task instance resumes.
    /// </summary>
    public TaskRuntimeEventPlan BuildResumedPlan()
        => new(
            "Task resumed.",
            JobLogLevels.Info,
            [new TaskRuntimeOutputEventPlan(TaskOutputEventType.StatusChange, "Running")]);

    /// <summary>
    /// Builds the side effects emitted when shared task data changes.
    /// </summary>
    public TaskRuntimeEventPlan BuildSharedDataChangedPlan(string description)
    {
        var message = $"SharedData: {description}";
        return new(
            message,
            JobLogLevels.Info,
            [new TaskRuntimeOutputEventPlan(TaskOutputEventType.Log, message)]);
    }

    /// <summary>
    /// Builds the side effects emitted when a task instance reaches a
    /// non-failed terminal state.
    /// </summary>
    public TaskRuntimeEventPlan BuildTerminalPlan(TaskInstanceStatus status)
        => new(
            $"Task {status}.",
            JobLogLevels.Info,
            [
                new TaskRuntimeOutputEventPlan(
                    TaskOutputEventType.StatusChange,
                    status.ToString()),
                new TaskRuntimeOutputEventPlan(TaskOutputEventType.Done, null)
            ]);

    /// <summary>
    /// Builds the side effects emitted when a task instance fails.
    /// </summary>
    public TaskRuntimeEventPlan BuildFailurePlan(string error)
        => new(
            $"Task failed: {error}",
            JobLogLevels.Error,
            [
                new TaskRuntimeOutputEventPlan(
                    TaskOutputEventType.StatusChange,
                    $"Failed: {error}"),
                new TaskRuntimeOutputEventPlan(TaskOutputEventType.Done, null)
            ]);
}

/// <summary>
/// Host-neutral description of task runtime log and stream side effects.
/// </summary>
public sealed record TaskRuntimeEventPlan(
    string? LogMessage,
    string LogLevel,
    IReadOnlyList<TaskRuntimeOutputEventPlan> OutputEvents);

/// <summary>
/// Host-neutral description of one task output stream event.
/// </summary>
public sealed record TaskRuntimeOutputEventPlan(
    TaskOutputEventType Type,
    string? Data);
