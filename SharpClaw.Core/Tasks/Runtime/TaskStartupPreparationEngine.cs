using System.Text.Json;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Tasks.Administration;
using SharpClaw.Core.Tasks.Compilation;

namespace SharpClaw.Core.Tasks.Runtime;

/// <summary>
/// Store-neutral startup preparation for queued task instances.
/// Hosts load and save entities; Core validates startup state, compiles the
/// script, and returns a startup decision. Hosts apply and save any failure
/// state.
/// </summary>
public sealed class TaskStartupPreparationEngine
{
    public TaskStartupPreparation Prepare(
        TaskInstanceState instance,
        string sourceText)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);

        if (instance.Status != TaskInstanceStatus.Queued)
        {
            throw new InvalidOperationException(
                $"Task instance {instance.Id} is {instance.Status}, expected Queued.");
        }

        Dictionary<string, object?>? parameterValues = null;
        if (instance.ParameterValuesJson is not null)
        {
            parameterValues = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                instance.ParameterValuesJson);
        }

        var compilation = TaskScriptEngine.ProcessScript(
            sourceText,
            parameterValues);

        if (compilation.Plan is null)
        {
            var errors = string.Join(
                "; ",
                compilation.Diagnostics.Select(d => d.Message));
            return TaskStartupPreparation.CompilationFailed(
                errors,
                compilation.Diagnostics.Count);
        }

        return TaskStartupPreparation.StartExecution(
            compilation.Plan,
            compilation.Diagnostics.Count);
    }
}

public sealed record TaskStartupPreparation(
    TaskStartupPreparationKind Kind,
    CompiledTaskPlan? Plan,
    string? CompilationErrors,
    int DiagnosticCount)
{
    public static TaskStartupPreparation StartExecution(
        CompiledTaskPlan plan,
        int diagnosticCount) =>
        new(
            TaskStartupPreparationKind.StartExecution,
            plan,
            CompilationErrors: null,
            diagnosticCount);

    public static TaskStartupPreparation CompilationFailed(
        string errors,
        int diagnosticCount) =>
        new(
            TaskStartupPreparationKind.CompilationFailed,
            Plan: null,
            CompilationErrors: errors,
            diagnosticCount);
}

public enum TaskStartupPreparationKind
{
    StartExecution,
    CompilationFailed
}
