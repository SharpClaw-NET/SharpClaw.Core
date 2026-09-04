using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelServiceGraphCorrectionTests
{
    [Fact]
    public async Task Registration_graph_retains_di_contract_storage_and_chat_declarations()
    {
        var registry = new ServiceCollection();
        registry.Add(new GraphRegistration(4096));
        registry.Add(new ContractConsumerRegistration());

        var graph = registry.Compile();
        var service = graph.GetRequiredService<RegistrationService>();
        var storage = Assert.Single(graph.Services.Storage);

        Assert.Equal("dependency-ready", service.Value);
        Assert.Equal(2, graph.Services.Contracts.Count);
        Assert.Equal("documents", storage.StorageName);
        Assert.Equal(4096, storage.MaxDocumentBytes);
        Assert.IsType<RegistrationConversationResolver>(graph.Services.ConversationResolver);
        Assert.Contains(graph.Services.ContextContributors, value => value is RegistrationContributor);

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
    public void Missing_required_registration_contract_fails_graph_compilation()
    {
        var registry = new ServiceCollection();
        registry.Add(new ContractConsumerRegistration());

        var exception = Assert.Throws<KernelGraphCompilationException>(() =>
            registry.CompileDeclaredServices());

        Assert.Contains("sample.contract", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no compatible service", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unresolvable_declared_chat_service_fails_graph_compilation()
    {
        var registry = new ServiceCollection();
        registry.Add(new BrokenServiceRegistration());

        var exception = Assert.Throws<AggregateException>(() =>
            registry.CompileDeclaredServices());

        Assert.Contains(typeof(BrokenContributor).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Unable to resolve service", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unresolvable_declared_service_fails_dependency_closure()
    {
        var registry = new ServiceCollection();
        registry.Add(new BrokenDeclaredServiceRegistration());

        var exception = Assert.Throws<AggregateException>(() =>
            registry.CompileDeclaredServices());

        Assert.Contains(typeof(BrokenDeclaredService).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Unable to resolve service", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Competing_exclusive_resolver_slots_fail_graph_compilation()
    {
        var registry = new ServiceCollection();
        registry.Add(new ResolverRegistration("resolver.one", "one"));
        registry.Add(new ResolverRegistration("resolver.two", "two"));

        var exception = Assert.Throws<KernelGraphCompilationException>(() => registry.Compile());

        Assert.Contains(nameof(IConversationResolver), exception.Message, StringComparison.Ordinal);
        Assert.Contains("more than one", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registration_declaration_mutation_changes_contract_hash()
    {
        var firstRegistry = new ServiceCollection();
        firstRegistry.Add(new GraphRegistration(4096));
        var first = firstRegistry.Compile().ActionSnapshot.ContractHash;

        var secondRegistry = new ServiceCollection();
        secondRegistry.Add(new GraphRegistration(8192));
        var second = secondRegistry.Compile().ActionSnapshot.ContractHash;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Registration_tool_handler_resolves_its_declared_service_graph()
    {
        var registry = new ServiceCollection();
        registry.Add(new ToolServiceRegistration());
        var graph = registry.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var pipeline = new UnifiedToolPipeline(graph, dispatcher);

        var outcome = await pipeline.InvokeAsync(
            KernelTestExecution.CreateToolInvocation("service_tool"),
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("registration-service", outcome.Result!.Content);
    }

    private sealed class GraphRegistration(int maximumDocumentBytes) : ITestServiceRegistration
    {
        public TestSourceIdentity Identity { get; } = new("graph.registration", "Graph registration", "graph");

        public void Configure(IServiceCollection services)
        {
            services.AddSingleton<RegistrationDependency>();
            services.AddSingleton<RegistrationService>();
            services.AddContractExport<SampleContract>(Identity.Id, "sample.contract", 2, 2048);
            services.AddStorage(new ScopedStorageContractDescriptor(
                Identity.Id,
                "documents",
                [new ScopedStorageOperationDescriptor("read", "Read one document.")],
                "Registration documents.",
                [],
                maximumDocumentBytes,
                16));
            services.AddConversationResolver<RegistrationConversationResolver>();
            services.AddSingleton<IConversationStore, RegistrationConversationStore>();
            services.AddContextContributor<RegistrationContributor>();
        }
    }

    private sealed class ContractConsumerRegistration : ITestServiceRegistration
    {
        public TestSourceIdentity Identity { get; } = new("contract.consumer", "Contract consumer", "consumer");

        public void Configure(IServiceCollection services) =>
            services.AddContractRequirement<SampleContract>(Identity.Id, "sample.contract", 1, false);
    }

    private sealed class BrokenServiceRegistration : ITestServiceRegistration
    {
        public TestSourceIdentity Identity { get; } = new("broken.registration", "Broken registration", "broken");

        public void Configure(IServiceCollection services) =>
            services.AddContextContributor<BrokenContributor>();
    }

    private sealed class BrokenDeclaredServiceRegistration : ITestServiceRegistration
    {
        public TestSourceIdentity Identity { get; } =
            new("broken.service.registration", "Broken service registration", "broken_service");

        public void Configure(IServiceCollection services) =>
            services.AddSingleton<BrokenDeclaredService>();
    }

    private sealed class ResolverRegistration(string id, string claimId) : ITestServiceRegistration
    {
        public TestSourceIdentity Identity { get; } = new(id, id, id.Replace('.', '_'));

        public void Configure(IServiceCollection services)
        {
            _ = claimId;
            services.AddConversationResolver<SimpleConversationResolver>();
            services.AddSingleton<IConversationStore, RegistrationConversationStore>();
        }
    }

    private sealed class ToolServiceRegistration : ITestServiceRegistration
    {
        public TestSourceIdentity Identity { get; } =
            new("tool.service.registration", "Tool service registration", "tool_service");

        public void Configure(IServiceCollection services)
        {
            services.AddSingleton<ToolDependency>();
            services.AddTool<DependencyToolHandler>(Identity.Id, new ToolDescriptor(
                "service_tool",
                "Returns a value from a registration service.",
                JsonSerializer.SerializeToElement(new { type = "object" })));
        }
    }

    private sealed record SampleContract(string Value);

    private sealed class RegistrationDependency
    {
        public string Value => "dependency-ready";
    }

    private sealed class RegistrationService(RegistrationDependency dependency)
    {
        public string Value => dependency.Value;
    }

    private sealed class ToolDependency
    {
        public string Value => "registration-service";
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

    private sealed class RegistrationConversationResolver(RegistrationService service) : IConversationResolver
    {
        public ValueTask<ConversationSelection> ResolveAsync(
            ChatTurnInput input,
            ChatOperationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ConversationSelection(Guid.NewGuid(), service.Value.Length > 0));
    }

    private sealed class SimpleConversationResolver : IConversationResolver
    {
        public ValueTask<ConversationSelection> ResolveAsync(
            ChatTurnInput input,
            ChatOperationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ConversationSelection(Guid.NewGuid()));
    }

    private sealed class RegistrationConversationStore : IConversationStore
    {
        public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
            Guid conversationId,
            ChatOperationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ChatCompletionMessage>>([]);

        public ValueTask CommitExchangeAsync(
            ChatExchange exchange,
            ChatOperationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class RegistrationContributor(RegistrationService service) : IChatContextContributor
    {
        public ValueTask<ChatContextContribution> ContributeAsync(
            ChatContextRequest request,
            ChatOperationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ChatContextContribution(
                [new SystemPromptSegment("registration", service.Value)],
                [],
                []));
    }

    private sealed class BrokenContributor(MissingDependency dependency) : IChatContextContributor
    {
        private readonly MissingDependency _dependency = dependency;

        public ValueTask<ChatContextContribution> ContributeAsync(
            ChatContextRequest request,
            ChatOperationContext context,
            CancellationToken cancellationToken)
        {
            _ = _dependency;
            return ValueTask.FromResult(ChatContextContribution.Empty);
        }
    }
}
