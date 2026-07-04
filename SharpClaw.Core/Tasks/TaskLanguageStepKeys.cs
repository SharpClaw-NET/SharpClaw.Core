namespace SharpClaw.Core.Tasks;

/// <summary>
/// Stable wire-format step keys for intrinsic task-language statements.
/// These keys represent ordinary C# script syntax, not module-contributed
/// operations.
/// </summary>
public static class TaskLanguageStepKeys
{
    /// <summary>Declares a task-local variable.</summary>
    public const string DeclareVariable = "core.declare_variable";
    /// <summary>Assigns a task-local variable.</summary>
    public const string Assign = "core.assign";
    /// <summary>Registers an event handler body with the runtime.</summary>
    public const string EventHandler = "core.event_handler";
    /// <summary>Represents an ordinary C# conditional statement.</summary>
    public const string Conditional = "core.conditional";
    /// <summary>Represents an ordinary C# loop statement.</summary>
    public const string Loop = "core.loop";
    /// <summary>Terminates the current task body.</summary>
    public const string Return = "core.return";
    /// <summary>Evaluates a side-effect-free expression.</summary>
    public const string Evaluate = "core.evaluate";
    /// <summary>Pauses execution for a bounded duration.</summary>
    public const string Delay = "core.delay";
    /// <summary>Waits until the task runtime is stopped or cancelled.</summary>
    public const string WaitUntilStopped = "core.wait_until_stopped";
    /// <summary>Appends a task log entry.</summary>
    public const string Log = "core.log";

    /// <summary>
    /// Returns true when the step is implemented by the Core task language
    /// runtime rather than a module step executor.
    /// </summary>
    public static bool IsIntrinsic(string stepKey) => stepKey is
        DeclareVariable
        or Assign
        or EventHandler
        or Conditional
        or Loop
        or Return
        or Evaluate
        or Delay
        or WaitUntilStopped
        or Log;
}
