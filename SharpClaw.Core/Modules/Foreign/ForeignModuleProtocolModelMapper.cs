using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Modules.Foreign;

namespace SharpClaw.Core.Modules.Foreign;

/// <summary>
/// Converts Contracts-owned foreign module protocol DTOs into Core-owned runtime models.
/// </summary>
public static class ForeignModuleProtocolModelMapper
{
    public static ModuleHealthStatus ToModuleHealthStatus(this ForeignModuleHealthResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return new ModuleHealthStatus(
            response.IsHealthy,
            response.Message,
            response.Details?.ToDictionary(kv => kv.Key, kv => (object)kv.Value));
    }

    public static ModuleToolDefinition ToModuleToolDefinition(this ForeignModuleToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new ModuleToolDefinition(
            descriptor.Name,
            descriptor.Description,
            descriptor.ParametersSchema,
            (descriptor.Permission ?? new ForeignModulePermissionDescriptor(IsPerResource: false))
                .ToModuleToolPermission(),
            descriptor.TimeoutSeconds,
            descriptor.Aliases);
    }

    public static ModuleInlineToolDefinition ToModuleInlineToolDefinition(
        this ForeignModuleInlineToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new ModuleInlineToolDefinition(
            descriptor.Name,
            descriptor.Description,
            descriptor.ParametersSchema,
            descriptor.Permission?.ToModuleToolPermission(),
            descriptor.Aliases);
    }

    public static ModuleToolPermission ToModuleToolPermission(this ForeignModulePermissionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return string.IsNullOrWhiteSpace(descriptor.DelegateTo)
            ? new ModuleToolPermission(
                descriptor.IsPerResource,
                (_, _, _, _) => Task.FromResult(
                    AgentActionResult.Approve(
                        "Foreign module tool does not require host permission.",
                        PermissionClearance.Unset)))
            : new ModuleToolPermission(descriptor.IsPerResource, Check: null, descriptor.DelegateTo);
    }

    public static ModuleGlobalFlagDescriptor ToModuleGlobalFlagDescriptor(
        this ForeignModuleGlobalFlagDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new ModuleGlobalFlagDescriptor(
            descriptor.FlagKey,
            descriptor.DisplayName,
            descriptor.Description,
            descriptor.DelegateMethodName);
    }

    public static ForeignModuleProtocolContractExport ToProtocolContractExport(
        this ForeignModuleProtocolContractExportDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new ForeignModuleProtocolContractExport(
            descriptor.ContractName,
            descriptor.Schema,
            descriptor.Operations,
            descriptor.Description);
    }

    public static ForeignModuleProtocolContractRequirement ToProtocolContractRequirement(
        this ForeignModuleProtocolContractRequirementDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new ForeignModuleProtocolContractRequirement(
            descriptor.ContractName,
            descriptor.Schema,
            descriptor.Optional,
            descriptor.Description);
    }

    public static ForeignModuleAgentJobContext ToForeignModuleAgentJobContext(AgentJobContext job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new ForeignModuleAgentJobContext(
            job.JobId,
            job.AgentId,
            job.ChannelId,
            job.ResourceId,
            job.ActionKey);
    }

    public static ForeignModuleInlineToolContext ToForeignModuleInlineToolContext(InlineToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new ForeignModuleInlineToolContext(
            context.AgentId,
            context.ChannelId,
            context.ThreadId,
            context.ToolCallId);
    }
}
