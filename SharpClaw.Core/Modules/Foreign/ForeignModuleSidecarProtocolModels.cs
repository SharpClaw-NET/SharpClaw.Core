using System.Text.Json;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Core.Modules.Foreign;

public sealed record ForeignModuleHandshakeRequest(
    int ProtocolVersion,
    string ModuleId,
    string ToolPrefix,
    string? HostVersion = null);

public sealed record ForeignModuleHandshakeResponse(
    int ProtocolVersion,
    string ModuleId,
    string ToolPrefix,
    string Runtime,
    string RuntimeVersion,
    IReadOnlyList<string>? Capabilities = null);

public sealed record ForeignModuleLifecycleRequest(
    int ProtocolVersion,
    string ModuleId);

public sealed record ForeignModuleLifecycleResponse(
    bool Accepted = true,
    string? Message = null);

public sealed record ForeignModuleHealthResponse(
    bool IsHealthy,
    string? Message = null,
    IReadOnlyDictionary<string, JsonElement>? Details = null)
{
    public ModuleHealthStatus ToModuleHealthStatus() =>
        new(IsHealthy, Message, Details?.ToDictionary(kv => kv.Key, kv => (object)kv.Value));
}

public sealed record ForeignModuleDiscoveryResponse(
    IReadOnlyList<ForeignModuleEndpointDescriptor>? Endpoints = null,
    IReadOnlyList<ForeignModuleToolDescriptor>? Tools = null,
    IReadOnlyList<ForeignModuleInlineToolDescriptor>? InlineTools = null,
    IReadOnlyList<ForeignModuleProtocolContractExportDescriptor>? ProtocolContracts = null,
    IReadOnlyList<ForeignModuleProtocolContractRequirementDescriptor>? RequiredProtocolContracts = null,
    IReadOnlyList<ForeignModuleHeaderTagDescriptor>? HeaderTags = null,
    IReadOnlyList<ForeignModuleResourceTypeDescriptor>? ResourceTypes = null,
    IReadOnlyList<ForeignModuleGlobalFlagDescriptor>? GlobalFlags = null,
    IReadOnlyList<ModuleUiContribution>? UiContributions = null,
    IReadOnlyList<ModuleFrontendContribution>? FrontendContributions = null,
    IReadOnlyList<ModuleStorageContractDescriptor>? StorageContracts = null,
    IReadOnlyList<ForeignModuleCliCommandDescriptor>? CliCommands = null,
    ForeignModuleTaskParserDescriptor? TaskParser = null,
    IReadOnlyList<TaskOperationDescriptor>? TaskOperationDescriptors = null,
    IReadOnlyList<ForeignModuleTaskOperationExecutorDescriptor>? TaskOperationExecutors = null,
    IReadOnlyList<ForeignModuleTaskTriggerSourceDescriptor>? TaskTriggerSources = null,
    IReadOnlyList<ForeignModuleTaskTriggerBindingSideEffectDescriptor>? TaskTriggerBindingSideEffects = null,
    IReadOnlyList<ForeignModuleTaskMetricProviderDescriptor>? TaskMetricProviders = null,
    IReadOnlyList<ForeignModuleTaskEventSinkDescriptor>? TaskEventSinks = null,
    IReadOnlyList<ForeignModuleProviderPluginDescriptor>? ProviderPlugins = null);

public sealed record ForeignModuleEndpointDescriptor(
    string Method,
    string RoutePattern,
    string ResponseMode,
    string? AuthPolicy = null,
    ForeignModulePermissionDescriptor? Permission = null,
    string? ContributionId = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

public static class ForeignModuleEndpointAuthPolicy
{
    public const string Anonymous = "anonymous";
    public const string Authenticated = "authenticated";
}

public sealed record ForeignModulePermissionDescriptor(
    bool IsPerResource,
    string? DelegateTo = null)
{
    public ModuleToolPermission ToModuleToolPermission() =>
        string.IsNullOrWhiteSpace(DelegateTo)
            ? new ModuleToolPermission(
                IsPerResource,
                (_, _, _, _) => Task.FromResult(
                    AgentActionResult.Approve(
                        "Foreign module tool does not require host permission.",
                        PermissionClearance.Unset)))
            : new ModuleToolPermission(IsPerResource, Check: null, DelegateTo);
}

public sealed record ForeignModuleToolDescriptor(
    string Name,
    string Description,
    JsonElement ParametersSchema,
    ForeignModulePermissionDescriptor? Permission = null,
    int? TimeoutSeconds = null,
    IReadOnlyList<string>? Aliases = null,
    bool SupportsStreaming = false,
    bool SupportsDynamicCompletionBehavior = false,
    ModuleJobCompletionBehavior CompletionBehavior =
        ModuleJobCompletionBehavior.CompleteWhenExecutionReturns)
{
    public ModuleToolDefinition ToModuleToolDefinition() =>
        new(
            Name,
            Description,
            ParametersSchema,
            (Permission ?? new ForeignModulePermissionDescriptor(IsPerResource: false))
                .ToModuleToolPermission(),
            TimeoutSeconds,
            Aliases);
}

public sealed record ForeignModuleInlineToolDescriptor(
    string Name,
    string Description,
    JsonElement ParametersSchema,
    ForeignModulePermissionDescriptor? Permission = null,
    IReadOnlyList<string>? Aliases = null)
{
    public ModuleInlineToolDefinition ToModuleInlineToolDefinition() =>
        new(
            Name,
            Description,
            ParametersSchema,
            Permission?.ToModuleToolPermission(),
            Aliases);
}

public sealed record ForeignModuleHeaderTagDescriptor(
    string Name,
    bool SupportsContext = true);

public sealed record ForeignModuleResourceTypeDescriptor(
    string ResourceType,
    string GrantLabel,
    string DelegateMethodName,
    string? DefaultResourceKey = null,
    bool SupportsLookupItems = false);

public sealed record ForeignModuleGlobalFlagDescriptor(
    string FlagKey,
    string DisplayName,
    string Description,
    string DelegateMethodName)
{
    public ModuleGlobalFlagDescriptor ToModuleGlobalFlagDescriptor() =>
        new(FlagKey, DisplayName, Description, DelegateMethodName);
}

public sealed record ForeignModuleCliCommandDescriptor(
    string Name,
    string[]? Aliases,
    ModuleCliScope Scope,
    string Description,
    string[]? UsageLines);

public sealed record ForeignModuleToolExecutionRequest(
    int ProtocolVersion,
    string ModuleId,
    string ToolName,
    JsonElement Parameters,
    ForeignModuleAgentJobContext Job);

public sealed record ForeignModuleInlineToolExecutionRequest(
    int ProtocolVersion,
    string ModuleId,
    string ToolName,
    JsonElement Parameters,
    ForeignModuleInlineToolContext Context);

public sealed record ForeignModuleToolExecutionResponse(
    string? Result = null,
    ModuleJobCompletionBehavior? CompletionBehavior = null);

public sealed record ForeignModuleToolCompletionBehaviorRequest(
    int ProtocolVersion,
    string ModuleId,
    string ToolName,
    JsonElement Parameters,
    ForeignModuleAgentJobContext Job);

public sealed record ForeignModuleToolCompletionBehaviorResponse(
    ModuleJobCompletionBehavior CompletionBehavior);

public sealed record ForeignModuleAgentJobContext(
    Guid JobId,
    Guid AgentId,
    Guid ChannelId,
    Guid? ResourceId,
    string? ActionKey)
{
    public static ForeignModuleAgentJobContext From(AgentJobContext job) =>
        new(job.JobId, job.AgentId, job.ChannelId, job.ResourceId, job.ActionKey);
}

public sealed record ForeignModuleInlineToolContext(
    Guid AgentId,
    Guid ChannelId,
    Guid? ThreadId,
    string ToolCallId)
{
    public static ForeignModuleInlineToolContext From(InlineToolContext context) =>
        new(context.AgentId, context.ChannelId, context.ThreadId, context.ToolCallId);
}

public sealed record ForeignModuleToolStreamEvent(
    string? Delta = null,
    string? Result = null,
    string? Error = null,
    bool IsFinal = false);

public sealed record ForeignModuleHeaderTagResolveRequest(
    int ProtocolVersion,
    string ModuleId,
    string Name,
    ModuleHeaderTagContext? Context = null);

public sealed record ForeignModuleHeaderTagResolveResponse(string Value);

public sealed record ForeignModuleResourceRequest(
    int ProtocolVersion,
    string ModuleId,
    string ResourceType);

public sealed record ForeignModuleResourceLookupItem(Guid Id, string Name);

public sealed record ForeignModuleResourceIdsResponse(IReadOnlyList<Guid> Ids);

public sealed record ForeignModuleResourceLookupResponse(
    IReadOnlyList<ForeignModuleResourceLookupItem> Items);

public sealed record ForeignModuleCliExecutionRequest(
    int ProtocolVersion,
    string ModuleId,
    string CommandName,
    IReadOnlyList<string> Args);

public sealed record ForeignModuleCliExecutionResponse(
    bool Success,
    string? Stdout = null,
    string? Stderr = null);

public sealed record ForeignModuleProtocolContractExportDescriptor(
    string ContractName,
    JsonElement Schema,
    IReadOnlyList<ForeignModuleProtocolContractOperation> Operations,
    string? Description = null)
{
    public ForeignModuleProtocolContractExport ToProtocolContractExport() =>
        new(ContractName, Schema, Operations, Description);
}

public sealed record ForeignModuleProtocolContractRequirementDescriptor(
    string ContractName,
    JsonElement? Schema = null,
    bool Optional = false,
    string? Description = null)
{
    public ForeignModuleProtocolContractRequirement ToProtocolContractRequirement() =>
        new(ContractName, Schema, Optional, Description);
}

public sealed record ForeignModuleProtocolContractInvocationRequest(
    int ProtocolVersion,
    string ModuleId,
    string ContractName,
    string Operation,
    JsonElement Parameters);

public sealed record ForeignModuleProtocolContractInvocationResponse(
    JsonElement Result);

public sealed record ForeignModuleTaskParserDescriptor(
    IReadOnlyList<ForeignModuleTaskParserOperationMapping>? OperationKeyMappings = null,
    IReadOnlyList<ForeignModuleTaskParserEventMapping>? EventTriggerMappings = null,
    IReadOnlyList<string>? SingleArgExpressionMethods = null,
    IReadOnlyList<ForeignModuleTaskTriggerAttributeHandlerDescriptor>? TriggerAttributeHandlers = null);

public sealed record ForeignModuleTaskParserOperationMapping(
    string MethodName,
    string StatementKey,
    string ModuleId);

public sealed record ForeignModuleTaskParserEventMapping(
    string MethodName,
    string TriggerKey,
    string ModuleId);

public sealed record ForeignModuleTaskTriggerAttributeHandlerDescriptor(
    string Name,
    IReadOnlyList<string>? NamedStringArgs = null,
    IReadOnlyList<string>? NamedIntArgs = null,
    IReadOnlyList<string>? NamedDoubleArgs = null);

public sealed record ForeignModuleTaskOperationExecutorDescriptor(
    string ModuleId,
    IReadOnlyList<string> OperationKeys,
    bool SupportsInvocation = false);

public sealed record ForeignModuleTaskTriggerSourceDescriptor(
    IReadOnlyList<string> TriggerKeys,
    bool OwnsBindingPersistence = false);

public sealed record ForeignModuleTaskTriggerBindingSideEffectDescriptor(
    string TriggerKey);

public sealed record ForeignModuleTaskMetricProviderDescriptor(
    string MetricName,
    string Description);

public sealed record ForeignModuleTaskEventSinkDescriptor(
    SharpClawEventType SubscribedEvents);

public sealed record ForeignModuleTaskTriggerAttributeHandleRequest(
    int ProtocolVersion,
    string ModuleId,
    string HandlerName,
    ForeignModuleTaskTriggerAttributeContextDescriptor Context);

public sealed record ForeignModuleTaskTriggerAttributeContextDescriptor(
    string AttributeName,
    int Line,
    int ArgumentCount,
    IReadOnlyList<string?> StringArgs,
    IReadOnlyList<int?> IntArgs,
    IReadOnlyList<string?> RawArgs,
    IReadOnlyDictionary<string, string?> NamedStringArgs,
    IReadOnlyDictionary<string, int?> NamedIntArgs,
    IReadOnlyDictionary<string, double?> NamedDoubleArgs);

public sealed record ForeignModuleTaskTriggerAttributeHandleResponse(
    TaskTriggerDefinition? Trigger = null,
    IReadOnlyList<ForeignModuleTaskTriggerAttributeDiagnostic>? Diagnostics = null);

public sealed record ForeignModuleTaskTriggerAttributeDiagnostic(
    TaskTriggerAttributeDiagnosticSeverity Severity,
    string Code,
    string Message);

public sealed record ForeignModuleTaskTriggerStartRequest(
    int ProtocolVersion,
    string ModuleId,
    IReadOnlyList<string> TriggerKeys,
    IReadOnlyList<ForeignModuleTaskTriggerSourceContextDescriptor> Contexts);

public sealed record ForeignModuleTaskTriggerStopRequest(
    int ProtocolVersion,
    string ModuleId,
    IReadOnlyList<string> TriggerKeys);

public sealed record ForeignModuleTaskTriggerSourceContextDescriptor(
    Guid TaskDefinitionId,
    TaskTriggerDefinition Definition);

public sealed record ForeignModuleTaskTriggerDefinitionRequest(
    int ProtocolVersion,
    string ModuleId,
    string TriggerKey,
    TaskTriggerDefinition Definition);

public sealed record ForeignModuleTaskTriggerBindingValueResponse(
    string? Value);

public sealed record ForeignModuleTaskTriggerSyncBindingsRequest(
    int ProtocolVersion,
    string ModuleId,
    IReadOnlyList<string> TriggerKeys,
    TaskDefinitionDescriptor Definition,
    IReadOnlyList<TaskTriggerDefinition> OwnedTriggers);

public sealed record ForeignModuleTaskTriggerSyncBindingsResponse(
    bool Changed);

public sealed record ForeignModuleTaskTriggerRemoveBindingsRequest(
    int ProtocolVersion,
    string ModuleId,
    IReadOnlyList<string> TriggerKeys,
    Guid DefinitionId);

public sealed record ForeignModuleTaskTriggerBindingCreatedRequest(
    int ProtocolVersion,
    string ModuleId,
    string TriggerKey,
    TaskDefinitionDescriptor Definition,
    TaskTriggerDefinition Trigger,
    TaskTriggerBindingDescriptor Binding);

public sealed record ForeignModuleTaskTriggerBindingRemovedRequest(
    int ProtocolVersion,
    string ModuleId,
    string TriggerKey,
    TaskTriggerBindingDescriptor Binding);

public sealed record ForeignModuleTaskMetricValueRequest(
    int ProtocolVersion,
    string ModuleId,
    string MetricName);

public sealed record ForeignModuleTaskMetricValueResponse(
    double Value);

public sealed record ForeignModuleTaskEventSinkRequest(
    int ProtocolVersion,
    string ModuleId,
    SharpClawEvent Event);

public sealed record ForeignModuleTaskAckResponse(
    bool Accepted = true);

public sealed record ForeignModuleProviderPluginDescriptor(
    string ProviderKey,
    string DisplayName,
    string? OwnerModuleId = null,
    bool RequiresEndpoint = false,
    bool SupportsAutomaticEndpointDiscovery = false,
    bool IsSeedable = true,
    bool RequiresApiKey = true,
    bool SupportsNativeToolCalling = false,
    IReadOnlyList<ProviderCostSeed>? CostSeeds = null,
    ForeignModuleCompletionParameterSpecDescriptor? ParameterSpec = null,
    bool SupportsDeviceCodeFlow = false,
    bool SupportsCostFeed = false,
    string? CostFeedPermissionDeniedNote = null);

public sealed record ForeignModuleCompletionParameterSpecDescriptor(
    string ProviderName,
    bool SupportsTemperature = true,
    float TemperatureMin = 0.0f,
    float TemperatureMax = 2.0f,
    bool SupportsTopP = true,
    float TopPMin = 0.0f,
    float TopPMax = 1.0f,
    bool SupportsTopK = true,
    int TopKMin = 1,
    int TopKMax = int.MaxValue,
    bool SupportsFrequencyPenalty = true,
    float FrequencyPenaltyMin = -2.0f,
    float FrequencyPenaltyMax = 2.0f,
    bool SupportsPresencePenalty = true,
    float PresencePenaltyMin = -2.0f,
    float PresencePenaltyMax = 2.0f,
    bool SupportsStop = true,
    int MaxStopSequences = 16,
    bool SupportsSeed = true,
    bool SupportsResponseFormat = true,
    bool RejectsJsonObjectResponseFormat = false,
    bool OnlyJsonObjectResponseFormat = false,
    bool SupportsReasoningEffort = true,
    bool ReasoningEffortInformationalOnly = false,
    string[]? ValidReasoningEffortValues = null,
    bool SupportsToolChoice = true,
    bool SupportsStrictTools = false)
{
    public static ForeignModuleCompletionParameterSpecDescriptor From(ICompletionParameterSpec spec) =>
        new(
            spec.ProviderName,
            spec.SupportsTemperature,
            spec.TemperatureMin,
            spec.TemperatureMax,
            spec.SupportsTopP,
            spec.TopPMin,
            spec.TopPMax,
            spec.SupportsTopK,
            spec.TopKMin,
            spec.TopKMax,
            spec.SupportsFrequencyPenalty,
            spec.FrequencyPenaltyMin,
            spec.FrequencyPenaltyMax,
            spec.SupportsPresencePenalty,
            spec.PresencePenaltyMin,
            spec.PresencePenaltyMax,
            spec.SupportsStop,
            spec.MaxStopSequences,
            spec.SupportsSeed,
            spec.SupportsResponseFormat,
            spec.RejectsJsonObjectResponseFormat,
            spec.OnlyJsonObjectResponseFormat,
            spec.SupportsReasoningEffort,
            spec.ReasoningEffortInformationalOnly,
            spec.ValidReasoningEffortValues,
            spec.SupportsToolChoice,
            spec.SupportsStrictTools);
}

public sealed record ForeignModuleProviderModelListRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey,
    string? Endpoint,
    string ApiKey);

public sealed record ForeignModuleProviderModelListResponse(
    IReadOnlyList<string> ModelIds);

public sealed record ForeignModuleProviderCapabilitiesResolveRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey,
    string ModelName);

public sealed record ForeignModuleProviderCapabilitiesResolveResponse(
    IReadOnlyList<string> Tags);

public sealed record ForeignModuleProviderChatCompletionRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey,
    string? Endpoint,
    string ApiKey,
    string Model,
    string? SystemPrompt,
    IReadOnlyList<ChatCompletionMessage> Messages,
    int? MaxCompletionTokens = null,
    Dictionary<string, JsonElement>? ProviderParameters = null,
    CompletionParameters? CompletionParameters = null);

public sealed record ForeignModuleProviderChatCompletionWithToolsRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey,
    string? Endpoint,
    string ApiKey,
    string Model,
    string? SystemPrompt,
    IReadOnlyList<ToolAwareMessage> Messages,
    IReadOnlyList<ChatToolDefinition> Tools,
    int? MaxCompletionTokens = null,
    Dictionary<string, JsonElement>? ProviderParameters = null,
    CompletionParameters? CompletionParameters = null);

public sealed record ForeignModuleProviderChatCompletionResponse(
    ChatCompletionResult Result);

public sealed record ForeignModuleProviderDeviceCodeStartRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey);

public sealed record ForeignModuleProviderDeviceCodeStartResponse(
    DeviceCodeSession Session);

public sealed record ForeignModuleProviderDeviceCodePollRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey,
    DeviceCodeSession Session);

public sealed record ForeignModuleProviderDeviceCodePollResponse(
    string? AccessToken);

public sealed record ForeignModuleProviderCostFeedRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey,
    string ApiKey,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime);

public sealed record ForeignModuleProviderCostFeedResponse(
    ProviderCostResult? Result);

public sealed record ForeignModuleProviderAgentIdentifierSuffixRequest(
    int ProtocolVersion,
    string ModuleId,
    string ProviderKey,
    string ProviderName,
    Guid ModelId);

public sealed record ForeignModuleProviderAgentIdentifierSuffixResponse(
    string Suffix);
