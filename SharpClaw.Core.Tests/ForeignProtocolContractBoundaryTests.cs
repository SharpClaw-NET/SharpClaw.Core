using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Modules.Foreign;
using SharpClaw.Core.Modules;

namespace SharpClaw.Core.Tests;

public sealed class ForeignProtocolContractBoundaryTests
{
    [Fact]
    public void Module_registry_resolves_protocol_contracts_from_contracts_namespace()
    {
        using var schema = JsonDocument.Parse("{}");
        var export = new ForeignModuleProtocolContractExport(
            "sample_contract",
            schema.RootElement.Clone(),
            [
                new ForeignModuleProtocolContractOperation(
                    "invoke",
                    schema.RootElement.Clone(),
                    schema.RootElement.Clone())
            ]);
        var requirement = new ForeignModuleProtocolContractRequirement("sample_contract");
        var invoker = new TestProtocolInvoker(export.ContractName, export.Operations);
        var provider = new ProtocolProviderModule(export, invoker);
        var consumer = new ProtocolConsumerModule(requirement);
        var registry = new ModuleRegistry();

        registry.Register(provider);
        registry.Register(consumer);

        var resolvedExport = registry.ResolveProtocolContract("sample_contract");
        Assert.NotNull(resolvedExport);
        Assert.Equal("provider", resolvedExport.Value.ModuleId);
        Assert.Same(export, resolvedExport.Value.Export);
        Assert.Same(invoker, registry.ResolveProtocolContractInvoker("sample_contract"));
        Assert.Empty(registry.GetUnsatisfiedProtocolRequirements("consumer"));
        Assert.Equal(
            "SharpClaw.Contracts.Modules.Foreign",
            typeof(ForeignModuleProtocolContractExport).Namespace);
    }

    private sealed class ProtocolProviderModule(
        ForeignModuleProtocolContractExport export,
        IForeignModuleProtocolContractInvoker invoker)
        : TestModule("provider", "provider"), IForeignModuleProtocolContractExporter
    {
        public IReadOnlyList<ForeignModuleProtocolContractExport> ExportedProtocolContracts { get; } = [export];
        public IReadOnlyList<ForeignModuleProtocolContractRequirement> RequiredProtocolContracts => [];

        public IForeignModuleProtocolContractInvoker GetProtocolContractInvoker(string contractName) =>
            string.Equals(contractName, export.ContractName, StringComparison.Ordinal)
                ? invoker
                : throw new InvalidOperationException($"Unknown contract '{contractName}'.");
    }

    private sealed class ProtocolConsumerModule(
        ForeignModuleProtocolContractRequirement requirement)
        : TestModule("consumer", "consumer"), IForeignModuleProtocolContractModule
    {
        public IReadOnlyList<ForeignModuleProtocolContractExport> ExportedProtocolContracts => [];
        public IReadOnlyList<ForeignModuleProtocolContractRequirement> RequiredProtocolContracts { get; } = [requirement];
    }

    private sealed class TestProtocolInvoker(
        string contractName,
        IReadOnlyList<ForeignModuleProtocolContractOperation> operations)
        : IForeignModuleProtocolContractInvoker
    {
        public string ContractName { get; } = contractName;
        public IReadOnlyList<ForeignModuleProtocolContractOperation> Operations { get; } = operations;

        public Task<JsonElement> InvokeAsync(
            string operation,
            JsonElement parameters,
            CancellationToken ct = default) =>
            Task.FromResult(parameters.Clone());
    }

    private abstract class TestModule(string id, string toolPrefix) : ISharpClawCoreModule
    {
        public string Id { get; } = id;
        public string DisplayName => Id;
        public string ToolPrefix { get; } = toolPrefix;

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            throw new NotImplementedException();
    }
}
