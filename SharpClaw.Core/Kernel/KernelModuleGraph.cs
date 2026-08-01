using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

internal sealed record KernelModuleDeclaration(
    ModuleIdentity Identity,
    IReadOnlyList<ServiceDescriptor> Services,
    IReadOnlyList<KernelModuleContractDeclaration> Contracts,
    IReadOnlyList<ModuleStorageContractDescriptor> Storage,
    Type? ConversationResolver,
    ExclusiveRegistration? ConversationResolverRegistration,
    Type? ProfileResolver,
    ExclusiveRegistration? ProfileResolverRegistration,
    IReadOnlyList<Type> ContextContributors);

public sealed record KernelCompiledModule(
    ModuleIdentity Identity,
    IReadOnlyList<Type> ServiceTypes,
    IReadOnlyList<KernelModuleContractDeclaration> Contracts,
    IReadOnlyList<ModuleStorageContractDescriptor> Storage,
    Type? ConversationResolver,
    ExclusiveRegistration? ConversationResolverRegistration,
    Type? ProfileResolver,
    ExclusiveRegistration? ProfileResolverRegistration,
    IReadOnlyList<Type> ContextContributors);

public sealed class KernelModuleGraph
{
    internal KernelModuleGraph(
        IReadOnlyList<KernelCompiledModule> modules,
        IServiceProvider services,
        IReadOnlyList<string> hashRecords)
    {
        Modules = modules;
        Services = services;
        HashRecords = hashRecords;
        Contracts = modules.SelectMany(module => module.Contracts).ToArray();
        Storage = modules.SelectMany(module => module.Storage).ToArray();
        ContextContributors = modules.SelectMany(module => module.ContextContributors).ToArray();
        ConversationResolver = modules.Select(module => module.ConversationResolver).SingleOrDefault(type => type is not null);
        ProfileResolver = modules.Select(module => module.ProfileResolver).SingleOrDefault(type => type is not null);
    }

    public IReadOnlyList<KernelCompiledModule> Modules { get; }

    public IReadOnlyList<KernelModuleContractDeclaration> Contracts { get; }

    public IReadOnlyList<ModuleStorageContractDescriptor> Storage { get; }

    public IReadOnlyList<Type> ContextContributors { get; }

    public Type? ConversationResolver { get; }

    public Type? ProfileResolver { get; }

    internal IServiceProvider Services { get; }

    internal IReadOnlyList<string> HashRecords { get; }
}

internal static class KernelModuleGraphCompiler
{
    public static KernelModuleGraph Compile(
        IReadOnlyList<KernelModuleDeclaration> declarations,
        IServiceProvider? hostServices)
    {
        ValidateModuleIdentities(declarations);
        ValidateExclusiveSlots(declarations);
        ValidateContracts(declarations);
        ValidateStorage(declarations);

        var services = new List<ServiceDescriptor>();
        foreach (var declaration in declarations)
        {
            foreach (var service in declaration.Services)
            {
                if (service.IsKeyedService)
                {
                    throw new KernelGraphCompilationException(
                        $"Module '{declaration.Identity.Id}' uses a keyed service. " +
                        "The store-neutral kernel service graph does not support keyed services.");
                }
                services.Add(service);
            }

            AddConcreteService(services, declaration.ConversationResolver);
            AddConcreteService(services, declaration.ProfileResolver);
            foreach (var contributor in declaration.ContextContributors)
                AddConcreteService(services, contributor);
        }

        var provider = new KernelModuleServiceProvider(services, hostServices);
        ValidateResolvableDeclarations(declarations, provider);
        var modules = declarations
            .Select(declaration => new KernelCompiledModule(
                declaration.Identity,
                new ReadOnlyCollection<Type>(declaration.Services.Select(service => service.ServiceType).ToArray()),
                new ReadOnlyCollection<KernelModuleContractDeclaration>(declaration.Contracts.ToArray()),
                new ReadOnlyCollection<ModuleStorageContractDescriptor>(declaration.Storage.ToArray()),
                declaration.ConversationResolver,
                declaration.ConversationResolverRegistration,
                declaration.ProfileResolver,
                declaration.ProfileResolverRegistration,
                new ReadOnlyCollection<Type>(declaration.ContextContributors.ToArray())))
            .ToArray();
        return new KernelModuleGraph(
            new ReadOnlyCollection<KernelCompiledModule>(modules),
            provider,
            new ReadOnlyCollection<string>(BuildHashRecords(declarations).ToArray()));
    }

    private static void ValidateModuleIdentities(IReadOnlyList<KernelModuleDeclaration> declarations)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            if (string.IsNullOrWhiteSpace(declaration.Identity.Id) ||
                string.IsNullOrWhiteSpace(declaration.Identity.DisplayName) ||
                string.IsNullOrWhiteSpace(declaration.Identity.ToolPrefix))
                throw new KernelGraphCompilationException("A module identity contains an empty required value.");
            if (!ids.Add(declaration.Identity.Id))
                throw new KernelGraphCompilationException(
                    $"Module '{declaration.Identity.Id}' is registered more than once.");
        }
    }

    private static void ValidateExclusiveSlots(IReadOnlyList<KernelModuleDeclaration> declarations)
    {
        ValidateExclusiveSlot(
            "conversation resolver",
            declarations
                .Where(value => value.ConversationResolver is not null)
                .Select(value => (value.Identity.Id, value.ConversationResolver!, value.ConversationResolverRegistration)));
        ValidateExclusiveSlot(
            "chat profile resolver",
            declarations
                .Where(value => value.ProfileResolver is not null)
                .Select(value => (value.Identity.Id, value.ProfileResolver!, value.ProfileResolverRegistration)));
    }

    private static void ValidateExclusiveSlot(
        string slot,
        IEnumerable<(string ModuleId, Type Type, ExclusiveRegistration? Registration)> claims)
    {
        var values = claims.ToArray();
        foreach (var value in values)
        {
            if (value.Registration is null || string.IsNullOrWhiteSpace(value.Registration.Id))
                throw new KernelGraphCompilationException(
                    $"Module '{value.ModuleId}' registered a {slot} without an exclusive registration id.");
        }
        if (values.Length > 1)
        {
            throw new KernelGraphCompilationException(
                $"The {slot} has competing claims from modules " +
                $"'{string.Join("', '", values.Select(value => value.ModuleId))}'.");
        }
    }

    private static void ValidateContracts(IReadOnlyList<KernelModuleDeclaration> declarations)
    {
        var contracts = declarations.SelectMany(declaration => declaration.Contracts).ToArray();
        var exports = contracts.Where(contract => contract.Kind == KernelModuleContractKind.Export).ToArray();
        var duplicate = exports
            .GroupBy(contract => (contract.Name, contract.SchemaVersion), ContractKeyComparer.Instance)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new KernelGraphCompilationException(
                $"Contract '{duplicate.Key.Name}' version {duplicate.Key.SchemaVersion} has more than one exporter.");
        }

        foreach (var contract in contracts)
        {
            if (string.IsNullOrWhiteSpace(contract.Name) || contract.SchemaVersion < 1)
                throw new KernelGraphCompilationException("A module contract declaration is invalid.");
            if (contract.Kind == KernelModuleContractKind.Export && contract.MaxBytes < 1)
                throw new KernelGraphCompilationException(
                    $"Contract export '{contract.Name}' must have a positive byte limit.");
            if (contract.Kind != KernelModuleContractKind.Requirement || contract.Optional)
                continue;
            var match = exports.Any(export =>
                export.Name == contract.Name &&
                export.ContractType == contract.ContractType &&
                export.SchemaVersion >= contract.SchemaVersion);
            if (!match)
            {
                throw new KernelGraphCompilationException(
                    $"Module '{contract.OwnerModuleId}' requires contract '{contract.Name}' " +
                    $"version {contract.SchemaVersion}, but no compatible export exists.");
            }
        }
    }

    private static void ValidateStorage(IReadOnlyList<KernelModuleDeclaration> declarations)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            foreach (var storage in declaration.Storage)
            {
                if (!string.Equals(storage.ModuleId, declaration.Identity.Id, StringComparison.Ordinal))
                {
                    throw new KernelGraphCompilationException(
                        $"Storage '{storage.StorageName}' must use owner module id '{declaration.Identity.Id}'.");
                }
                if (string.IsNullOrWhiteSpace(storage.StorageName) ||
                    storage.MaxDocumentBytes < 1 || storage.MaxBatchSize < 1 ||
                    storage.Operations.Count == 0 || storage.Operations.Any(operation => string.IsNullOrWhiteSpace(operation.Name)))
                    throw new KernelGraphCompilationException(
                        $"Module '{declaration.Identity.Id}' has an invalid storage declaration.");
                if (!names.Add($"{storage.ModuleId}\0{storage.StorageName}"))
                    throw new KernelGraphCompilationException(
                        $"Storage '{storage.ModuleId}/{storage.StorageName}' is registered more than once.");
                if (storage.Operations.Select(operation => operation.Name).Distinct(StringComparer.Ordinal).Count() !=
                    storage.Operations.Count)
                    throw new KernelGraphCompilationException(
                        $"Storage '{storage.ModuleId}/{storage.StorageName}' has duplicate operations.");
            }
        }
    }

    private static void ValidateResolvableDeclarations(
        IReadOnlyList<KernelModuleDeclaration> declarations,
        KernelModuleServiceProvider services)
    {
        foreach (var declaration in declarations)
        {
            foreach (var service in declaration.Services)
            {
                if (service.ServiceType.ContainsGenericParameters)
                {
                    throw new KernelGraphCompilationException(
                        $"Module '{declaration.Identity.Id}' service '{service.ServiceType.FullName}' " +
                        "uses an open generic registration, which the kernel service graph does not support.");
                }
                try
                {
                    _ = services.Validate(service);
                }
                catch (Exception exception)
                {
                    throw new KernelGraphCompilationException(
                        $"Module '{declaration.Identity.Id}' service '{service.ServiceType.FullName}' " +
                        $"cannot be resolved: {exception.Message}");
                }
            }

            foreach (var type in declaration.ContextContributors
                         .Concat([declaration.ConversationResolver, declaration.ProfileResolver])
                         .Where(type => type is not null)
                         .Cast<Type>())
            {
                try
                {
                    _ = KernelServiceResolution.Resolve(type, services);
                }
                catch (Exception exception)
                {
                    throw new KernelGraphCompilationException(
                        $"Module '{declaration.Identity.Id}' service '{type.FullName}' cannot be resolved: " +
                        exception.Message);
                }
            }
        }
    }

    private static void AddConcreteService(ICollection<ServiceDescriptor> services, Type? serviceType)
    {
        if (serviceType is null || services.Any(descriptor => descriptor.ServiceType == serviceType))
            return;
        services.Add(ServiceDescriptor.Singleton(serviceType, serviceType));
    }

    private static IEnumerable<string> BuildHashRecords(IEnumerable<KernelModuleDeclaration> declarations)
    {
        foreach (var declaration in declarations.OrderBy(value => value.Identity.Id, StringComparer.Ordinal))
        {
            yield return $"module.identity|{declaration.Identity.Id}|{declaration.Identity.DisplayName}|" +
                         declaration.Identity.ToolPrefix;
            foreach (var service in declaration.Services
                         .OrderBy(ServiceSignature, StringComparer.Ordinal))
                yield return $"module.service|{declaration.Identity.Id}|{ServiceSignature(service)}";
            foreach (var contract in declaration.Contracts
                         .OrderBy(value => value.Name, StringComparer.Ordinal)
                         .ThenBy(value => value.SchemaVersion)
                         .ThenBy(value => value.Kind))
                yield return $"module.contract|{declaration.Identity.Id}|" +
                             KernelGraphHasher.Flatten("value", contract).JoinWith(";");
            foreach (var storage in declaration.Storage.OrderBy(value => value.StorageName, StringComparer.Ordinal))
                yield return $"module.storage|{declaration.Identity.Id}|" +
                             KernelGraphHasher.Flatten("value", storage).JoinWith(";");
            yield return $"module.chat.conversation|{declaration.Identity.Id}|" +
                         $"{declaration.ConversationResolver?.AssemblyQualifiedName}|" +
                         declaration.ConversationResolverRegistration?.Id;
            yield return $"module.chat.profile|{declaration.Identity.Id}|" +
                         $"{declaration.ProfileResolver?.AssemblyQualifiedName}|" +
                         declaration.ProfileResolverRegistration?.Id;
            foreach (var contributor in declaration.ContextContributors
                         .OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal))
                yield return $"module.chat.contributor|{declaration.Identity.Id}|{contributor.AssemblyQualifiedName}";
        }
    }

    private static string ServiceSignature(ServiceDescriptor descriptor)
    {
        var implementation = descriptor.ImplementationType?.AssemblyQualifiedName ??
                             descriptor.ImplementationInstance?.GetType().AssemblyQualifiedName ??
                             FactoryIdentity(descriptor.ImplementationFactory);
        return $"{descriptor.ServiceType.AssemblyQualifiedName}|{descriptor.Lifetime}|{implementation}";
    }

    private static string FactoryIdentity(Func<IServiceProvider, object>? factory)
    {
        if (factory is null)
            return "<none>";
        var method = factory.Method;
        string metadataToken;
        try
        {
            metadataToken = method.MetadataToken.ToString(CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            metadataToken = "dynamic";
        }
        return $"{method.DeclaringType?.AssemblyQualifiedName}|{method.Name}|" +
               $"{method.Module.ModuleVersionId:N}|{metadataToken}|{factory.Target?.GetType().AssemblyQualifiedName}";
    }

    private sealed class ContractKeyComparer : IEqualityComparer<(string Name, int SchemaVersion)>
    {
        public static ContractKeyComparer Instance { get; } = new();

        public bool Equals((string Name, int SchemaVersion) left, (string Name, int SchemaVersion) right) =>
            left.SchemaVersion == right.SchemaVersion &&
            string.Equals(left.Name, right.Name, StringComparison.Ordinal);

        public int GetHashCode((string Name, int SchemaVersion) value) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.Name), value.SchemaVersion);
    }
}

internal sealed class KernelModuleServiceProvider(
    IReadOnlyList<ServiceDescriptor> descriptors,
    IServiceProvider? hostServices) : IServiceProvider
{
    private readonly object _sync = new();
    private readonly Dictionary<ServiceDescriptor, object> _singletons = new();
    [ThreadStatic]
    private static HashSet<ServiceDescriptor>? _active;

    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceType == typeof(IServiceProvider))
            return this;
        if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            var itemType = serviceType.GetGenericArguments()[0];
            var matches = descriptors.Where(descriptor => descriptor.ServiceType == itemType).ToArray();
            var array = Array.CreateInstance(itemType, matches.Length);
            for (var index = 0; index < matches.Length; index++)
                array.SetValue(Resolve(matches[index]), index);
            return array;
        }

        var descriptor = descriptors.LastOrDefault(value => value.ServiceType == serviceType);
        return descriptor is not null ? Resolve(descriptor) : hostServices?.GetService(serviceType);
    }

    internal object Validate(ServiceDescriptor descriptor) => Resolve(descriptor);

    private object Resolve(ServiceDescriptor descriptor)
    {
        if (descriptor.Lifetime != ServiceLifetime.Singleton)
            return Create(descriptor);
        lock (_sync)
        {
            if (_singletons.TryGetValue(descriptor, out var existing))
                return existing;
            var created = Create(descriptor);
            _singletons.Add(descriptor, created);
            return created;
        }
    }

    private object Create(ServiceDescriptor descriptor)
    {
        _active ??= [];
        if (!_active.Add(descriptor))
            throw new KernelGraphCompilationException(
                $"Module service '{descriptor.ServiceType.FullName}' has a circular dependency.");
        try
        {
            return descriptor.ImplementationInstance ??
                   descriptor.ImplementationFactory?.Invoke(this) ??
                   (descriptor.ImplementationType is { } type
                       ? ActivatorUtilities.CreateInstance(this, type)
                       : throw new KernelGraphCompilationException(
                           $"Module service '{descriptor.ServiceType.FullName}' has no implementation."));
        }
        finally
        {
            _active.Remove(descriptor);
        }
    }
}
