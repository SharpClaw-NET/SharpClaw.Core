using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

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
        ConversationResolver = GetOptionalSingle<IConversationResolver>(services);
        ProfileResolver = GetOptionalSingle<IChatProfileResolver>(services);
        ConversationStore = GetOptionalSingle<IConversationStore>(services);
        ContextContributors = Array.AsReadOnly(services.GetServices<IChatContextContributor>().ToArray());

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

    private static T? GetOptionalSingle<T>(IServiceProvider services)
        where T : class
    {
        var values = services.GetServices<T>().ToArray();
        return values.Length switch
        {
            0 => null,
            1 => values[0],
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

        yield return $"service.chat.conversation|{ConversationResolver?.GetType().AssemblyQualifiedName}";
        yield return $"service.chat.profile|{ProfileResolver?.GetType().AssemblyQualifiedName}";
        yield return $"service.chat.store|{ConversationStore?.GetType().AssemblyQualifiedName}";
        foreach (var contributor in ContextContributors
                     .OrderBy(value => value.GetType().AssemblyQualifiedName, StringComparer.Ordinal))
        {
            yield return $"service.chat.context|{contributor.GetType().AssemblyQualifiedName}";
        }
    }
}
