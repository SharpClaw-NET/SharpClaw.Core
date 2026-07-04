using System.Text.Json;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Modules;

namespace SharpClaw.Core.Modules.Foreign;

public sealed record ForeignModuleConfigGetRequest
{
    public string Key { get; init; } = string.Empty;
}

public sealed record ForeignModuleConfigSetRequest
{
    public string Key { get; init; } = string.Empty;
    public string? Value { get; init; }
}

public sealed record ForeignModuleConfigGetResponse(string? Value);

public sealed record ForeignModuleConfigAllResponse(
    IReadOnlyDictionary<string, string> Values);

public sealed record ForeignModuleLogRequest
{
    public string Message { get; init; } = string.Empty;
    public string Level { get; init; } = "Info";
}

public sealed record ForeignModuleJobLogRequest
{
    public Guid JobId { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Level { get; init; } = "Info";
}

public sealed record ForeignModuleJobCompleteRequest
{
    public Guid JobId { get; init; }
    public string? ResultData { get; init; }
    public string? Message { get; init; }
}

public sealed record ForeignModuleJobFailRequest
{
    public Guid JobId { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Details { get; init; }
}

public sealed record ForeignModuleJobCancelRequest
{
    public Guid JobId { get; init; }
    public string? Message { get; init; }
}

public sealed record ForeignModuleJobActionPrefixRequest
{
    public string ActionKeyPrefix { get; init; } = string.Empty;
    public Guid? ResourceId { get; init; }
}

public sealed record ForeignModuleJobExistsWithActionPrefixRequest
{
    public Guid JobId { get; init; }
    public string ActionKeyPrefix { get; init; } = string.Empty;
}

public sealed record ForeignModuleJobGetResponse(AgentJobResponse? Job);

public sealed record ForeignModuleJobListResponse(
    IReadOnlyList<AgentJobResponse> Jobs);

public sealed record ForeignModuleJobSummaryListResponse(
    IReadOnlyList<AgentJobSummaryResponse> Jobs);

public sealed record ForeignModuleCapabilityAck(
    bool Accepted = true,
    string? Message = null);

public sealed record ForeignModuleProtocolContractsListResponse(
    IReadOnlyList<ForeignModuleProtocolContractExport> Contracts);

public sealed record ForeignModuleProtocolContractInvokeRequest
{
    public string ContractName { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public JsonElement Parameters { get; init; }
}

public sealed record ForeignModuleProtocolContractInvokeResponse(
    JsonElement Result);

public sealed record ForeignModuleTaskSourceRequest
{
    public string SourceText { get; init; } = string.Empty;
}

public sealed record ForeignModuleTaskIdRequest
{
    public Guid Id { get; init; }
}

public sealed record ForeignModuleTaskUpdateRequest
{
    public Guid Id { get; init; }
    public string? SourceText { get; init; }
    public bool? IsActive { get; init; }
}

public sealed record ForeignModuleTaskGetResponse(
    TaskDefinitionResponse? Definition);

public sealed record ForeignModuleTaskListResponse(
    IReadOnlyList<TaskDefinitionResponse> Definitions);

public sealed record ForeignModuleTaskDeleteResponse(bool Deleted);

public sealed record ForeignModuleTaskLaunchRequest
{
    public Guid TaskDefinitionId { get; init; }
    public IReadOnlyDictionary<string, string>? ParameterValues { get; init; }
    public Guid? CallerAgentId { get; init; }
    public Guid? ChannelId { get; init; }
    public Guid? ContextId { get; init; }
}

public sealed record ForeignModuleTaskLaunchResponse(Guid InstanceId);

public sealed record ForeignModuleTaskContextExecuteStatementsRequest
{
    public string ContextId { get; init; } = string.Empty;
    public Guid? ChannelId { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? Variables { get; init; }
    public IReadOnlyList<ForeignModuleTaskStatementInvocationDescriptor> Steps { get; init; } = [];
}

public sealed record ForeignModuleTaskContextExecuteEventHandlerRequest
{
    public string HandlerId { get; init; } = string.Empty;
    public Guid? ChannelId { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? Variables { get; init; }
}

public sealed record ForeignModuleTaskContextExecutionResponse(
    TaskStatementResult Result,
    Guid ChannelId,
    IReadOnlyDictionary<string, JsonElement> Variables);

public sealed record ForeignModuleIdsResponse(IReadOnlyList<Guid> Ids);

public sealed record ForeignModuleLookupItemsResponse(
    IReadOnlyList<ForeignModuleLookupItem> Items);

public sealed record ForeignModuleLookupItem(Guid Id, string Name);

public sealed record ForeignModuleContextAccessibleThreadsRequest
{
    public Guid AgentId { get; init; }
    public Guid CurrentChannelId { get; init; }
    public string CrossThreadPermissionKey { get; init; } = string.Empty;
}

public sealed record ForeignModuleContextThreadMessagesRequest
{
    public Guid ThreadId { get; init; }
    public int MaxMessages { get; init; } = 50;
}

public sealed record ForeignModuleContextThreadsResponse(
    IReadOnlyList<ThreadSummary> Threads);

public sealed record ForeignModuleContextMessagesResponse(
    IReadOnlyList<HostContextChatMessageSummary> Messages);

public sealed record ForeignModuleConversationSteerResponse(
    ConversationSteeringResponse Steering);

public sealed record ForeignModuleConversationSteeringListRequest
{
    public Guid ChannelId { get; init; }
    public Guid? ThreadId { get; init; }
    public int Limit { get; init; } = 20;
}

public sealed record ForeignModuleConversationSteeringListResponse(
    IReadOnlyList<ConversationSteeringResponse> Steering);

public sealed record ForeignModuleQueueMetricsResponse(
    double PendingJobCount,
    double PendingTaskCount,
    double SchedulerPendingJobCount);

public sealed record ForeignModuleHostAgentChatRequest
{
    public Guid InstanceId { get; init; }
    public string TaskName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public Guid? AgentId { get; init; }
}

public sealed record ForeignModuleHostAgentChatToThreadRequest
{
    public Guid InstanceId { get; init; }
    public string TaskName { get; init; } = string.Empty;
    public Guid ThreadId { get; init; }
    public string Message { get; init; } = string.Empty;
    public Guid? AgentId { get; init; }
}

public sealed record ForeignModuleHostAgentTextResponse(string? Text);

public sealed record ForeignModuleHostAgentParseStructuredResponseRequest
{
    public Guid InstanceId { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? TypeName { get; init; }
}

public sealed record ForeignModuleHostAgentFindRequest
{
    public string Search { get; init; } = string.Empty;
}

public sealed record ForeignModuleHostAgentIdResponse(Guid? Id);

public sealed record ForeignModuleHostAgentCreateAgentRequest
{
    public Guid InstanceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public Guid ModelId { get; init; }
    public string? SystemPrompt { get; init; }
    public string? CustomId { get; init; }
}

public sealed record ForeignModuleHostAgentCreateThreadRequest
{
    public Guid InstanceId { get; init; }
    public Guid? ChannelId { get; init; }
    public string? ThreadName { get; init; }
}

public sealed record ForeignModuleHostAgentCreateRoleRequest
{
    public string RoleName { get; init; } = string.Empty;
}

public sealed record ForeignModuleHostAgentSetRolePermissionsRequest
{
    public Guid RoleId { get; init; }
    public string RequestJson { get; init; } = string.Empty;
}

public sealed record ForeignModuleHostAgentAssignRoleRequest
{
    public Guid AgentId { get; init; }
    public Guid RoleId { get; init; }
}

public sealed record ForeignModuleHostAgentCreateChannelRequest
{
    public Guid InstanceId { get; init; }
    public string Title { get; init; } = string.Empty;
    public Guid AgentId { get; init; }
    public string? CustomId { get; init; }
}

public sealed record ForeignModuleHostAgentAddAllowedAgentRequest
{
    public Guid InstanceId { get; init; }
    public Guid AgentId { get; init; }
    public Guid? ChannelId { get; init; }
}

public sealed record ForeignModuleAgentCreateRequest
{
    public string Name { get; init; } = string.Empty;
    public Guid ModelId { get; init; }
    public string? SystemPrompt { get; init; }
}

public sealed record ForeignModuleAgentCreateResponse(
    Guid AgentId,
    string ModelName,
    string AgentName);

public sealed record ForeignModuleAgentUpdateRequest
{
    public Guid AgentId { get; init; }
    public string? Name { get; init; }
    public string? SystemPrompt { get; init; }
    public Guid? ModelId { get; init; }
}

public sealed record ForeignModuleAgentUpdateResponse(string Result);

public sealed record ForeignModuleSetHeaderRequest
{
    public Guid Id { get; init; }
    public string? Header { get; init; }
}

public sealed record ForeignModuleModelEnsureProviderRequest
{
    public string ProviderKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

public sealed record ForeignModuleModelEnsureModelRequest
{
    public string ModelName { get; init; } = string.Empty;
    public Guid ProviderId { get; init; }
    public IReadOnlyList<string> CapabilityTags { get; init; } = [];
}

public sealed record ForeignModuleModelMetadataRequest
{
    public Guid ModelId { get; init; }
}

public sealed record ForeignModuleModelDeleteRequest
{
    public Guid ModelId { get; init; }
}

public sealed record ForeignModuleGuidResponse(Guid Id);

public sealed record ForeignModuleModelProviderInfoResponse(
    ModelProviderInfo? Info);

public sealed record ForeignModuleModelLocalFilePathResponse(string? Path);

public sealed record ForeignModuleModelMetadataResponse(
    ModelMetadata? Metadata);

public sealed record ForeignModuleBooleanResponse(bool Value);

public sealed record ForeignModuleExternalModulesRootResponse(string Directory);

public sealed record ForeignModuleRegisteredRequest
{
    public string ModuleId { get; init; } = string.Empty;
}

public sealed record ForeignModuleToolPrefixRegisteredRequest
{
    public string ToolPrefix { get; init; } = string.Empty;
}

public sealed record ForeignModuleRegisteredResponse(bool IsRegistered);

public sealed record ForeignModuleLoadRequest
{
    public string ModuleDir { get; init; } = string.Empty;
}

public sealed record ForeignModuleModuleIdRequest
{
    public string ModuleId { get; init; } = string.Empty;
}

public sealed record ForeignModuleStateResponseEnvelope(ModuleStateResponse State);

public sealed record ForeignModuleInfoListResponse(
    IReadOnlyList<ModuleInfo> Modules);

public sealed record ForeignModuleToolInvokeRequest
{
    public string ToolName { get; init; } = string.Empty;
    public JsonElement Parameters { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed record ForeignModuleToolInvokeResponse(string Result);

public sealed record ForeignModuleStorageContractsResponse(
    IReadOnlyList<ModuleStorageContractDescriptor> Contracts);

public sealed record ForeignModuleStorageInvokeRequest
{
    public string? ModuleId { get; init; }
    public string StorageName { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public JsonElement Parameters { get; init; }
}

public sealed record ForeignModuleStorageInvokeResponse(JsonElement Result);
