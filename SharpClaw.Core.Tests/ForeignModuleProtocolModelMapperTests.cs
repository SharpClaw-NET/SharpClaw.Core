using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Modules.Foreign;
using SharpClaw.Core.Modules.Foreign;

namespace SharpClaw.Core.Tests;

public sealed class ForeignModuleProtocolModelMapperTests
{
    [Fact]
    public void Tool_descriptor_maps_to_core_tool_definition()
    {
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var descriptor = new ForeignModuleToolDescriptor(
            "sample_tool",
            "Runs a sample tool.",
            schema.RootElement.Clone(),
            new ForeignModulePermissionDescriptor(IsPerResource: true, DelegateTo: "CheckSampleAsync"),
            TimeoutSeconds: 12,
            Aliases: ["sample_alias"]);

        var definition = descriptor.ToModuleToolDefinition();

        Assert.Equal("sample_tool", definition.Name);
        Assert.Equal("Runs a sample tool.", definition.Description);
        Assert.True(definition.Permission.IsPerResource);
        Assert.Null(definition.Permission.Check);
        Assert.Equal("CheckSampleAsync", definition.Permission.DelegateTo);
        Assert.Equal(12, definition.TimeoutSeconds);
        Assert.Equal(["sample_alias"], definition.Aliases);
    }

    [Fact]
    public void Inline_tool_descriptor_maps_to_core_inline_tool_definition()
    {
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var descriptor = new ForeignModuleInlineToolDescriptor(
            "sample_inline",
            "Runs inline.",
            schema.RootElement.Clone(),
            new ForeignModulePermissionDescriptor(IsPerResource: false),
            Aliases: ["inline_alias"]);

        var definition = descriptor.ToModuleInlineToolDefinition();

        Assert.Equal("sample_inline", definition.Name);
        Assert.Equal("Runs inline.", definition.Description);
        Assert.NotNull(definition.Permission);
        Assert.False(definition.Permission!.IsPerResource);
        Assert.NotNull(definition.Permission.Check);
        Assert.Equal(["inline_alias"], definition.Aliases);
    }

    [Fact]
    public void Global_flag_descriptor_maps_to_contract_module_descriptor()
    {
        var descriptor = new ForeignModuleGlobalFlagDescriptor(
            "CanUseSample",
            "Use Sample",
            "Allows sample execution.",
            "UseSampleAsync");

        var flag = descriptor.ToModuleGlobalFlagDescriptor();

        Assert.Equal("CanUseSample", flag.FlagKey);
        Assert.Equal("Use Sample", flag.DisplayName);
        Assert.Equal("Allows sample execution.", flag.Description);
        Assert.Equal("UseSampleAsync", flag.DelegateMethodName);
    }

    [Fact]
    public void Protocol_contract_descriptors_map_to_contract_models()
    {
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var operation = new ForeignModuleProtocolContractOperation(
            "invoke",
            schema.RootElement.Clone(),
            schema.RootElement.Clone(),
            "Invokes sample behavior.");
        var exportDescriptor = new ForeignModuleProtocolContractExportDescriptor(
            "sample_contract",
            schema.RootElement.Clone(),
            [operation],
            "Sample export.");
        var requirementDescriptor = new ForeignModuleProtocolContractRequirementDescriptor(
            "sample_contract",
            schema.RootElement.Clone(),
            Optional: true,
            "Sample requirement.");

        var export = exportDescriptor.ToProtocolContractExport();
        var requirement = requirementDescriptor.ToProtocolContractRequirement();

        Assert.Equal("sample_contract", export.ContractName);
        Assert.Same(operation, Assert.Single(export.Operations));
        Assert.Equal("Sample export.", export.Description);
        Assert.Equal("sample_contract", requirement.ContractName);
        Assert.True(requirement.Optional);
        Assert.Equal("Sample requirement.", requirement.Description);
    }

    [Fact]
    public void Health_response_maps_to_core_health_status()
    {
        using var detailsJson = JsonDocument.Parse("""{"queueDepth":3}""");
        var details = new Dictionary<string, JsonElement>
        {
            ["queueDepth"] = detailsJson.RootElement.GetProperty("queueDepth").Clone()
        };
        var response = new ForeignModuleHealthResponse(
            IsHealthy: false,
            Message: "Queue is backed up.",
            Details: details);

        var status = response.ToModuleHealthStatus();

        Assert.False(status.IsHealthy);
        Assert.Equal("Queue is backed up.", status.Message);
        var value = Assert.IsType<JsonElement>(status.Details!["queueDepth"]);
        Assert.Equal(3, value.GetInt32());
    }

    [Fact]
    public void Core_contexts_map_to_contract_foreign_module_contexts()
    {
        var job = new AgentJobContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "sample_action");
        var inline = new InlineToolContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "call-1");

        var foreignJob = ForeignModuleProtocolModelMapper.ToForeignModuleAgentJobContext(job);
        var foreignInline = ForeignModuleProtocolModelMapper.ToForeignModuleInlineToolContext(inline);

        Assert.Equal(job.JobId, foreignJob.JobId);
        Assert.Equal(job.AgentId, foreignJob.AgentId);
        Assert.Equal(job.ChannelId, foreignJob.ChannelId);
        Assert.Equal(job.ResourceId, foreignJob.ResourceId);
        Assert.Equal(job.ActionKey, foreignJob.ActionKey);
        Assert.Equal(inline.AgentId, foreignInline.AgentId);
        Assert.Equal(inline.ChannelId, foreignInline.ChannelId);
        Assert.Equal(inline.ThreadId, foreignInline.ThreadId);
        Assert.Equal(inline.ToolCallId, foreignInline.ToolCallId);
    }
}
