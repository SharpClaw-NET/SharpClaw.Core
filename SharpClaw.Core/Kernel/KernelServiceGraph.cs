using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Kernel;

/// <summary>Contains the behavior services used by one compiled kernel.</summary>
public sealed class KernelServiceGraph
{
    internal KernelServiceGraph(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
        Contracts = Array.AsReadOnly(services.GetServices<ServiceContractBinding>().ToArray());
        Storage = Array.AsReadOnly(services.GetServices<ScopedStorageContractDescriptor>().ToArray());
        var serviceLookup = services.GetService<IServiceProviderIsService>();
        ConversationResolver = IsRegistered<IConversationResolver>(services, serviceLookup)
            ? new ScopedConversationResolver(services)
            : null;
        ProfileResolver = IsRegistered<IChatProfileResolver>(services, serviceLookup)
            ? new ScopedProfileResolver(services)
            : null;
        ConversationStore = IsRegistered<IConversationStore>(services, serviceLookup)
            ? new ScopedConversationStore(services)
            : null;
        ContextContributors = IsRegistered<IChatContextContributor>(services, serviceLookup)
            ? Array.AsReadOnly<IChatContextContributor>([new ScopedContextContributors(services)])
            : Array.Empty<IChatContextContributor>();

        if (ConversationResolver is not null && ConversationStore is null)
        {
            throw new KernelGraphCompilationException(
                "A conversation resolver requires one IConversationStore service.");
        }

        ValidateContracts(Contracts);
        ValidateStorage(Storage);
        HashRecords = Array.AsReadOnly(BuildHashRecords().ToArray());
    }

    public IReadOnlyList<ServiceContractBinding> Contracts { get; }

    public IReadOnlyList<ScopedStorageContractDescriptor> Storage { get; }

    public IConversationResolver? ConversationResolver { get; }

    public IChatProfileResolver? ProfileResolver { get; }

    public IConversationStore? ConversationStore { get; }

    public IReadOnlyList<IChatContextContributor> ContextContributors { get; }

    internal IServiceProvider Services { get; }

    internal IReadOnlyList<string> HashRecords { get; }

    private static bool IsRegistered<T>(
        IServiceProvider services,
        IServiceProviderIsService? serviceLookup) =>
        serviceLookup?.IsService(typeof(T)) ?? services.GetService<T>() is not null;

    private static T GetRequiredSingle<T>(IServiceProvider services)
        where T : class
    {
        var values = services.GetServices<T>().ToArray();
        return values.Length switch
        {
            1 => values[0],
            0 => throw new KernelGraphCompilationException(
                $"The service graph does not contain a {typeof(T).FullName} service."),
            _ => throw new KernelGraphCompilationException(
                $"The service graph contains more than one {typeof(T).FullName} service."),
        };
    }

    private static void ValidateContracts(IReadOnlyList<ServiceContractBinding> contracts)
    {
        var exports = contracts.Where(value => value.IsExport).ToArray();
        var duplicate = exports
            .GroupBy(value => (value.ContractName, value.SchemaVersion))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new KernelGraphCompilationException(
                $"Contract '{duplicate.Key.ContractName}' version {duplicate.Key.SchemaVersion} has more than one provider.");
        }

        foreach (var contract in contracts)
        {
            if (string.IsNullOrWhiteSpace(contract.SourceId)
                || string.IsNullOrWhiteSpace(contract.ContractName)
                || contract.SchemaVersion < 1)
            {
                throw new KernelGraphCompilationException("A service contract binding is invalid.");
            }

            if (contract.IsExport && contract.MaxBytes < 1)
            {
                throw new KernelGraphCompilationException(
                    $"Contract provider '{contract.ContractName}' must have a positive byte limit.");
            }

            if (contract.IsExport || contract.Optional)
                continue;

            var match = exports.Any(export =>
                export.ContractName == contract.ContractName
                && export.ServiceType == contract.ServiceType
                && export.SchemaVersion >= contract.SchemaVersion);
            if (!match)
            {
                throw new KernelGraphCompilationException(
                    $"Source '{contract.SourceId}' requires contract '{contract.ContractName}' version " +
                    $"{contract.SchemaVersion}, but no compatible service exists.");
            }
        }
    }

    private static void ValidateStorage(IReadOnlyList<ScopedStorageContractDescriptor> storage)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in storage)
        {
            if (string.IsNullOrWhiteSpace(value.SourceId)
                || string.IsNullOrWhiteSpace(value.StorageName)
                || value.MaxDocumentBytes < 1
                || value.MaxBatchSize < 1
                || value.Operations.Count == 0
                || value.Operations.Any(operation => string.IsNullOrWhiteSpace(operation.Name)))
            {
                throw new KernelGraphCompilationException(
                    $"Storage '{value.SourceId}/{value.StorageName}' has an invalid contract.");
            }

            if (!names.Add($"{value.SourceId}\0{value.StorageName}"))
            {
                throw new KernelGraphCompilationException(
                    $"Storage '{value.SourceId}/{value.StorageName}' is configured more than once.");
            }

            if (value.Operations.Select(operation => operation.Name).Distinct(StringComparer.Ordinal).Count()
                != value.Operations.Count)
            {
                throw new KernelGraphCompilationException(
                    $"Storage '{value.SourceId}/{value.StorageName}' has duplicate operations.");
            }
        }
    }

    private IEnumerable<string> BuildHashRecords()
    {
        foreach (var contract in Contracts
                     .OrderBy(value => value.SourceId, StringComparer.Ordinal)
                     .ThenBy(value => value.ContractName, StringComparer.Ordinal)
                     .ThenBy(value => value.SchemaVersion))
        {
            yield return $"service.contract|{contract.SourceId}|{contract.ContractName}|" +
                         $"{contract.SchemaVersion}|{contract.ServiceType.AssemblyQualifiedName}|" +
                         $"{contract.IsExport}|{contract.Optional}|{contract.MaxBytes}";
        }

        foreach (var value in Storage
                     .OrderBy(item => item.SourceId, StringComparer.Ordinal)
                     .ThenBy(item => item.StorageName, StringComparer.Ordinal))
        {
            yield return $"service.storage|{value.SourceId}|{value.StorageName}|" +
                         string.Join(';', KernelGraphHasher.Flatten("value", value));
        }

        yield return $"service.chat.conversation|{ConversationResolver is not null}";
        yield return $"service.chat.profile|{ProfileResolver is not null}";
        yield return $"service.chat.store|{ConversationStore is not null}";
        yield return $"service.chat.context|{ContextContributors.Count > 0}";
    }

    private sealed class ScopedConversationResolver(IServiceProvider rootServices)
        : IConversationResolver
    {
        public ValueTask<ConversationSelection> ResolveAsync(
            ChatTurnInput input,
            ChatOperationContext context,
            CancellationToken ct) =>
            KernelExecutionScope.RunAsync(
                rootServices,
                services => GetRequiredSingle<IConversationResolver>(services)
                    .ResolveAsync(input, context, ct));
    }

    private sealed class ScopedProfileResolver(IServiceProvider rootServices)
        : IChatProfileResolver
    {
        public ValueTask<ChatProfile> ResolveAsync(
            ChatTurnContext turn,
            ChatOperationContext context,
            CancellationToken ct) =>
            KernelExecutionScope.RunAsync(
                rootServices,
                services => GetRequiredSingle<IChatProfileResolver>(services)
                    .ResolveAsync(turn, context, ct));
    }

    private sealed class ScopedConversationStore(IServiceProvider rootServices)
        : IConversationStore
    {
        public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
            Guid conversationId,
            ChatOperationContext context,
            CancellationToken ct) =>
            KernelExecutionScope.RunAsync(
                rootServices,
                services => GetRequiredSingle<IConversationStore>(services)
                    .LoadHistoryAsync(conversationId, context, ct));

        public async ValueTask CommitExchangeAsync(
            ChatExchange exchange,
            ChatOperationContext context,
            CancellationToken ct)
        {
            await KernelExecutionScope.RunAsync(
                rootServices,
                async services =>
                {
                    await GetRequiredSingle<IConversationStore>(services)
                        .CommitExchangeAsync(exchange, context, ct);
                    return true;
                });
        }
    }

    private sealed class ScopedContextContributors(IServiceProvider rootServices)
        : IChatContextContributor
    {
        public ValueTask<ChatContextContribution> ContributeAsync(
            ChatContextRequest request,
            ChatOperationContext context,
            CancellationToken ct) =>
            KernelExecutionScope.RunAsync(
                rootServices,
                async services =>
                {
                    var prompt = new List<SystemPromptSegment>();
                    var messages = new List<ChatCompletionMessage>();
                    var features = new List<ExtensionFeature>();
                    foreach (var contributor in services.GetServices<IChatContextContributor>())
                    {
                        var contribution = await contributor.ContributeAsync(request, context, ct);
                        prompt.AddRange(contribution.SystemPromptSegments);
                        messages.AddRange(contribution.Messages);
                        features.AddRange(contribution.Features);
                    }

                    return new ChatContextContribution(prompt, messages, features);
                });
    }
}
