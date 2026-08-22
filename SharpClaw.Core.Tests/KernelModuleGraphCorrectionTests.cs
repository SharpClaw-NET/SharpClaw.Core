using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelModuleGraphCorrectionTests
{
    [Fact]
    public async Task Module_graph_retains_di_contract_storage_and_chat_declarations()
    {
        var registry = new KernelModuleRegistry();
        registry.Add(new GraphModule(4096));
        registry.Add(new ContractConsumerModule());

        var graph = registry.Compile();
        var service = graph.GetRequiredService<ModuleService>();
        var module = Assert.Single(graph.Modules.Modules, value => value.Identity.Id == "graph.module");
        var storage = Assert.Single(graph.Modules.Storage);

        Assert.Equal("dependency-ready", service.Value);
        Assert.Contains(typeof(ModuleService), module.ServiceTypes);
        Assert.Equal(2, graph.Modules.Contracts.Count);
        Assert.Equal("documents", storage.StorageName);
        Assert.Equal(4096, storage.MaxDocumentBytes);
        Assert.Equal(typeof(ModuleConversationResolver), graph.Modules.ConversationResolver);
        Assert.Contains(typeof(ModuleContributor), graph.Modules.ContextContributors);

        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var contribution = await graph.CreateChatContextAssembler(dispatcher).BuildAsync(
            new ChatContextRequest(
                Guid.NewGuid(),
                new ChatProfile("provider", Guid.NewGuid()),
                []),
            CancellationToken.None);
        Assert.Equal("dependency-ready", Assert.Single(contribution.SystemPromptSegments).Content);
    }

    [Fact]
    public void Missing_required_module_contract_fails_graph_compilation()
    {
        var registry = new KernelModuleRegistry();
        registry.Add(new ContractConsumerModule());

        var exception = Assert.Throws<KernelGraphCompilationException>(() => registry.Compile());

        Assert.Contains("sample.contract", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no compatible export", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unresolvable_declared_chat_service_fails_graph_compilation()
    {
        var registry = new KernelModuleRegistry();
        registry.Add(new BrokenServiceModule());

        var exception = Assert.Throws<KernelGraphCompilationException>(() => registry.Compile());

        Assert.Contains(typeof(BrokenContributor).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be resolved", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unresolvable_declared_service_fails_dependency_closure()
    {
        var registry = new KernelModuleRegistry();
        registry.Add(new BrokenDeclaredServiceModule());

        var exception = Assert.Throws<KernelGraphCompilationException>(() => registry.Compile());

        Assert.Contains(typeof(BrokenDeclaredService).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be resolved", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Competing_exclusive_resolver_slots_fail_graph_compilation()
    {
        var registry = new KernelModuleRegistry();
        registry.Add(new ResolverModule("resolver.one", "one"));
        registry.Add(new ResolverModule("resolver.two", "two"));

        var exception = Assert.Throws<KernelGraphCompilationException>(() => registry.Compile());

        Assert.Contains("conversation resolver", exception.Message, StringComparison.Ordinal);
        Assert.Contains("competing claims", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Module_declaration_mutation_changes_contract_hash()
    {
        var firstRegistry = new KernelModuleRegistry();
        firstRegistry.Add(new GraphModule(4096));
        var first = firstRegistry.Compile().ActionSnapshot.ContractHash;

        var secondRegistry = new KernelModuleRegistry();
        secondRegistry.Add(new GraphModule(8192));
        var second = secondRegistry.Compile().ActionSnapshot.ContractHash;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Module_tool_handler_resolves_its_declared_service_graph()
    {
        var registry = new KernelModuleRegistry();
        registry.Add(new ToolServiceModule());
        var graph = registry.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var pipeline = new UnifiedToolPipeline(graph, dispatcher);

        var outcome = await pipeline.InvokeAsync(
            new ToolInvocation(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "call",
                "service_tool",
                JsonSerializer.SerializeToElement(new { }),
                KernelTestExecution.CreateToolContext()),
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("module-service", outcome.Result!.Content);
    }

    private sealed class GraphModule(int maximumDocumentBytes) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("graph.module", "Graph module", "graph");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton<ModuleDependency>();
            module.Services.AddSingleton<ModuleService>();
            module.Contracts.Export<SampleContract>("sample.contract", 2, 2048);
            module.Storage.Add(new ModuleStorageContractDescriptor(
                Identity.Id,
                "documents",
                [new ModuleStorageOperationDescriptor("read", "Read one document.")],
                "Module documents.",
                [],
                maximumDocumentBytes,
                16));
            module.Chat.UseConversationResolver<ModuleConversationResolver>(
                new ExclusiveRegistration("graph.conversation"));
            module.Chat.AddContextContributor<ModuleContributor>();
        }
    }

    private sealed class ContractConsumerModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("contract.consumer", "Contract consumer", "consumer");

        public void Configure(ISharpClawModuleBuilder module) =>
            module.Contracts.Require<SampleContract>("sample.contract", 1, false);
    }

    private sealed class BrokenServiceModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("broken.module", "Broken module", "broken");

        public void Configure(ISharpClawModuleBuilder module) =>
            module.Chat.AddContextContributor<BrokenContributor>();
    }

    private sealed class BrokenDeclaredServiceModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("broken.service.module", "Broken service module", "broken_service");

        public void Configure(ISharpClawModuleBuilder module) =>
            module.Services.AddSingleton<BrokenDeclaredService>();
    }

    private sealed class ResolverModule(string id, string registration) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new(id, id, id.Replace('.', '_'));

        public void Configure(ISharpClawModuleBuilder module) =>
            module.Chat.UseConversationResolver<SimpleConversationResolver>(
                new ExclusiveRegistration(registration));
    }

    private sealed class ToolServiceModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new("tool.service.module", "Tool service module", "tool_service");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton<ToolDependency>();
            module.Tools.Add<DependencyToolHandler>(new ToolDescriptor(
                "service_tool",
                "Returns a value from a module service.",
                JsonSerializer.SerializeToElement(new { type = "object" })));
        }
    }

    private sealed record SampleContract(string Value);

    private sealed class ModuleDependency
    {
        public string Value => "dependency-ready";
    }

    private sealed class ModuleService(ModuleDependency dependency)
    {
        public string Value => dependency.Value;
    }

    private sealed class ToolDependency
    {
        public string Value => "module-service";
    }

    private sealed class DependencyToolHandler(ToolDependency dependency) : IToolHandler
    {
        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ToolResult.Text(dependency.Value));
    }

    private sealed class MissingDependency;

    private sealed class BrokenDeclaredService(MissingDependency dependency)
    {
        private readonly MissingDependency _dependency = dependency;
    }

    private sealed class ModuleConversationResolver(ModuleService service) : IConversationResolver
    {
        public ValueTask<ConversationSelection> ResolveAsync(
            ChatTurnInput input,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ConversationSelection(Guid.NewGuid(), service.Value.Length > 0));
    }

    private sealed class SimpleConversationResolver : IConversationResolver
    {
        public ValueTask<ConversationSelection> ResolveAsync(
            ChatTurnInput input,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ConversationSelection(Guid.NewGuid()));
    }

    private sealed class ModuleContributor(ModuleService service) : IChatContextContributor
    {
        public ValueTask<ChatContextContribution> ContributeAsync(
            ChatContextRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ChatContextContribution(
                [new SystemPromptSegment("module", service.Value)],
                [],
                []));
    }

    private sealed class BrokenContributor(MissingDependency dependency) : IChatContextContributor
    {
        private readonly MissingDependency _dependency = dependency;

        public ValueTask<ChatContextContribution> ContributeAsync(
            ChatContextRequest request,
            CancellationToken cancellationToken)
        {
            _ = _dependency;
            return ValueTask.FromResult(ChatContextContribution.Empty);
        }
    }
}
