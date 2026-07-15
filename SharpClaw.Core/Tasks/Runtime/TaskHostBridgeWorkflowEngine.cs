using SharpClaw.Core.State;
using System.Text;
using System.Text.Json;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Channels;
using SharpClaw.Contracts.DTOs.Roles;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Permissions;

namespace SharpClaw.Core.Tasks.Runtime;

/// <summary>
/// Store-neutral workflow for task-exposed host agent bridge operations.
/// Hosts own chat transport, persistence, and cache invalidation; Core owns
/// bridge sequencing, mutation rules, log text, and request construction.
/// </summary>
public sealed class TaskHostBridgeWorkflowEngine(
    TaskHostBridgeProvisioningEngine provisioning,
    TaskStructuredResponseParser structuredResponses,
    RolePermissionAdministrationEngine rolePermissions,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public TaskHostBridgeWorkflowEngine()
        : this(
            new TaskHostBridgeProvisioningEngine(),
            new TaskStructuredResponseParser(),
            new RolePermissionAdministrationEngine())
    {
    }

    public async Task<string?> ChatAsync(
        Guid instanceId,
        string taskName,
        string message,
        Guid? agentId,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);

        var channelId = await GetInstanceChannelIdAsync(instanceId, host, ct);
        var request = BuildTaskChatRequest(instanceId, taskName, message, agentId);
        var response = await host.SendChatAsync(channelId, request, null, ct);
        var content = response.AssistantMessage.Content;

        await host.AppendTaskLogAsync(
            instanceId,
            BuildChatLog("Chat", content.Length),
            ct);
        return content;
    }

    public async Task<string> ChatStreamAsync(
        Guid instanceId,
        string taskName,
        string message,
        Guid? agentId,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);

        var channelId = await GetInstanceChannelIdAsync(instanceId, host, ct);
        var request = BuildTaskChatRequest(instanceId, taskName, message, agentId);
        var builder = new StringBuilder();

        await foreach (var evt in host.SendChatStreamAsync(
                           channelId,
                           request,
                           ct))
        {
            if (evt.Type == ChatStreamEventType.TextDelta && evt.Delta is not null)
                builder.Append(evt.Delta);
        }

        await host.AppendTaskLogAsync(
            instanceId,
            BuildChatLog("ChatStream", builder.Length),
            ct);
        return builder.ToString();
    }

    public async Task<string?> ChatToThreadAsync(
        Guid instanceId,
        string taskName,
        Guid threadId,
        string message,
        Guid? agentId,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);

        var channelId = await GetInstanceChannelIdAsync(instanceId, host, ct);
        var request = BuildTaskChatRequest(instanceId, taskName, message, agentId);
        var response = await host.SendChatAsync(channelId, request, threadId, ct);
        var content = response.AssistantMessage.Content;

        await host.AppendTaskLogAsync(
            instanceId,
            BuildChatToThreadLog(threadId, content.Length),
            ct);
        return content;
    }

    public string ParseStructuredResponse(
        Guid instanceId,
        string text,
        string? typeName,
        ITaskHostBridgeWorkflowHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        IReadOnlyList<Models.TaskDataTypeDefinition>? dataTypes = null;
        if (!string.IsNullOrWhiteSpace(typeName)
            && host.LoadTaskDefinitionSourceText(instanceId) is { } sourceText)
        {
            var compileResult = TaskScriptEngine.ProcessScript(sourceText, null);
            dataTypes = compileResult.Plan?.Definition.DataTypes;
        }

        return structuredResponses.Parse(text, typeName, dataTypes);
    }

    public Task<Guid?> FindModelAsync(
        string search,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
        => host.FindIdAsync(TaskHostBridgeLookupKind.Model, search, ct);

    public Task<Guid?> FindProviderAsync(
        string search,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
        => host.FindIdAsync(TaskHostBridgeLookupKind.Provider, search, ct);

    public Task<Guid?> FindAgentAsync(
        string search,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
        => host.FindIdAsync(TaskHostBridgeLookupKind.Agent, search, ct);

    public Task<Guid?> FindRoleAsync(
        string search,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
        => host.FindIdAsync(TaskHostBridgeLookupKind.Role, search, ct);

    public Task<Guid?> FindChannelAsync(
        string search,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
        => host.FindIdAsync(TaskHostBridgeLookupKind.Channel, search, ct);

    public async Task<Guid> CreateAgentAsync(
        Guid instanceId,
        string name,
        Guid modelId,
        string? systemPrompt,
        string? customId,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);

        var existing = string.IsNullOrEmpty(customId)
            ? null
            : await host.LoadLatestAgentByCustomIdAsync(customId, ct);
        var result = provisioning.ApplyAgentProvisioning(
            existing,
            name,
            modelId,
            systemPrompt,
            customId);

        if (result.Created)
            host.TrackAgent(result.Agent);

        await host.SaveAsync(ct);
        host.Invalidate(TaskHostBridgeInvalidationTarget.Agent, result.Agent.Id);

        if (await host.LoadInstanceChannelIdAsync(instanceId, ct) is { } channelId
            && await host.LoadChannelWithAllowedAgentsAsync(channelId, ct) is { } channel
            && provisioning.AddChannelAllowedAgent(channel, result.Agent))
        {
            await host.SaveAsync(ct);
            host.Invalidate(TaskHostBridgeInvalidationTarget.Channel, channel.Id);
        }

        await host.AppendTaskLogAsync(
            instanceId,
            TaskHostBridgeProvisioningEngine.BuildCreateAgentLog(
                name,
                result.Agent.Id),
            ct);
        return result.Agent.Id;
    }

    public async Task<Guid> CreateThreadAsync(
        Guid instanceId,
        Guid? channelId,
        string? threadName,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);

        var resolvedChannelId = channelId
            ?? await GetInstanceChannelIdAsync(instanceId, host, ct);
        var thread = provisioning.CreateThread(
            resolvedChannelId,
            threadName,
            _timeProvider.GetUtcNow());

        host.TrackThread(thread);
        await host.SaveAsync(ct);
        host.Invalidate(TaskHostBridgeInvalidationTarget.Thread, thread.Id);

        await host.AppendTaskLogAsync(
            instanceId,
            TaskHostBridgeProvisioningEngine.BuildCreateThreadLog(
                thread.Name,
                thread.Id),
            ct);
        return thread.Id;
    }

    public async Task<Guid> CreateRoleAsync(
        string roleName,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (await host.LoadRoleByNameAsync(roleName, ct) is { } existing)
            return existing.Id;

        return await host.CreateRoleAsync(roleName, ct);
    }

    public async Task SetRolePermissionsAsync(
        Guid roleId,
        string requestJson,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);

        var request = DeserializeSetRolePermissionsRequest(requestJson);
        var role = await host.LoadRoleWithPermissionSetAsync(roleId, ct)
            ?? throw new InvalidOperationException(
                $"SetRolePermissions: role '{roleId}' not found.");

        var permissionSet = await host.EnsureRolePermissionSetAsync(role, ct);
        await host.LoadPermissionSetCollectionsAsync(permissionSet, ct);

        rolePermissions.ReconcilePermissionSet(permissionSet, request);

        await host.SaveAsync(ct);
        host.Invalidate(TaskHostBridgeInvalidationTarget.Permission, role.Id);
    }

    public async Task AssignRoleAsync(
        Guid agentId,
        Guid roleId,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);

        var agent = await host.LoadAgentAsync(agentId, ct)
            ?? throw new InvalidOperationException(
                $"AssignRole: agent '{agentId}' not found.");
        if (!await host.RoleExistsAsync(roleId, ct))
            throw new InvalidOperationException(
                $"AssignRole: role '{roleId}' not found.");

        agent.RoleId = roleId;
        await host.SaveAsync(ct);
        host.Invalidate(TaskHostBridgeInvalidationTarget.Agent, agentId);
    }

    public async Task<Guid> CreateChannelAsync(
        Guid instanceId,
        string title,
        Guid agentId,
        string? customId,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);

        var contextId = await host.LoadInstanceContextIdAsync(instanceId, ct);
        var existing = !string.IsNullOrEmpty(customId)
            ? await host.LoadChannelByCustomIdAsync(customId, ct)
            : await host.LoadChannelByTitleAsync(title, ct);

        Guid channelId;
        if (existing is not null)
        {
            provisioning.ApplyExistingChannelProvisioning(
                existing,
                title,
                agentId,
                customId,
                contextId);
            await host.SaveAsync(ct);
            host.Invalidate(TaskHostBridgeInvalidationTarget.Channel, existing.Id);
            channelId = existing.Id;
        }
        else
        {
            channelId = await host.CreateChannelAsync(
                new CreateChannelRequest(
                    AgentId: agentId,
                    Title: title,
                    CustomId: customId,
                    ContextId: contextId),
                ct);
        }

        await host.TryAdoptInstanceChannelAsync(instanceId, channelId, ct);

        await host.AppendTaskLogAsync(
            instanceId,
            TaskHostBridgeProvisioningEngine.BuildCreateChannelLog(
                title,
                channelId),
            ct);
        return channelId;
    }

    public async Task AddAllowedAgentAsync(
        Guid instanceId,
        Guid agentId,
        Guid? channelId,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);

        var agent = await host.LoadAgentAsync(agentId, ct)
            ?? throw new InvalidOperationException(
                $"AddAllowedAgent: agent '{agentId}' not found.");
        var targetChannelId = channelId
            ?? await GetInstanceChannelIdAsync(instanceId, host, ct);
        var channel = await host.LoadChannelWithAllowedAgentsAsync(
                targetChannelId,
                ct)
            ?? throw new InvalidOperationException(
                $"AddAllowedAgent: channel '{targetChannelId}' not found.");

        if (provisioning.AddChannelAllowedAgent(channel, agent))
        {
            await host.SaveAsync(ct);
            host.Invalidate(TaskHostBridgeInvalidationTarget.Channel, channel.Id);
        }

        await host.AppendTaskLogAsync(
            instanceId,
            TaskHostBridgeProvisioningEngine.BuildAddAllowedAgentLog(
                agentId,
                targetChannelId),
            ct);
    }

    private async Task<Guid> GetInstanceChannelIdAsync(
        Guid instanceId,
        ITaskHostBridgeWorkflowHost host,
        CancellationToken ct)
    {
        return provisioning.RequireInstanceChannel(
            instanceId,
            await host.LoadInstanceChannelIdAsync(instanceId, ct));
    }

    private static ChatRequest BuildTaskChatRequest(
        Guid instanceId,
        string taskName,
        string message,
        Guid? agentId)
    {
        return new ChatRequest(
            message,
            agentId,
            WellKnownClientKeys.Api,
            TaskContext: new TaskChatContext(instanceId, taskName));
    }

    private static SetRolePermissionsRequest DeserializeSetRolePermissionsRequest(
        string requestJson)
    {
        return !string.IsNullOrWhiteSpace(requestJson)
            ? JsonSerializer.Deserialize<SetRolePermissionsRequest>(
                  requestJson,
                  new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
              ?? new SetRolePermissionsRequest()
            : new SetRolePermissionsRequest();
    }

    private static string BuildChatLog(string operation, int chars)
        => $"{operation} \u2192 {chars} chars";

    private static string BuildChatToThreadLog(Guid threadId, int chars)
        => $"ChatToThread {threadId} \u2192 {chars} chars";
}

public interface ITaskHostBridgeWorkflowHost
{
    Task<Guid?> LoadInstanceChannelIdAsync(Guid instanceId, CancellationToken ct);

    Task<Guid?> LoadInstanceContextIdAsync(Guid instanceId, CancellationToken ct);

    string? LoadTaskDefinitionSourceText(Guid instanceId);

    Task<ChatResponse> SendChatAsync(
        Guid channelId,
        ChatRequest request,
        Guid? threadId,
        CancellationToken ct);

    IAsyncEnumerable<ChatStreamEvent> SendChatStreamAsync(
        Guid channelId,
        ChatRequest request,
        CancellationToken ct);

    Task AppendTaskLogAsync(
        Guid instanceId,
        string message,
        CancellationToken ct);

    Task<Guid?> FindIdAsync(
        TaskHostBridgeLookupKind kind,
        string search,
        CancellationToken ct);

    Task<AgentState?> LoadLatestAgentByCustomIdAsync(
        string customId,
        CancellationToken ct);

    void TrackAgent(AgentState agent);

    Task<ChannelState?> LoadChannelWithAllowedAgentsAsync(
        Guid channelId,
        CancellationToken ct);

    void TrackThread(ChatThreadState thread);

    Task<RoleState?> LoadRoleByNameAsync(string roleName, CancellationToken ct);

    Task<Guid> CreateRoleAsync(string roleName, CancellationToken ct);

    Task<RoleState?> LoadRoleWithPermissionSetAsync(Guid roleId, CancellationToken ct);

    Task<PermissionSetState> EnsureRolePermissionSetAsync(
        RoleState role,
        CancellationToken ct);

    Task LoadPermissionSetCollectionsAsync(
        PermissionSetState permissionSet,
        CancellationToken ct);

    Task<AgentState?> LoadAgentAsync(Guid agentId, CancellationToken ct);

    Task<bool> RoleExistsAsync(Guid roleId, CancellationToken ct);

    Task<ChannelState?> LoadChannelByCustomIdAsync(
        string customId,
        CancellationToken ct);

    Task<ChannelState?> LoadChannelByTitleAsync(
        string title,
        CancellationToken ct);

    Task<Guid> CreateChannelAsync(
        CreateChannelRequest request,
        CancellationToken ct);

    Task<bool> TryAdoptInstanceChannelAsync(
        Guid instanceId,
        Guid channelId,
        CancellationToken ct);

    Task SaveAsync(CancellationToken ct);

    void Invalidate(TaskHostBridgeInvalidationTarget target, Guid? entityId = null);
}

public enum TaskHostBridgeLookupKind
{
    Model,
    Provider,
    Agent,
    Role,
    Channel
}

public enum TaskHostBridgeInvalidationTarget
{
    Agent,
    Channel,
    Thread,
    Permission
}
