using SharpClaw.Core.State;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Tasks.Models;
using SharpClaw.Core.Tasks.Preflight;

namespace SharpClaw.Core.Tasks.Administration;

/// <summary>
/// Store-neutral task authoring and instance administration workflow.
/// Hosts own persistence, trigger side effects, and runtime fact gathering;
/// Core owns sequencing, validation, lifecycle transitions, and response
/// mapping.
/// </summary>
public sealed class TaskAdministrationWorkflowEngine(
    TaskAdministrationEngine tasks)
{
    public TaskAdministrationWorkflowEngine()
        : this(new TaskAdministrationEngine())
    {
    }

    public TaskValidationResponse ValidateDefinition(string sourceText)
        => tasks.ValidateDefinition(sourceText);

    public async Task<TaskDefinitionResponse> CreateDefinitionAsync(
        CreateTaskDefinitionRequest request,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var prepared = tasks.PrepareDefinition(request);
        var nameExists = await host.DefinitionNameExistsAsync(
            prepared.Entity.Name,
            ct);
        tasks.EnsureDefinitionNameAvailable(prepared.Entity.Name, nameExists);

        var entity = prepared.Entity;
        host.TrackDefinition(entity);
        await host.SaveAsync(ct);

        var bindingsChanged = await host.SyncTriggersAsync(
            entity,
            prepared.Definition.TriggerDefinitions,
            ct);
        if (bindingsChanged)
        {
            await host.SaveAsync(ct);
            await host.NotifyTriggerBindingsChangedAsync(ct);
        }

        return ToDefinitionResponse(
            entity,
            prepared.Definition.Parameters,
            prepared.Definition.Requirements,
            prepared.Definition.TriggerDefinitions,
            host);
    }

    public async Task<TaskDefinitionResponse?> GetDefinitionAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var entity = await host.LoadDefinitionAsync(id, ct);
        if (entity is null)
            return null;

        return ToDefinitionResponse(
            entity,
            tasks.DeserializeParameters(entity.ParametersJson),
            tasks.DeserializeRequirements(entity.RequirementsJson),
            tasks.DeserializeTriggers(entity.TriggersJson),
            host);
    }

    public async Task<IReadOnlyList<TaskRequirementDefinition>?> GetRequirementsAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var entity = await host.LoadDefinitionAsync(id, ct);
        return entity is null
            ? null
            : tasks.DeserializeRequirements(entity.RequirementsJson);
    }

    public async Task<IReadOnlyList<TaskTriggerDefinition>?> GetTriggersAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var entity = await host.LoadDefinitionAsync(id, ct);
        return entity is null
            ? null
            : tasks.DeserializeTriggers(entity.TriggersJson);
    }

    public async Task<IReadOnlyList<TaskDefinitionResponse>> ListDefinitionsAsync(
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var definitions = await host.ListDefinitionsAsync(ct);
        return definitions
            .OrderByDescending(definition => definition.UpdatedAt)
            .Select(definition => ToDefinitionResponse(
                definition,
                tasks.DeserializeParameters(definition.ParametersJson),
                tasks.DeserializeRequirements(definition.RequirementsJson),
                tasks.DeserializeTriggers(definition.TriggersJson),
                host))
            .ToList();
    }

    public async Task<TaskDefinitionResponse?> UpdateDefinitionAsync(
        Guid id,
        UpdateTaskDefinitionRequest request,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var entity = await host.LoadDefinitionAsync(id, ct);
        if (entity is null)
            return null;

        var updated = tasks.ApplyDefinitionUpdate(entity, request);
        await host.SaveAsync(ct);

        if (updated.SourceWasUpdated)
        {
            var bindingsChanged = await host.SyncTriggersAsync(
                entity,
                updated.Triggers,
                ct);
            if (bindingsChanged)
            {
                await host.SaveAsync(ct);
                await host.NotifyTriggerBindingsChangedAsync(ct);
            }
        }

        return ToDefinitionResponse(
            entity,
            updated.Parameters,
            updated.Requirements,
            updated.Triggers,
            host);
    }

    public async Task<bool> DeleteDefinitionAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var entity = await host.LoadDefinitionAsync(id, ct);
        if (entity is null)
            return false;

        var bindingsChanged = await host.RemoveTriggersAsync(id, ct);
        if (bindingsChanged)
        {
            await host.SaveAsync(ct);
            await host.NotifyTriggerBindingsChangedAsync(ct);
        }

        host.RemoveDefinition(entity);
        await host.SaveAsync(ct);
        return true;
    }

    public async Task<int> SetTriggersEnabledAsync(
        Guid taskDefinitionId,
        bool enabled,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var bindings = await host.LoadTriggerBindingsAsync(
            taskDefinitionId,
            ct);
        foreach (var binding in bindings)
            binding.IsEnabled = enabled;

        await host.SaveAsync(ct);
        return bindings.Count;
    }

    public async Task<TaskInstanceState> CreateInstanceAsync(
        StartTaskInstanceRequest request,
        Guid? callerUserId,
        Guid? callerAgentId,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var definition = await host.LoadDefinitionAsync(
                request.TaskDefinitionId,
                ct)
            ?? throw new InvalidOperationException(
                $"Task definition {request.TaskDefinitionId} not found.");

        var requirements = tasks.DeserializeRequirements(
            definition.RequirementsJson);
        if (requirements.Count > 0)
        {
            var preflightResult = await host.CheckRuntimePreflightAsync(
                requirements,
                tasks.ToPreflightParameterMap(request.ParameterValues),
                callerAgentId,
                ct);
            if (preflightResult.IsBlocked)
                throw new TaskPreflightBlockedException(preflightResult);
        }

        var instance = tasks.CreateInstance(
            definition,
            request,
            callerUserId,
            callerAgentId);
        host.TrackInstance(instance);
        await host.SaveAsync(ct);

        return instance;
    }

    public async Task<bool> PauseInstanceAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        return await MutateInstanceAsync(id, tasks.TryPauseInstance, host, ct);
    }

    public async Task<bool> ResumeInstanceAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        return await MutateInstanceAsync(id, tasks.TryResumeInstance, host, ct);
    }

    public async Task<bool> TryMarkInstanceRunningAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        return await MutateInstanceAsync(
            id,
            tasks.TryMarkInstanceRunning,
            host,
            ct);
    }

    public async Task<bool> StopInstanceAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        return await MutateInstanceAsync(id, tasks.TryStopInstance, host, ct);
    }

    public async Task<bool> CancelInstanceAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        return await MutateInstanceAsync(id, tasks.TryCancelInstance, host, ct);
    }

    public async Task AppendLogAsync(
        Guid instanceId,
        string message,
        string level,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        await host.AppendLogAsync(
            tasks.CreateLog(instanceId, message, level),
            ct);
    }

    public Task<bool> ApplyCompilationFailureAsync(
        Guid id,
        string errors,
        ITaskAdministrationHost host,
        CancellationToken ct = default) =>
        MutateInstanceAsync(
            id,
            instance =>
            {
                tasks.ApplyCompilationFailure(instance, errors);
                return true;
            },
            host,
            ct);

    public Task<bool> ApplyTerminalStatusAsync(
        Guid id,
        SharpClaw.Contracts.Enums.TaskInstanceStatus status,
        ITaskAdministrationHost host,
        CancellationToken ct = default) =>
        MutateInstanceAsync(
            id,
            instance =>
            {
                tasks.ApplyTerminalStatus(instance, status);
                return true;
            },
            host,
            ct);

    public Task<bool> ApplyFailureAsync(
        Guid id,
        string error,
        ITaskAdministrationHost host,
        CancellationToken ct = default) =>
        MutateInstanceAsync(
            id,
            instance =>
            {
                tasks.ApplyFailure(instance, error);
                return true;
            },
            host,
            ct);

    public async Task<TaskRestartRecoveryPlan?> ApplyRestartRecoveryAsync(
        Guid id,
        ITaskAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        var instance = await host.LoadInstanceAsync(id, ct);
        if (instance is null)
            return null;

        var plan = tasks.ApplyRestartRecovery(instance);
        await host.PersistInstanceAsync(instance, ct);
        return plan;
    }

    private async Task<bool> MutateInstanceAsync(
        Guid id,
        Func<TaskInstanceState, bool> mutate,
        ITaskAdministrationHost host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ArgumentNullException.ThrowIfNull(host);

        var instance = await host.LoadInstanceAsync(id, ct);
        if (instance is null || !mutate(instance))
            return false;

        await host.PersistInstanceAsync(instance, ct);
        return true;
    }

    private TaskDefinitionResponse ToDefinitionResponse(
        TaskDefinitionState entity,
        IReadOnlyList<TaskParameterDefinition> parameters,
        IReadOnlyList<TaskRequirementDefinition> requirements,
        IReadOnlyList<TaskTriggerDefinition> triggers,
        ITaskAdministrationHost host)
    {
        return tasks.ToDefinitionResponse(
            entity,
            parameters,
            requirements,
            triggers,
            host.ResolveTriggerValue,
            host.ResolveTriggerFilter);
    }
}

public interface ITaskAdministrationHost
{
    Task<bool> DefinitionNameExistsAsync(
        string name,
        CancellationToken ct);

    Task<TaskDefinitionState?> LoadDefinitionAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<TaskDefinitionState>> ListDefinitionsAsync(
        CancellationToken ct);

    void TrackDefinition(TaskDefinitionState definition);

    void RemoveDefinition(TaskDefinitionState definition);

    Task<IReadOnlyList<TaskTriggerBindingState>> LoadTriggerBindingsAsync(
        Guid taskDefinitionId,
        CancellationToken ct);

    Task<bool> SyncTriggersAsync(
        TaskDefinitionState definition,
        IReadOnlyList<TaskTriggerDefinition> triggers,
        CancellationToken ct);

    Task<bool> RemoveTriggersAsync(Guid definitionId, CancellationToken ct);

    Task NotifyTriggerBindingsChangedAsync(CancellationToken ct);

    string? ResolveTriggerValue(TaskTriggerDefinition trigger);

    string? ResolveTriggerFilter(TaskTriggerDefinition trigger);

    Task<TaskPreflightResult> CheckRuntimePreflightAsync(
        IReadOnlyList<TaskRequirementDefinition> requirements,
        IReadOnlyDictionary<string, object?> parameterValues,
        Guid? callerAgentId,
        CancellationToken ct);

    void TrackInstance(TaskInstanceState instance);

    Task<TaskInstanceState?> LoadInstanceAsync(Guid id, CancellationToken ct);

    Task PersistInstanceAsync(
        TaskInstanceState instance,
        CancellationToken ct);

    Task AppendLogAsync(TaskExecutionLog log, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);
}
