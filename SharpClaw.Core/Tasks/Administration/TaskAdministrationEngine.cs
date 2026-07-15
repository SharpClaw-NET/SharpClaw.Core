using SharpClaw.Core.State;
using System.Text.Json;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Tasks.Models;

namespace SharpClaw.Core.Tasks.Administration;

/// <summary>
/// Store-neutral task definition and instance administration rules.
/// </summary>
public sealed class TaskAdministrationEngine
{
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a task administration engine with an optional host-supplied clock.
    /// </summary>
    public TaskAdministrationEngine(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Parses and validates task source without creating a persisted entity.
    /// </summary>
    public TaskValidationResponse ValidateDefinition(string sourceText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);

        var parseResult = TaskScriptEngine.Parse(sourceText);
        if (!parseResult.Success || parseResult.Definition is null)
        {
            return new TaskValidationResponse(
                false,
                parseResult.Diagnostics.Select(ToDiagnosticResponse).ToArray());
        }

        var validation = TaskScriptEngine.Validate(parseResult.Definition);
        var diagnostics = parseResult.Diagnostics
            .Concat(validation.Diagnostics)
            .Select(ToDiagnosticResponse)
            .ToArray();

        return new TaskValidationResponse(validation.IsValid, diagnostics);
    }

    /// <summary>
    /// Creates a new task definition entity from validated source.
    /// </summary>
    public TaskDefinitionPreparation PrepareDefinition(
        CreateTaskDefinitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var definition = ParseAndValidateDefinition(request.SourceText);

        var entity = new TaskDefinitionState
        {
            Name = definition.Name,
            Description = definition.Description,
            SourceText = request.SourceText,
            OutputTypeName = definition.OutputType?.Name,
            ParametersJson = SerializeParameters(definition.Parameters),
            RequirementsJson = SerializeRequirements(definition.Requirements),
            TriggersJson = SerializeTriggers(definition.TriggerDefinitions),
        };

        return new TaskDefinitionPreparation(entity, definition);
    }

    /// <summary>
    /// Throws when a task definition name is already present in the store.
    /// </summary>
    public void EnsureDefinitionNameAvailable(string name, bool nameAlreadyExists)
    {
        if (nameAlreadyExists)
        {
            throw new InvalidOperationException(
                $"Task definition '{name}' already exists.");
        }
    }

    /// <summary>
    /// Applies source and active-state updates to an existing task definition.
    /// </summary>
    public TaskDefinitionUpdatePreparation ApplyDefinitionUpdate(
        TaskDefinitionState entity,
        UpdateTaskDefinitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<TaskParameterDefinition>? parameters = null;
        IReadOnlyList<TaskRequirementDefinition>? requirements = null;
        IReadOnlyList<TaskTriggerDefinition>? triggers = null;

        if (request.SourceText is not null)
        {
            var definition = ParseAndValidateDefinition(request.SourceText);

            entity.Name = definition.Name;
            entity.Description = definition.Description;
            entity.SourceText = request.SourceText;
            entity.OutputTypeName = definition.OutputType?.Name;
            entity.ParametersJson = SerializeParameters(definition.Parameters);
            entity.RequirementsJson = SerializeRequirements(definition.Requirements);
            entity.TriggersJson = SerializeTriggers(definition.TriggerDefinitions);

            parameters = definition.Parameters;
            requirements = definition.Requirements;
            triggers = definition.TriggerDefinitions;
        }

        if (request.IsActive is not null)
            entity.IsActive = request.IsActive.Value;

        return new TaskDefinitionUpdatePreparation(
            parameters ?? DeserializeParameters(entity.ParametersJson),
            requirements ?? DeserializeRequirements(entity.RequirementsJson),
            triggers ?? DeserializeTriggers(entity.TriggersJson),
            SourceWasUpdated: request.SourceText is not null);
    }

    /// <summary>
    /// Creates a queued task instance row from a validated start request.
    /// </summary>
    public TaskInstanceState CreateInstance(
        TaskDefinitionState definition,
        StartTaskInstanceRequest request,
        Guid? callerUserId,
        Guid? callerAgentId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(request);

        if (!definition.IsActive)
        {
            throw new InvalidOperationException(
                $"Task definition '{definition.Name}' is not active.");
        }

        return new TaskInstanceState
        {
            Id = Guid.NewGuid(),
            CreatedAt = _timeProvider.GetUtcNow(),
            TaskDefinitionId = definition.Id,
            Status = TaskInstanceStatus.Queued,
            ParameterValuesJson = SerializeParameterValues(request.ParameterValues),
            ChannelId = request.ChannelId,
            ContextId = request.ContextId,
            CallerUserId = callerUserId,
            CallerAgentId = callerAgentId,
        };
    }

    /// <summary>
    /// Converts supplied string parameters into the preflight value map.
    /// </summary>
    public IReadOnlyDictionary<string, object?> ToPreflightParameterMap(
        IReadOnlyDictionary<string, string>? parameterValues)
    {
        return parameterValues is not null
            ? parameterValues.ToDictionary(
                kv => kv.Key,
                kv => (object?)kv.Value,
                StringComparer.Ordinal)
            : new Dictionary<string, object?>();
    }

    /// <summary>
    /// Moves a running instance to paused.
    /// </summary>
    public bool TryPauseInstance(TaskInstanceState instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.Status != TaskInstanceStatus.Running)
            return false;

        instance.Status = TaskInstanceStatus.Paused;
        return true;
    }

    /// <summary>
    /// Moves a paused instance back to running.
    /// </summary>
    public bool TryResumeInstance(TaskInstanceState instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.Status != TaskInstanceStatus.Paused)
            return false;

        instance.Status = TaskInstanceStatus.Running;
        return true;
    }

    /// <summary>
    /// Moves a queued instance to running and clears terminal fields.
    /// </summary>
    public bool TryMarkInstanceRunning(TaskInstanceState instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.Status != TaskInstanceStatus.Queued)
            return false;

        instance.Status = TaskInstanceStatus.Running;
        instance.StartedAt = _timeProvider.GetUtcNow();
        instance.CompletedAt = null;
        instance.ErrorMessage = null;
        return true;
    }

    /// <summary>
    /// Cancels a running or paused instance after a graceful stop request.
    /// </summary>
    public bool TryStopInstance(TaskInstanceState instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.Status is not (TaskInstanceStatus.Running or TaskInstanceStatus.Paused))
            return false;

        instance.Status = TaskInstanceStatus.Cancelled;
        instance.CompletedAt = _timeProvider.GetUtcNow();
        return true;
    }

    /// <summary>
    /// Cancels a queued, running, or paused instance.
    /// </summary>
    public bool TryCancelInstance(TaskInstanceState instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.Status is not (TaskInstanceStatus.Queued or TaskInstanceStatus.Running or TaskInstanceStatus.Paused))
            return false;

        instance.Status = TaskInstanceStatus.Cancelled;
        instance.CompletedAt = _timeProvider.GetUtcNow();
        return true;
    }

    /// <summary>
    /// Marks a task instance failed because its script could not compile.
    /// </summary>
    public void ApplyCompilationFailure(TaskInstanceState instance, string errors)
    {
        ArgumentNullException.ThrowIfNull(instance);

        instance.Status = TaskInstanceStatus.Failed;
        instance.ErrorMessage = $"Compilation failed: {errors}";
        instance.CompletedAt = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Marks a task instance completed or cancelled.
    /// </summary>
    public void ApplyTerminalStatus(TaskInstanceState instance, TaskInstanceStatus status)
    {
        ArgumentNullException.ThrowIfNull(instance);

        instance.Status = status;
        instance.CompletedAt = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Marks a task instance failed with the supplied error message.
    /// </summary>
    public void ApplyFailure(TaskInstanceState instance, string error)
    {
        ArgumentNullException.ThrowIfNull(instance);

        instance.Status = TaskInstanceStatus.Failed;
        instance.ErrorMessage = error;
        instance.CompletedAt = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Marks an instance from a previous process lifetime as failed because
    /// SharpClaw task side effects cannot be safely replayed.
    /// </summary>
    public TaskRestartRecoveryPlan ApplyRestartRecovery(TaskInstanceState instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var previous = instance.Status;
        instance.Status = TaskInstanceStatus.Failed;
        instance.ErrorMessage =
            $"Instance was {previous} when the application restarted. " +
            "Manual restart required.";
        instance.CompletedAt ??= _timeProvider.GetUtcNow();

        return new TaskRestartRecoveryPlan(
            previous,
            $"Recovery: instance was {previous} at startup \u2014 marked Failed.");
    }

    /// <summary>Creates a storage-neutral task output event.</summary>
    public TaskOutputEmission CreateOutput(
        Guid instanceId,
        long sequence,
        string? outputJson)
    {
        return new TaskOutputEmission(
            Guid.NewGuid(),
            instanceId,
            sequence,
            outputJson,
            _timeProvider.GetUtcNow());
    }

    /// <summary>Creates a storage-neutral task diagnostic event.</summary>
    public TaskExecutionLog CreateLog(
        Guid instanceId,
        string message,
        string level = JobLogLevels.Info)
    {
        return new TaskExecutionLog(
            Guid.NewGuid(),
            instanceId,
            message,
            level,
            _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Projects a persisted task definition into its public response.
    /// </summary>
    public TaskDefinitionResponse ToDefinitionResponse(
        TaskDefinitionState entity,
        IReadOnlyList<TaskParameterDefinition> parameters,
        IReadOnlyList<TaskRequirementDefinition> requirements,
        IReadOnlyList<TaskTriggerDefinition> triggers,
        Func<TaskTriggerDefinition, string?>? triggerValueResolver = null,
        Func<TaskTriggerDefinition, string?>? triggerFilterResolver = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(triggers);

        return new TaskDefinitionResponse(
            entity.Id,
            entity.Name,
            entity.Description,
            entity.OutputTypeName,
            entity.IsActive,
            parameters.Select(ToParameterResponse).ToArray(),
            requirements.Select(ToRequirementResponse).ToArray(),
            triggers.Select(t => new TaskTriggerResponse(
                t.TriggerKey ?? string.Empty,
                triggerValueResolver?.Invoke(t),
                triggerFilterResolver?.Invoke(t),
                IsEnabled: true)).ToArray(),
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.CustomId);
    }

    /// <summary>
    /// Projects a task instance into the list-summary response.
    /// </summary>
    public TaskInstanceSummaryResponse ToSummaryResponse(
        TaskInstanceState instance,
        string taskName)
    {
        ArgumentNullException.ThrowIfNull(instance);

        return new TaskInstanceSummaryResponse(
            instance.Id,
            instance.TaskDefinitionId,
            taskName,
            instance.Status,
            instance.CreatedAt,
            instance.StartedAt,
            instance.CompletedAt);
    }

    /// <summary>
    /// Formats a task diagnostic for exception messages.
    /// </summary>
    public string FormatDiagnostic(TaskDiagnostic diagnostic)
        => diagnostic.Line > 0 ? $"[Line {diagnostic.Line}] {diagnostic.Message}" : diagnostic.Message;

    /// <summary>
    /// Projects a task diagnostic into its public response.
    /// </summary>
    public TaskDiagnosticResponse ToDiagnosticResponse(TaskDiagnostic diagnostic)
        => new(
            diagnostic.Severity.ToString(),
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Line,
            diagnostic.Column);

    /// <summary>
    /// Serializes task parameter metadata into the canonical persisted JSON shape.
    /// </summary>
    public string SerializeParameters(IReadOnlyList<TaskParameterDefinition> parameters)
    {
        var dtos = parameters
            .Select(p => new ParameterDto(p.Name, p.TypeName, p.Description, p.DefaultValue, p.IsRequired))
            .ToArray();
        return JsonSerializer.Serialize(dtos);
    }

    /// <summary>
    /// Deserializes task parameter metadata from the canonical persisted JSON shape.
    /// </summary>
    public IReadOnlyList<TaskParameterDefinition> DeserializeParameters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var dtos = JsonSerializer.Deserialize<List<ParameterDto>>(json) ?? [];
        return dtos
            .Select(d => new TaskParameterDefinition(
                Name: d.Name ?? "",
                TypeName: d.TypeName ?? "string",
                Description: d.Description,
                DefaultValue: d.DefaultValue,
                IsRequired: d.IsRequired))
            .ToArray();
    }

    /// <summary>
    /// Serializes task requirement metadata into the canonical persisted JSON shape.
    /// </summary>
    public string SerializeRequirements(IReadOnlyList<TaskRequirementDefinition> requirements)
    {
        var dtos = requirements
            .Select(r => new RequirementDto(
                r.Kind.ToString(),
                r.Severity.ToString(),
                r.Value,
                r.CapabilityValue,
                r.ParameterName,
                r.Line))
            .ToArray();
        return JsonSerializer.Serialize(dtos);
    }

    /// <summary>
    /// Deserializes task requirement metadata from the canonical persisted JSON shape.
    /// </summary>
    public IReadOnlyList<TaskRequirementDefinition> DeserializeRequirements(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var dtos = JsonSerializer.Deserialize<List<RequirementDto>>(json) ?? [];
        return dtos
            .Select(d =>
            {
                Enum.TryParse<TaskRequirementKind>(d.Kind ?? string.Empty, out var kind);
                Enum.TryParse<TaskDiagnosticSeverity>(
                    d.Severity ?? nameof(TaskDiagnosticSeverity.Error),
                    out var severity);

                return new TaskRequirementDefinition
                {
                    Kind = kind,
                    Severity = severity,
                    Value = d.Value,
                    CapabilityValue = d.CapabilityValue,
                    ParameterName = d.ParameterName,
                    Line = d.Line,
                };
            })
            .ToArray();
    }

    /// <summary>
    /// Serializes task trigger metadata into the canonical persisted JSON shape.
    /// </summary>
    public string SerializeTriggers(IReadOnlyList<TaskTriggerDefinition> triggers)
        => JsonSerializer.Serialize(triggers);

    /// <summary>
    /// Deserializes task trigger metadata from the canonical persisted JSON shape.
    /// </summary>
    public IReadOnlyList<TaskTriggerDefinition> DeserializeTriggers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<TaskTriggerDefinition>>(json) ?? [];
    }

    /// <summary>
    /// Serializes task instance parameter values for persistence.
    /// </summary>
    public string? SerializeParameterValues(IReadOnlyDictionary<string, string>? parameterValues)
        => parameterValues is not null ? JsonSerializer.Serialize(parameterValues) : null;

    private TaskScriptDefinition ParseAndValidateDefinition(string sourceText)
    {
        var parseResult = TaskScriptEngine.Parse(sourceText);
        if (!parseResult.Success || parseResult.Definition is null)
        {
            var errors = string.Join("; ", parseResult.Diagnostics.Select(FormatDiagnostic));
            throw new InvalidOperationException($"Task script parse failed: {errors}");
        }

        var validation = TaskScriptEngine.Validate(parseResult.Definition);
        if (!validation.IsValid)
        {
            var errors = string.Join("; ", validation.Diagnostics.Select(FormatDiagnostic));
            throw new InvalidOperationException($"Task script validation failed: {errors}");
        }

        return parseResult.Definition;
    }

    private static TaskParameterResponse ToParameterResponse(TaskParameterDefinition parameter)
        => new(
            parameter.Name,
            parameter.TypeName,
            parameter.Description,
            parameter.DefaultValue,
            parameter.IsRequired);

    private static TaskRequirementResponse ToRequirementResponse(TaskRequirementDefinition requirement)
        => new(
            requirement.Kind.ToString(),
            requirement.Severity.ToString(),
            requirement.Value,
            requirement.CapabilityValue,
            requirement.ParameterName);

    private sealed record ParameterDto(
        string Name,
        string TypeName,
        string? Description,
        string? DefaultValue,
        bool IsRequired);

    private sealed record RequirementDto(
        string? Kind,
        string? Severity,
        string? Value,
        string? CapabilityValue,
        string? ParameterName,
        int Line);
}

/// <summary>
/// Parsed metadata produced while preparing a new task definition.
/// </summary>
public sealed record TaskDefinitionPreparation(
    TaskDefinitionState Entity,
    TaskScriptDefinition Definition);

/// <summary>
/// Parsed metadata produced while updating a task definition.
/// </summary>
public sealed record TaskDefinitionUpdatePreparation(
    IReadOnlyList<TaskParameterDefinition> Parameters,
    IReadOnlyList<TaskRequirementDefinition> Requirements,
    IReadOnlyList<TaskTriggerDefinition> Triggers,
    bool SourceWasUpdated);

/// <summary>
/// Result of applying the SharpClaw restart recovery policy to a task instance.
/// </summary>
public sealed record TaskRestartRecoveryPlan(
    TaskInstanceStatus PreviousStatus,
    string LogMessage);
