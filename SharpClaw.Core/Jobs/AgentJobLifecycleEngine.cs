using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Jobs;

/// <summary>
/// Store-neutral implementation of SharpClaw agent job lifecycle rules.
/// Hosts own persistence and execution dispatch; Core owns allowed
/// transitions, target status fields, and lifecycle log text.
/// </summary>
public sealed class AgentJobLifecycleEngine
{
    /// <summary>Creates the initial queued status and queue log for a new job.</summary>
    public AgentJobLifecycleDecision Queue(string? actionKey) =>
        new()
        {
            Status = AgentJobStatus.Queued,
            Logs = [Info($"Job queued: {actionKey ?? "unknown"}.")]
        };

    /// <summary>
    /// Resolves the post-submission permission result into either immediate
    /// execution, awaiting approval, or denial.
    /// </summary>
    public AgentJobLifecycleDecision ResolveSubmissionPermission(
        AgentActionResult result,
        bool channelPreauthorized)
    {
        return result.Verdict switch
        {
            ClearanceVerdict.Approved => new AgentJobLifecycleDecision
            {
                ShouldExecute = true,
                Logs = [Info($"Permission granted: {result.Reason}")]
            },
            ClearanceVerdict.PendingApproval when channelPreauthorized => new AgentJobLifecycleDecision
            {
                ShouldExecute = true,
                Logs = [Info("Pre-authorized by channel/context permission set.")]
            },
            ClearanceVerdict.PendingApproval => new AgentJobLifecycleDecision
            {
                Status = AgentJobStatus.AwaitingApproval,
                Logs = [Info($"Awaiting approval: {result.Reason}")]
            },
            _ => new AgentJobLifecycleDecision
            {
                Status = AgentJobStatus.Denied,
                Logs = [Warning($"Denied: {result.Reason}")]
            }
        };
    }

    /// <summary>Rejects approval attempts for jobs that are not awaiting approval.</summary>
    public AgentJobLifecycleDecision RejectApprovalForStatus(AgentJobStatus status) =>
        new()
        {
            Logs =
            [
                Warning(
                    $"Approve rejected: job is {status}, not AwaitingApproval.")
            ]
        };

    /// <summary>
    /// Resolves an approval permission result into execution, a warning-only
    /// insufficient approval attempt, or a denied terminal state.
    /// </summary>
    public AgentJobLifecycleDecision ResolveApproval(
        AgentActionResult result,
        ActionCaller approver,
        DateTimeOffset now)
    {
        var formattedApprover = FormatCaller(approver);
        return result.Verdict switch
        {
            ClearanceVerdict.Approved => new AgentJobLifecycleDecision
            {
                ShouldExecute = true,
                Logs = [Info($"Approved by {formattedApprover}: {result.Reason}")]
            },
            ClearanceVerdict.PendingApproval => new AgentJobLifecycleDecision
            {
                Logs =
                [
                    Warning(
                        $"Approval attempt by {formattedApprover} insufficient: {result.Reason}")
                ]
            },
            _ => new AgentJobLifecycleDecision
            {
                Status = AgentJobStatus.Denied,
                UpdateCompletedAt = true,
                CompletedAt = now,
                Logs =
                [
                    Warning(
                        "Denied: agent permission revoked. "
                        + $"Attempt by {formattedApprover}: {result.Reason}")
                ]
            }
        };
    }

    /// <summary>Creates a cancellation transition or a warning when already terminal.</summary>
    public AgentJobLifecycleDecision Cancel(AgentJobStatus status, DateTimeOffset now)
    {
        if (IsTerminal(status))
        {
            return new AgentJobLifecycleDecision
            {
                Logs = [Warning($"Cancel rejected: job is already {status}.")]
            };
        }

        return new AgentJobLifecycleDecision
        {
            Status = AgentJobStatus.Cancelled,
            UpdateCompletedAt = true,
            CompletedAt = now,
            Logs = [Info("Job cancelled.")]
        };
    }

    /// <summary>
    /// Creates a stop transition for executing or paused jobs, including the
    /// optional action-prefix guard used by module lifecycle controllers.
    /// </summary>
    public AgentJobLifecycleDecision Stop(
        AgentJobStatus status,
        string? actionKey,
        string? requiredActionPrefix,
        DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(requiredActionPrefix)
            && actionKey?.StartsWith(requiredActionPrefix, StringComparison.OrdinalIgnoreCase) != true)
        {
            return new AgentJobLifecycleDecision
            {
                Logs =
                [
                    Warning(
                        "Stop rejected: job action does not match the requested action prefix.")
                ]
            };
        }

        if (status is not AgentJobStatus.Executing and not AgentJobStatus.Paused)
        {
            return new AgentJobLifecycleDecision
            {
                Logs =
                [
                    Warning(
                        $"Stop rejected: job is {status}, not Executing or Paused.")
                ]
            };
        }

        return new AgentJobLifecycleDecision
        {
            Status = AgentJobStatus.Completed,
            UpdateCompletedAt = true,
            CompletedAt = now,
            Logs = [Info("Job stopped.")]
        };
    }

    /// <summary>Creates a pause transition for currently executing jobs.</summary>
    public AgentJobLifecycleDecision Pause(AgentJobStatus status)
    {
        if (status != AgentJobStatus.Executing)
        {
            return new AgentJobLifecycleDecision
            {
                Logs = [Warning($"Pause rejected: job is {status}, not Executing.")]
            };
        }

        return new AgentJobLifecycleDecision
        {
            Status = AgentJobStatus.Paused,
            Logs = [Info("Job paused.")]
        };
    }

    /// <summary>Creates a resume transition for paused jobs.</summary>
    public AgentJobLifecycleDecision Resume(AgentJobStatus status)
    {
        if (status != AgentJobStatus.Paused)
        {
            return new AgentJobLifecycleDecision
            {
                Logs = [Warning($"Resume rejected: job is {status}, not Paused.")]
            };
        }

        return new AgentJobLifecycleDecision
        {
            Status = AgentJobStatus.Executing,
            Logs = [Info("Job resumed.")]
        };
    }

    /// <summary>Marks execution as started.</summary>
    public AgentJobLifecycleDecision BeginExecution(DateTimeOffset now) =>
        new()
        {
            Status = AgentJobStatus.Executing,
            UpdateStartedAt = true,
            StartedAt = now,
            Logs = [Info("Execution started.")]
        };

    /// <summary>Resolves a successful module execution result.</summary>
    public AgentJobLifecycleDecision CompleteExecution(
        string? resultData,
        ModuleJobCompletionBehavior completionBehavior,
        DateTimeOffset now)
    {
        if (completionBehavior == ModuleJobCompletionBehavior.RemainExecuting)
        {
            return new AgentJobLifecycleDecision
            {
                Status = AgentJobStatus.Executing,
                UpdateCompletedAt = true,
                CompletedAt = null,
                UpdateResult = true,
                Result = resultData,
                Logs =
                [
                    Info(
                        "Module reported long-running work started; job remains "
                        + "Executing until the module or host lifecycle transitions it.")
                ]
            };
        }

        return new AgentJobLifecycleDecision
        {
            Status = AgentJobStatus.Completed,
            UpdateCompletedAt = true,
            CompletedAt = now,
            UpdateResult = true,
            Result = resultData,
            Logs = [Info("Job completed successfully.")]
        };
    }

    /// <summary>Resolves a thrown execution failure into a terminal failed state.</summary>
    public AgentJobLifecycleDecision FailExecution(
        string message,
        string errorLog,
        DateTimeOffset now) =>
        new()
        {
            Status = AgentJobStatus.Failed,
            UpdateCompletedAt = true,
            CompletedAt = now,
            UpdateFailure = true,
            ErrorCode = "job_execution_failed",
            ErrorMessage = message,
            ErrorDetails = errorLog,
            Logs = [Error($"Job failed: {message}")]
        };

    /// <summary>
    /// Resolves a module callback that reports a job failure. Terminal jobs
    /// are left untouched to preserve callback idempotence.
    /// </summary>
    public AgentJobLifecycleDecision FailModuleCallback(
        AgentJobStatus status,
        string message,
        string errorLog,
        DateTimeOffset now)
    {
        if (IsTerminal(status))
            return new AgentJobLifecycleDecision();

        return FailExecution(message, errorLog, now);
    }

    /// <summary>
    /// Resolves a module callback that reports completion. A null result value
    /// preserves the existing stored result data.
    /// </summary>
    public AgentJobLifecycleDecision CompleteModuleCallback(
        AgentJobStatus status,
        string? resultData,
        string? message,
        DateTimeOffset now)
    {
        if (IsTerminal(status))
            return new AgentJobLifecycleDecision();

        return new AgentJobLifecycleDecision
        {
            Status = AgentJobStatus.Completed,
            UpdateCompletedAt = true,
            CompletedAt = now,
            UpdateResult = resultData is not null,
            Result = resultData,
            Logs =
            [
                Info(
                    string.IsNullOrWhiteSpace(message)
                        ? "Job completed by module."
                        : message)
            ]
        };
    }

    /// <summary>
    /// Resolves a module callback that reports cancellation. Terminal jobs
    /// are left untouched to preserve callback idempotence.
    /// </summary>
    public AgentJobLifecycleDecision CancelModuleCallback(
        AgentJobStatus status,
        string? message,
        DateTimeOffset now)
    {
        if (IsTerminal(status))
            return new AgentJobLifecycleDecision();

        return new AgentJobLifecycleDecision
        {
            Status = AgentJobStatus.Cancelled,
            UpdateCompletedAt = true,
            CompletedAt = now,
            Logs =
            [
                Warning(
                    string.IsNullOrWhiteSpace(message)
                        ? "Job cancelled by module."
                        : message)
            ]
        };
    }

    /// <summary>
    /// Resolves stale-session cleanup for queued or executing module jobs.
    /// Other states are left untouched.
    /// </summary>
    public AgentJobLifecycleDecision CancelStaleFromPreviousSession(
        AgentJobStatus status,
        DateTimeOffset now)
    {
        if (status is not AgentJobStatus.Queued and not AgentJobStatus.Executing)
            return new AgentJobLifecycleDecision();

        return new AgentJobLifecycleDecision
        {
            Status = AgentJobStatus.Cancelled,
            UpdateCompletedAt = true,
            CompletedAt = now,
            Logs =
            [
                Warning("Cancelled: stale from previous session.")
            ]
        };
    }

    /// <summary>Formats a job action caller consistently for lifecycle logs.</summary>
    public static string FormatCaller(ActionCaller caller) =>
        caller.UserId is not null ? $"user {caller.UserId}"
        : caller.AgentId is not null ? $"agent {caller.AgentId}"
        : "unknown";

    private static bool IsTerminal(AgentJobStatus status) =>
        status is AgentJobStatus.Completed
            or AgentJobStatus.Failed
            or AgentJobStatus.Denied
            or AgentJobStatus.Cancelled;

    private static AgentJobLifecycleLog Info(string message) =>
        new(message, JobLogLevels.Info);

    private static AgentJobLifecycleLog Warning(string message) =>
        new(message, JobLogLevels.Warning);

    private static AgentJobLifecycleLog Error(string message) =>
        new(message, JobLogLevels.Error);
}
