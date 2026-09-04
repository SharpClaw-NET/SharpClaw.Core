using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

internal sealed record TestSourceIdentity(
    string Id,
    string DisplayName,
    string Prefix);

internal interface ITestServiceRegistration
{
    TestSourceIdentity Identity { get; }

    void Configure(IServiceCollection services);
}

internal static class KernelTestExecution
{
    private static readonly Type[] TestServiceTypes = typeof(KernelTestExecution).Assembly
        .GetTypes()
        .Where(type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters)
        .ToArray();

    public static void Add(
        this ServiceCollection services,
        ITestServiceRegistration source)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(source);
        source.Configure(services);
    }

    public static KernelGraph Compile(
        this ServiceCollection services,
        KernelGraphCompileOptions? options = null) =>
        CompileServices(services, options);

    public static KernelGraph Compile(
        this KernelGraphBuilder builder,
        KernelGraphCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = CreateTestServices();
        var provider = services.BuildServiceProvider();
        return builder.Compile(provider, options);
    }

    public static KernelGraph CompileForTest(
        this ServiceCollection services,
        KernelGraphCompileOptions? options = null)
        => CompileServices(services, options);

    public static KernelGraph CompileDeclaredServices(
        this ServiceCollection services,
        KernelGraphCompileOptions? options = null)
        => CompileServices(services, options);

    public static void AddAction<TAction, TResult>(
        this IServiceCollection services,
        string sourceId,
        ActionDescriptor<TAction, TResult> descriptor) =>
        services.AddSingleton<IActionDefinitionBinding>(
            new ActionDefinitionBinding<TAction, TResult>(sourceId, descriptor));

    public static void AddEvent<TEvent>(
        this IServiceCollection services,
        string sourceId,
        EventDescriptor<TEvent> descriptor) =>
        services.AddSingleton<IEventDefinitionBinding>(
            new EventDefinitionBinding<TEvent>(sourceId, descriptor));

    public static void AddActionHook<TInterceptor>(
        this IServiceCollection services,
        string sourceId,
        SharpClawActionKey key,
        HookOrdering ordering)
        where TInterceptor : class
    {
        services.AddSingleton<TInterceptor>();
        services.AddSingleton(new ActionHookBinding(
            sourceId,
            BehaviorTargetKind.Exact,
            key,
            null,
            typeof(TInterceptor),
            IsUntyped: typeof(IAnyActionInterceptor).IsAssignableFrom(typeof(TInterceptor)),
            ordering,
            typeof(TInterceptor).AssemblyQualifiedName!));
    }

    public static void AddEventListener<TListener>(
        this IServiceCollection services,
        string sourceId,
        SharpClawEventKey key,
        EventDelivery delivery,
        HookOrdering ordering)
        where TListener : class
    {
        services.AddSingleton<TListener>();
        services.AddSingleton(new EventHookBinding(
            sourceId,
            BehaviorTargetKind.Exact,
            key,
            null,
            typeof(TListener),
            IsUntyped: typeof(IAnyEventListener).IsAssignableFrom(typeof(TListener)),
            EventHookKind.Listener,
            delivery,
            ordering,
            typeof(TListener).AssemblyQualifiedName!));
    }

    public static void AddContractExport<TService>(
        this IServiceCollection services,
        string sourceId,
        string contractName,
        int schemaVersion,
        int maxBytes) =>
        services.AddSingleton(new ServiceContractBinding(
            sourceId,
            typeof(TService),
            contractName,
            schemaVersion,
            maxBytes,
            IsExport: true,
            Optional: false));

    public static void AddContractRequirement<TService>(
        this IServiceCollection services,
        string sourceId,
        string contractName,
        int minimumSchemaVersion,
        bool optional) =>
        services.AddSingleton(new ServiceContractBinding(
            sourceId,
            typeof(TService),
            contractName,
            minimumSchemaVersion,
            MaxBytes: 0,
            IsExport: false,
            optional));

    public static void AddStorage(
        this IServiceCollection services,
        ScopedStorageContractDescriptor descriptor) =>
        services.AddSingleton(descriptor);

    public static void AddConversationResolver<TResolver>(this IServiceCollection services)
        where TResolver : class, IConversationResolver
    {
        services.AddSingleton<TResolver>();
        services.AddSingleton<IConversationResolver>(
            provider => provider.GetRequiredService<TResolver>());
    }

    public static void AddContextContributor<TContributor>(this IServiceCollection services)
        where TContributor : class, IChatContextContributor
    {
        services.AddSingleton<TContributor>();
        services.AddSingleton<IChatContextContributor>(
            provider => provider.GetRequiredService<TContributor>());
    }

    public static void AddTool<THandler>(
        this IServiceCollection services,
        string sourceId,
        ToolDescriptor descriptor)
        where THandler : class, IToolHandler
    {
        services.AddSingleton<THandler>();
        services.AddSingleton(new ToolHandlerBinding(
            sourceId,
            descriptor,
            typeof(THandler),
            typeof(THandler).AssemblyQualifiedName!));
    }

    private static KernelGraph CompileServices(
        ServiceCollection services,
        KernelGraphCompileOptions? options)
    {
        ArgumentNullException.ThrowIfNull(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        return new KernelGraphBuilder().Compile(provider, options);
    }

    private static ServiceCollection CreateTestServices()
    {
        var services = new ServiceCollection();
        foreach (var type in TestServiceTypes)
            services.AddSingleton(type, type);
        return services;
    }

    public static ToolInvocation CreateToolInvocation(
        string toolName,
        JsonElement? arguments = null,
        Guid? invocationId = null)
    {
        var id = invocationId ?? Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var value = arguments ?? JsonSerializer.SerializeToElement(new { });
        return new ToolInvocation(
            id,
            conversationId,
            "call",
            toolName,
            value,
            CreateToolContext(id, toolName, value, conversationId: conversationId));
    }

    public static HostActionEntryRequestContext CreateToolContext(
        Guid invocationId,
        string toolName,
        JsonElement arguments,
        ActionContext<KernelActionEnvelope>? parent = null,
        Guid? conversationId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.SerializeToUtf8Bytes(arguments);
        return new HostActionEntryRequestContext(
            Guid.NewGuid(),
            "test-capability",
            HostActionEntryIngress.Tool,
            invocationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            parent?.Caller ?? RequestPrincipal.Anonymous,
            parent?.Features ?? ExtensionFeatureSet.Empty,
            parent?.TraceId ?? Guid.NewGuid(),
            parent?.IdempotencyKey ?? Guid.NewGuid(),
            parent?.Deadline ?? now.AddMinutes(1),
            parent?.Deadline ?? now.AddMinutes(1))
        {
            Contribution = new HostActionEntryContribution(
                new HostActionEntryIngressBinding(
                    HostActionEntryIngress.Tool,
                    toolName,
                    conversationId?.ToString("D")),
                new HostActionEntryLineage(
                    SharpClawActions.Tools.Invoke,
                    1,
                    "test-descriptor",
                    "SharpClaw.Contracts.Kernel.ToolInvocation",
                    1,
                    "test-schema",
                    Convert.ToHexString(SHA256.HashData(payload)),
                    payload.Length)),
            ParentInvocationId = parent?.InvocationId,
            Depth = parent is null ? 0 : parent.Depth + 1,
            Attempt = parent?.Attempt > 0 ? parent.Attempt : 1
        };
    }

    public static KernelActionExecutionContext CreateContext() =>
        new(
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());

    public static IKernelToolContextIssuer CreateToolContextIssuer() =>
        new TestToolContextIssuer();

    public static KernelActionDispatcher CreateDispatcher(
        KernelGraph graph,
        IActionContinuationHost? continuationHost = null,
        ICommittedEventWriter? eventWriter = null,
        IKernelActionResultSnapshotter? resultSnapshotter = null,
        IKernelActionRepeatEvidenceAuthority? repeatEvidenceAuthority = null,
        KernelExternalAuthoritySessionRegistry? externalAuthorityRegistry = null) =>
        new(
            graph,
            CreateContext(),
            continuationHost,
            eventWriter,
            resultSnapshotter,
            repeatEvidenceAuthority,
            externalAuthorityRegistry);
}

internal sealed class TestToolContextIssuer : IKernelToolContextIssuer
{
    public List<KernelToolContextIssueRequest> Requests { get; } = [];

    public ValueTask<HostActionEntryRequestContext?> IssueAsync(
        KernelToolContextIssueRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return ValueTask.FromResult<HostActionEntryRequestContext?>(
            KernelTestExecution.CreateToolContext(
                request.InvocationId,
                request.ToolName,
                request.Arguments,
                request.ParentActionContext,
                request.ConversationId));
    }
}

internal sealed class MatchingRepeatEvidenceAuthority : IKernelActionRepeatEvidenceAuthority
{
    public ValueTask<KernelActionRepeatEvidence?> AuthorizeAsync(
        KernelActionRepeatEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<KernelActionRepeatEvidence?>(new(
            Guid.NewGuid().ToString("N"),
            request.RequiredKind,
            request.ActionKey,
            request.ActionVersion,
            request.IdempotencyScope,
            request.IdempotencyKey,
            request.PriorInvocationId,
            request.PriorAttempt,
            request.NextInvocationId,
            request.NextAttempt,
            request.RequestedAt,
            request.RequestedAt.AddMinutes(1)));
    }
}
