using SharpClaw.Contracts.DTOs.AgentActions;

namespace SharpClaw.Core.Tools;

/// <summary>
/// Single source of truth for persisted tool-call notation appended to
/// assistant message content.
/// </summary>
public static class ToolNotationFormatter
{
    /// <summary>Glyph used for a regular tool execution line.</summary>
    public const string ExecutionGlyph = "\u2699";
    /// <summary>Glyph used while a job awaits approval.</summary>
    public const string AwaitingApprovalGlyph = "\u23f3";
    /// <summary>Status text for inline tool execution.</summary>
    public const string DoneStatus = "done";
    /// <summary>Fallback action name for unnamed jobs.</summary>
    public const string UnknownAction = "unknown";

    /// <summary>Formats the persisted notation for a completed job.</summary>
    public static string ForJob(AgentJobResponse job)
        => $"\n{ExecutionGlyph} [{job.ActionKey ?? UnknownAction}] \u2192 {job.Status}";

    /// <summary>Formats the persisted notation for an approval wait.</summary>
    public static string ForApproval(AgentJobResponse job)
        => $"\n{AwaitingApprovalGlyph} [{job.ActionKey ?? UnknownAction}] awaiting approval \u2192 {job.Status}";

    /// <summary>Formats the persisted notation for an inline tool.</summary>
    public static string ForInlineTool(string toolName)
        => $"\n{ExecutionGlyph} [{toolName}] \u2192 {DoneStatus}";
}
