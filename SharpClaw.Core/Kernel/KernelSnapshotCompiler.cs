using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

public sealed class KernelGraphBuilder
{
    private readonly List<IActionDefinitionRegistration> _actions = [];
    private readonly List<IEventDefinitionRegistration> _events = [];
    private readonly List<KernelActionHookRegistration> _actionHooks = [];
    private readonly List<KernelEventHookRegistration> _eventHooks = [];
    private readonly List<KernelToolRegistration> _tools = [];
    private readonly List<KernelModuleDeclaration> _modules = [];

    public KernelGraphBuilder(bool includeStandardDefinitions = true)
        : this(includeStandardDefinitions, includeLifecycleDefinitions: true)
    {
    }

    internal KernelGraphBuilder(
        bool includeStandardDefinitions,
        bool includeLifecycleDefinitions)
    {
        if (includeLifecycleDefinitions)
            AddLifecycleDefinitions();
        if (includeStandardDefinitions)
            AddStandardDefinitions();
    }

    public IActionDefinitionBuilder Actions => new KernelActionDefinitionBuilder(this, "core");

    public IEventDefinitionBuilder Events => new KernelEventDefinitionBuilder(this, "core");

    public IActionHookBuilder Hooks => new KernelActionHookBuilder(this, "core");

    public void Add<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        string ownerModuleId = "core") =>
        _actions.Add(new ActionDefinitionRegistration<TAction, TResult>(descriptor, ownerModuleId));

    public void AddEvent<TEvent>(
        EventDescriptor<TEvent> descriptor,
        string ownerModuleId = "core") =>
        _events.Add(new EventDefinitionRegistration<TEvent>(descriptor, ownerModuleId));

    public void AddTool<THandler>(ToolDescriptor descriptor, string ownerModuleId = "core") =>
        _tools.Add(new KernelToolRegistration(descriptor, ownerModuleId, typeof(THandler)));

    public KernelGraph Compile(
        IServiceProvider? serviceProvider = null,
        KernelGraphCompileOptions? options = null) =>
        new KernelSnapshotCompiler().Compile(this, serviceProvider, options);

    internal IReadOnlyList<IActionDefinitionRegistration> ActionDefinitions => _actions;

    internal IReadOnlyList<IEventDefinitionRegistration> EventDefinitions => _events;

    internal IReadOnlyList<KernelActionHookRegistration> ActionHooks => _actionHooks;

    internal IReadOnlyList<KernelEventHookRegistration> EventHooks => _eventHooks;

    internal IReadOnlyList<KernelToolRegistration> Tools => _tools;

    internal IReadOnlyList<KernelModuleDeclaration> Modules => _modules;

    internal void AddActionHook(KernelActionHookRegistration registration) => _actionHooks.Add(registration);

    internal void AddEventHook(KernelEventHookRegistration registration) => _eventHooks.Add(registration);

    internal void AddModule(KernelModuleDeclaration declaration) => _modules.Add(declaration);

    internal void Import(KernelGraphBuilder source)
    {
        _actions.AddRange(source._actions);
        _events.AddRange(source._events);
        _actionHooks.AddRange(source._actionHooks);
        _eventHooks.AddRange(source._eventHooks);
        _tools.AddRange(source._tools);
    }

    private void AddStandardDefinitions()
    {
        foreach (var manifest in KernelActionCatalog.Descriptors)
            Add(manifest.ToDescriptor(), KernelCapabilities.CoreOwner);
    }

    private void AddLifecycleDefinitions()
    {
        foreach (var descriptor in KernelActionLifecycleEvents.Descriptors)
            AddEvent(descriptor, KernelCapabilities.CoreOwner);
    }
}

public sealed class KernelSnapshotCompiler
{
    public KernelGraph Compile(
        KernelGraphBuilder builder,
        IServiceProvider? serviceProvider = null,
        KernelGraphCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        options ??= new KernelGraphCompileOptions();
        if (options.MaximumActionDepth < 1)
            throw new KernelGraphCompilationException("Maximum action depth must be positive.");

        var modules = KernelModuleGraphCompiler.Compile(builder.Modules, serviceProvider);
        var actions = CompileActions(builder, modules.Services, options);
        var events = CompileEvents(builder, modules.Services, options);
        var tools = CompileTools(builder, modules.Services);
        var actionGrants = actions.Values
            .Select(action => new ActionCapabilityGrant(
                action.Key,
                action.Version,
                action.SnapshotCapabilities,
                action.SnapshotSensitiveApproved,
                false))
            .ToArray();
        var eventGrants = events.Values
            .Select(eventDefinition => new EventCapabilityGrant(
                eventDefinition.Key,
                eventDefinition.Version,
                eventDefinition.SnapshotCapabilities,
                eventDefinition.SnapshotSensitiveApproved,
                false))
            .ToArray();
        var contractHash = ComputeContractHash(
            actions.Values,
            events.Values,
            tools,
            builder.ActionHooks,
            builder.EventHooks,
            modules,
            options);
        var snapshot = new ActionPipelineSnapshot(
            contractHash,
            actionGrants,
            eventGrants,
            options.MaximumActionDepth);
        var chatSnapshot = new ChatPipelineSnapshot(
            snapshot,
            tools.Select(tool => tool.Descriptor).ToArray(),
            ExtensionFeatureSet.Empty);

        return new KernelGraph(
            actions,
            events,
            tools,
            modules,
            snapshot,
            chatSnapshot,
            options.MaximumActionDepth);
    }

    private static Dictionary<string, ICompiledActionDefinition> CompileActions(
        KernelGraphBuilder builder,
        IServiceProvider? serviceProvider,
        KernelGraphCompileOptions options)
    {
        var result = new Dictionary<string, ICompiledActionDefinition>(StringComparer.Ordinal);
        foreach (var definition in builder.ActionDefinitions)
        {
            if (!result.TryAdd(definition.Key.Value, definition.Compile(
                    builder.ActionHooks,
                    serviceProvider,
                    options)))
            {
                throw new KernelGraphCompilationException(
                    $"Action key '{definition.Descriptor.Key.Value}' is registered more than once.");
            }
        }

        return result;
    }

    private static Dictionary<string, ICompiledEventDefinition> CompileEvents(
        KernelGraphBuilder builder,
        IServiceProvider? serviceProvider,
        KernelGraphCompileOptions options)
    {
        var result = new Dictionary<string, ICompiledEventDefinition>(StringComparer.Ordinal);
        foreach (var definition in builder.EventDefinitions)
        {
            if (!result.TryAdd(definition.Key.Value, definition.Compile(
                    builder.EventHooks,
                    serviceProvider,
                    options)))
            {
                throw new KernelGraphCompilationException(
                    $"Event key '{definition.Descriptor.Key.Value}' is registered more than once.");
            }
        }

        return result;
    }

    private static IReadOnlyList<KernelToolRegistration> CompileTools(
        KernelGraphBuilder builder,
        IServiceProvider serviceProvider)
    {
        var result = new List<KernelToolRegistration>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in builder.Tools)
        {
            if (!names.Add(tool.Descriptor.Name))
                throw new KernelGraphCompilationException($"Tool '{tool.Descriptor.Name}' is registered more than once.");
            if (!typeof(IToolHandler).IsAssignableFrom(tool.HandlerType))
                throw new KernelGraphCompilationException(
                    $"Tool handler '{tool.HandlerType.FullName}' does not implement IToolHandler.");
            try
            {
                if (KernelServiceResolution.Resolve(tool.HandlerType, serviceProvider) is not IToolHandler)
                    throw new KernelGraphCompilationException(
                        $"Tool handler '{tool.HandlerType.FullName}' cannot be resolved as IToolHandler.");
            }
            catch (KernelGraphCompilationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new KernelGraphCompilationException(
                    $"Module '{tool.OwnerModuleId}' tool handler '{tool.HandlerType.FullName}' " +
                    $"cannot be resolved: {exception.Message}");
            }
            result.Add(tool);
        }

        return new ReadOnlyCollection<KernelToolRegistration>(result);
    }

    private static string ComputeContractHash(
        IEnumerable<ICompiledActionDefinition> actions,
        IEnumerable<ICompiledEventDefinition> events,
        IEnumerable<KernelToolRegistration> tools,
        IEnumerable<KernelActionHookRegistration> actionHooks,
        IEnumerable<KernelEventHookRegistration> eventHooks,
        KernelModuleGraph modules,
        KernelGraphCompileOptions options)
    {
        var records = new List<string>
        {
            $"options|supported-action|{KernelGraphHasher.StableScalar((int)options.SupportedActionCapabilities)}",
            $"options|supported-event|{KernelGraphHasher.StableScalar((int)options.SupportedEventCapabilities)}",
            $"options|max-depth|{KernelGraphHasher.StableScalar(options.MaximumActionDepth)}"
        };
        foreach (var action in actions.OrderBy(action => action.Key.Value, StringComparer.Ordinal))
        {
            records.AddRange(KernelGraphHasher.Flatten("action.descriptor", action.DescriptorObject));
            records.Add($"action.compiled|{action.Key.Value}|{KernelGraphHasher.StableScalar(action.Version)}|" +
                        $"{action.Category}|{KernelGraphHasher.StableScalar((int)action.Capabilities)}|" +
                        $"{KernelGraphHasher.StableScalar((int)action.EffectiveCapabilities)}|" +
                        $"{KernelGraphHasher.StableScalar(action.ContainsSensitiveData)}|" +
                        $"{KernelGraphHasher.StableScalar(action.SensitiveApproved)}|{action.OwnerModuleId}|" +
                        $"{action.ActionType.AssemblyQualifiedName}|{action.ResultType.AssemblyQualifiedName}");
            records.AddRange(KernelGraphHasher.Flatten("action.input-schema", action.InputSchema));
            records.AddRange(KernelGraphHasher.Flatten("action.result-schema", action.ResultSchema));
            if (SharpClawActionCatalog.Kernel.Contains(action.Key))
            {
                var contract = KernelActionCatalog.DescriptorFor(action.Key);
                records.Add(
                    $"action.payload-contract|{action.Key.Value}|" +
                    $"{contract.InputPayloadType.AssemblyQualifiedName}|" +
                    contract.ResultPayloadType.AssemblyQualifiedName);
            }
            records.Add($"action.signature|{action.Signature}");
        }
        foreach (var eventDefinition in events.OrderBy(eventDefinition => eventDefinition.Key.Value, StringComparer.Ordinal))
        {
            records.AddRange(KernelGraphHasher.Flatten("event.descriptor", eventDefinition.Descriptor));
            records.Add($"event.compiled|{eventDefinition.Key.Value}|" +
                        $"{KernelGraphHasher.StableScalar(eventDefinition.Version)}|" +
                        $"{eventDefinition.Category}|" +
                        $"{KernelGraphHasher.StableScalar((int)eventDefinition.Capabilities)}|" +
                        $"{KernelGraphHasher.StableScalar((int)eventDefinition.EffectiveCapabilities)}|" +
                        $"{KernelGraphHasher.StableScalar(eventDefinition.ContainsSensitiveData)}|" +
                        $"{KernelGraphHasher.StableScalar(eventDefinition.SensitiveApproved)}|" +
                        $"{eventDefinition.OwnerModuleId}|" +
                        $"{eventDefinition.EventType.AssemblyQualifiedName}");
            records.AddRange(KernelGraphHasher.Flatten("event.payload-schema", eventDefinition.PayloadSchema));
            records.Add($"event.signature|{eventDefinition.Signature}");
        }
        foreach (var tool in tools.OrderBy(tool => tool.Descriptor.Name, StringComparer.Ordinal))
        {
            records.AddRange(KernelGraphHasher.Flatten("tool.descriptor", tool.Descriptor));
            records.Add($"tool.owner|{tool.OwnerModuleId}|{tool.HandlerType.AssemblyQualifiedName}");
        }
        foreach (var hook in actionHooks
                     .OrderBy(hook => hook.OwnerModuleId, StringComparer.Ordinal)
                     .ThenBy(hook => hook.TargetKind)
                     .ThenBy(hook => hook.Key?.Value, StringComparer.Ordinal)
                     .ThenBy(hook => hook.Category, StringComparer.Ordinal)
                     .ThenBy(hook => hook.Ordering.Id, StringComparer.Ordinal))
            records.AddRange(KernelGraphHasher.Flatten("action.registration", hook));
        foreach (var hook in eventHooks
                     .OrderBy(hook => hook.OwnerModuleId, StringComparer.Ordinal)
                     .ThenBy(hook => hook.TargetKind)
                     .ThenBy(hook => hook.Key?.Value, StringComparer.Ordinal)
                     .ThenBy(hook => hook.Category, StringComparer.Ordinal)
                     .ThenBy(hook => hook.Ordering.Id, StringComparer.Ordinal))
            records.AddRange(KernelGraphHasher.Flatten("event.registration", hook));
        records.AddRange(modules.HashRecords);
        AddDictionary(records, "action.grant", options.ActionCapabilityGrants);
        AddDictionary(records, "event.grant", options.EventCapabilityGrants);
        AddNestedDictionary(records, "action.module-grant", options.ActionModuleCapabilityGrants);
        AddNestedDictionary(records, "event.module-grant", options.EventModuleCapabilityGrants);
        AddApprovalBoundary(records, "sensitive.action", options.SensitiveActionApprovals);
        AddApprovalBoundary(records, "sensitive.event", options.SensitiveEventApprovals);
        foreach (var approval in (options.SensitiveActionApprovals ?? []).OrderBy(approval => approval.ModuleId, StringComparer.Ordinal)
                     .ThenBy(approval => approval.ActionKey.Value, StringComparer.Ordinal)
                     .ThenBy(approval => approval.ActionVersion)
                     .ThenBy(approval => approval.ActionType, StringComparer.Ordinal)
                     .ThenBy(approval => approval.ResultType, StringComparer.Ordinal)
                     .ThenBy(approval => approval.SchemaIdentity, StringComparer.Ordinal))
            records.AddRange(KernelGraphHasher.Flatten("sensitive.action", approval));
        foreach (var approval in (options.SensitiveEventApprovals ?? []).OrderBy(approval => approval.ModuleId, StringComparer.Ordinal)
                     .ThenBy(approval => approval.EventKey.Value, StringComparer.Ordinal)
                     .ThenBy(approval => approval.EventVersion)
                     .ThenBy(approval => approval.EventType, StringComparer.Ordinal)
                     .ThenBy(approval => approval.SchemaIdentity, StringComparer.Ordinal))
            records.AddRange(KernelGraphHasher.Flatten("sensitive.event", approval));

        var content = string.Join("\n", records);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static void AddApprovalBoundary<T>(
        ICollection<string> records,
        string prefix,
        IReadOnlyList<T>? values)
    {
        if (values is null)
        {
            records.Add($"{prefix}|<null>");
            return;
        }
        if (values.Count == 0)
            records.Add($"{prefix}|<empty>");
    }

    private static void AddDictionary<T>(
        ICollection<string> records,
        string prefix,
        IReadOnlyDictionary<string, T>? values)
    {
        if (values is null)
        {
            records.Add($"{prefix}|<null>");
            return;
        }
        if (values.Count == 0)
        {
            records.Add($"{prefix}|<empty>");
            return;
        }
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            records.Add($"{prefix}|{pair.Key}|{KernelGraphHasher.StableScalar(pair.Value)}");
    }

    private static void AddNestedDictionary<T>(
        ICollection<string> records,
        string prefix,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, T>>? values)
    {
        if (values is null)
        {
            records.Add($"{prefix}|<null>");
            return;
        }
        if (values.Count == 0)
        {
            records.Add($"{prefix}|<empty>");
            return;
        }
        foreach (var module in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (module.Value.Count == 0)
            {
                records.Add($"{prefix}|{module.Key}|<empty>");
                continue;
            }
            foreach (var grant in module.Value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                records.Add(
                    $"{prefix}|{module.Key}|{grant.Key}|{KernelGraphHasher.StableScalar(grant.Value)}");
        }
    }
}

internal static class KernelGraphHasher
{
    public static string JoinWith(this IEnumerable<string> values, string separator) =>
        string.Join(separator, values);

    public static IEnumerable<string> Flatten(string path, object? value)
    {
        if (value is null)
        {
            yield return $"{path}=<null>";
            yield break;
        }
        if (value is JsonElement json)
        {
            yield return $"{path}.json={json.GetRawText()}";
            yield break;
        }
        if (value is Type type)
        {
            yield return $"{path}.type={type.AssemblyQualifiedName}";
            yield break;
        }
        if (value is string or char or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Guid or DateTime or DateTimeOffset or TimeSpan or Enum)
        {
            yield return $"{path}={StableScalar(value)}";
            yield break;
        }
        if (value is IEnumerable sequence)
        {
            var items = sequence.Cast<object?>().ToArray();
            if (items.Length > 0 && items.All(IsKeyValuePair))
            {
                items = items
                    .OrderBy(item => ScalarKey(GetPropertyValue(item!, "Key")), StringComparer.Ordinal)
                    .ThenBy(item => ScalarKey(GetPropertyValue(item!, "Value")), StringComparer.Ordinal)
                    .ToArray();
            }

            for (var index = 0; index < items.Length; index++)
            {
                foreach (var itemRecord in Flatten($"{path}[{index}]", items[index]))
                    yield return itemRecord;
            }
            if (items.Length == 0)
                yield return $"{path}=<empty>";
            yield break;
        }
        yield return $"{path}.$type={value.GetType().AssemblyQualifiedName}";
        foreach (var property in value.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                     .Where(property => property.GetMethod is not null)
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            foreach (var propertyRecord in Flatten($"{path}.{property.Name}", property.GetValue(value)))
                yield return propertyRecord;
        }
    }

    private static bool IsKeyValuePair(object? value) =>
        value is not null &&
        value.GetType().IsGenericType &&
        value.GetType().GetGenericTypeDefinition() == typeof(KeyValuePair<,>);

    private static object? GetPropertyValue(object value, string name) =>
        value.GetType().GetProperty(name)?.GetValue(value);

    private static string ScalarKey(object? value) =>
        value is Type type
            ? type.AssemblyQualifiedName ?? type.FullName ?? type.Name
            : StableScalar(value);

    internal static string StableScalar(object? value) => value switch
    {
        null => "null:<null>",
        string text => $"string:{text}",
        char character => $"char:{((int)character).ToString(CultureInfo.InvariantCulture)}",
        bool boolean => boolean ? "bool:1" : "bool:0",
        byte number => $"uint8:{number.ToString(CultureInfo.InvariantCulture)}",
        sbyte number => $"int8:{number.ToString(CultureInfo.InvariantCulture)}",
        short number => $"int16:{number.ToString(CultureInfo.InvariantCulture)}",
        ushort number => $"uint16:{number.ToString(CultureInfo.InvariantCulture)}",
        int number => $"int32:{number.ToString(CultureInfo.InvariantCulture)}",
        uint number => $"uint32:{number.ToString(CultureInfo.InvariantCulture)}",
        long number => $"int64:{number.ToString(CultureInfo.InvariantCulture)}",
        ulong number => $"uint64:{number.ToString(CultureInfo.InvariantCulture)}",
        float number => $"float32:{number.ToString("R", CultureInfo.InvariantCulture)}",
        double number => $"float64:{number.ToString("R", CultureInfo.InvariantCulture)}",
        decimal number => $"decimal:{number.ToString("G29", CultureInfo.InvariantCulture)}",
        Guid guid => $"guid:{guid:N}",
        DateTime dateTime => $"datetime:{dateTime.Ticks.ToString(CultureInfo.InvariantCulture)}:{dateTime.Kind}",
        DateTimeOffset dateTimeOffset =>
            $"datetimeoffset:{dateTimeOffset.UtcTicks.ToString(CultureInfo.InvariantCulture)}:" +
            dateTimeOffset.Offset.Ticks.ToString(CultureInfo.InvariantCulture),
        TimeSpan duration => $"timespan:{duration.Ticks.ToString(CultureInfo.InvariantCulture)}",
        Enum enumeration =>
            $"enum:{enumeration.GetType().AssemblyQualifiedName}:" +
            Convert.ToUInt64(enumeration, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        Type type => $"type:{type.AssemblyQualifiedName}",
        _ => $"{value.GetType().AssemblyQualifiedName}:" +
             (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
    };
}

public sealed class KernelGraph
{
    private readonly IReadOnlyDictionary<string, ICompiledActionDefinition> _actions;
    private readonly IReadOnlyDictionary<string, ICompiledEventDefinition> _events;

    internal KernelGraph(
        IReadOnlyDictionary<string, ICompiledActionDefinition> actions,
        IReadOnlyDictionary<string, ICompiledEventDefinition> events,
        IReadOnlyList<KernelToolRegistration> tools,
        KernelModuleGraph modules,
        ActionPipelineSnapshot actionSnapshot,
        ChatPipelineSnapshot chatSnapshot,
        int maximumActionDepth)
    {
        _actions = new ReadOnlyDictionary<string, ICompiledActionDefinition>(
            new Dictionary<string, ICompiledActionDefinition>(actions, StringComparer.Ordinal));
        _events = new ReadOnlyDictionary<string, ICompiledEventDefinition>(
            new Dictionary<string, ICompiledEventDefinition>(events, StringComparer.Ordinal));
        Tools = tools;
        Modules = modules;
        ActionSnapshot = actionSnapshot;
        ChatSnapshot = chatSnapshot;
        MaximumActionDepth = maximumActionDepth;
    }

    public ActionPipelineSnapshot ActionSnapshot { get; }

    public ChatPipelineSnapshot ChatSnapshot { get; }

    public IReadOnlyList<KernelToolRegistration> Tools { get; }

    public KernelModuleGraph Modules { get; }

    public int MaximumActionDepth { get; }

    public object? GetService(Type serviceType) => Modules.Services.GetService(serviceType);

    public TService GetRequiredService<TService>() where TService : notnull =>
        (TService)(GetService(typeof(TService)) ?? throw new KernelGraphCompilationException(
            $"Kernel module service '{typeof(TService).FullName}' is not registered."));

    public KernelChatContextAssembler CreateChatContextAssembler(KernelActionDispatcher dispatcher) =>
        new(this, dispatcher, Modules.ContextContributors.Select(type =>
            (IChatContextContributor)(GetService(type) ?? throw new KernelGraphCompilationException(
                $"Chat context contributor '{type.FullName}' is not registered."))));

    public bool ContainsAction(SharpClawActionKey key) => _actions.ContainsKey(key.Value);

    public bool ContainsEvent(SharpClawEventKey key) => _events.ContainsKey(key.Value);

    public ActionDescriptor<KernelActionEnvelope, object> GetStandardAction(SharpClawActionKey key) =>
        GetAction<KernelActionEnvelope, object>(key).Descriptor;

    internal CompiledActionDefinition<TAction, TResult> GetAction<TAction, TResult>(
        SharpClawActionKey key)
    {
        if (!_actions.TryGetValue(key.Value, out var definition))
            throw new KernelActionExecutionException($"Action '{key.Value}' is not registered in the compiled graph.");
        if (definition is not CompiledActionDefinition<TAction, TResult> typed)
        {
            throw new KernelActionExecutionException(
                $"Action '{key.Value}' was compiled for '{definition.ActionType.FullName}' and '{definition.ResultType.FullName}'.");
        }

        return typed;
    }

    internal ICompiledEventDefinition GetEvent(SharpClawEventKey key)
    {
        if (!_events.TryGetValue(key.Value, out var definition))
            throw new KernelActionExecutionException($"Event '{key.Value}' is not registered in the compiled graph.");
        return definition;
    }
}

internal static class KernelCapabilities
{
    public const string CoreOwner = "core";

    public static readonly ActionInterceptionCapabilities AllActions =
        ActionInterceptionCapabilities.Inspect |
        ActionInterceptionCapabilities.ReplaceInput |
        ActionInterceptionCapabilities.Cancel |
        ActionInterceptionCapabilities.ReplaceResult |
        ActionInterceptionCapabilities.Defer |
        ActionInterceptionCapabilities.Repeat |
        ActionInterceptionCapabilities.Wrap |
        ActionInterceptionCapabilities.Observe |
        ActionInterceptionCapabilities.PublishEvents;

    public static readonly ActionInterceptionCapabilities ObservableActions =
        ActionInterceptionCapabilities.Inspect |
        ActionInterceptionCapabilities.Wrap |
        ActionInterceptionCapabilities.Observe |
        ActionInterceptionCapabilities.PublishEvents;

    public static readonly ActionRepeatPolicy NoRepeat =
        new(ActionRepeatKind.None, 1, TimeSpan.Zero, "invocation");

    public static readonly ActionContinuationPolicy DurableContinuation =
        new(TimeSpan.FromHours(1), true, true);
}

public enum KernelHookTargetKind
{
    Exact,
    Category,
    Any
}

internal sealed record KernelActionHookRegistration(
    KernelHookTargetKind TargetKind,
    SharpClawActionKey? Key,
    string? Category,
    Type HandlerType,
    bool IsUntyped,
    HookOrdering Ordering,
    string OwnerModuleId);

internal enum KernelEventHookKind
{
    Interceptor,
    Listener
}

internal sealed record KernelEventHookRegistration(
    KernelHookTargetKind TargetKind,
    SharpClawEventKey? Key,
    string? Category,
    Type HandlerType,
    bool IsUntyped,
    KernelEventHookKind Kind,
    EventDelivery Delivery,
    HookOrdering Ordering,
    string OwnerModuleId);

internal interface IActionDefinitionRegistration
{
    object DescriptorObject { get; }

    dynamic Descriptor { get; }

    SharpClawActionKey Key { get; }

    ICompiledActionDefinition Compile(
        IReadOnlyList<KernelActionHookRegistration> hooks,
        IServiceProvider? serviceProvider,
        KernelGraphCompileOptions options);
}

internal sealed class ActionDefinitionRegistration<TAction, TResult>(
    ActionDescriptor<TAction, TResult> descriptor,
    string ownerModuleId) : IActionDefinitionRegistration
{
    public object DescriptorObject => descriptor;

    public dynamic Descriptor => descriptor;

    public SharpClawActionKey Key => descriptor.Key;

    public ICompiledActionDefinition Compile(
        IReadOnlyList<KernelActionHookRegistration> hooks,
        IServiceProvider? serviceProvider,
        KernelGraphCompileOptions options)
    {
        var unsupported = descriptor.Capabilities & ~options.SupportedActionCapabilities;
        if (unsupported != 0)
        {
            throw new KernelGraphCompilationException(
                $"Action '{descriptor.Key.Value}' requires unsupported capabilities: {unsupported}.");
        }
        var effectiveCapabilities = descriptor.Capabilities & options.SupportedActionCapabilities;
        if (options.ActionCapabilityGrants?.TryGetValue(descriptor.Key.Value, out var grant) == true)
            effectiveCapabilities &= grant;
        effectiveCapabilities &= ResolveActionModuleGrant(
            descriptor,
            ownerModuleId,
            options);

        var matching = hooks
            .Where(hook => Matches(hook.TargetKind, hook.Key, hook.Category, descriptor.Key, descriptor.Category))
            .ToArray();
        var ordered = KernelHookOrdering.Order(matching);
        var frames = new List<IActionFrame<TAction, TResult>>();
        foreach (var hook in ordered)
        {
            var hookCapabilities = ResolveActionCapabilities(descriptor, hook, options);
            if (!hookCapabilities.HasFlag(ActionInterceptionCapabilities.Inspect))
            {
                throw new KernelGraphCompilationException(
                    $"Module '{hook.OwnerModuleId}' cannot receive action '{descriptor.Key.Value}' " +
                    "without the Inspect capability.");
            }
            var hookSensitiveApproved = ResolveSensitiveApproval(
                descriptor,
                hook.OwnerModuleId,
                options,
                typeof(TAction),
                typeof(TResult));
            if (hook.IsUntyped)
            {
                if (!typeof(IAnyActionInterceptor).IsAssignableFrom(hook.HandlerType))
                    throw new KernelGraphCompilationException(
                        $"'{hook.HandlerType.FullName}' does not implement IAnyActionInterceptor.");
                frames.Add(new AnyActionFrame<TAction, TResult>(
                    (IAnyActionInterceptor)KernelServiceResolution.Resolve(hook.HandlerType, serviceProvider),
                    hook.TargetKind,
                    hook.Key,
                    hook.Category,
                    hook.Ordering,
                    hook.OwnerModuleId,
                    hookCapabilities,
                    hookSensitiveApproved));
            }
            else
            {
                var expected = typeof(IActionInterceptor<,>).MakeGenericType(typeof(TAction), typeof(TResult));
                if (!expected.IsAssignableFrom(hook.HandlerType))
                    throw new KernelGraphCompilationException(
                        $"'{hook.HandlerType.FullName}' does not implement '{expected.FullName}'.");
                frames.Add(new TypedActionFrame<TAction, TResult>(
                    (IActionInterceptor<TAction, TResult>)KernelServiceResolution.Resolve(
                        hook.HandlerType,
                        serviceProvider),
                    hook.TargetKind,
                    hook.Key,
                    hook.Category,
                    hook.Ordering,
                    hook.OwnerModuleId,
                    hookCapabilities,
                    hookSensitiveApproved));
            }
        }

        var sensitiveApproved = ResolveSensitiveApproval(
            descriptor,
            ownerModuleId,
            options,
            typeof(TAction),
            typeof(TResult));
        if (descriptor.ContainsSensitiveData && frames.Any(frame => !frame.SensitiveApproved))
            sensitiveApproved = false;
        if (descriptor.ContainsSensitiveData && !sensitiveApproved)
            throw new KernelGraphCompilationException($"Sensitive action '{descriptor.Key.Value}' lacks exact approval.");

        return new CompiledActionDefinition<TAction, TResult>(
            descriptor,
            ownerModuleId,
            frames,
            effectiveCapabilities,
            sensitiveApproved,
            KernelSchemaIdentity.ActionInput(descriptor, typeof(TAction), typeof(TResult)),
            KernelSchemaIdentity.ActionResult(descriptor, typeof(TAction), typeof(TResult)));
    }

    private static ActionInterceptionCapabilities ResolveActionCapabilities<TActionValue, TResultValue>(
        ActionDescriptor<TActionValue, TResultValue> descriptor,
        KernelActionHookRegistration hook,
        KernelGraphCompileOptions options)
    {
        var allowed = descriptor.Capabilities & options.SupportedActionCapabilities;
        if (options.ActionCapabilityGrants?.TryGetValue(descriptor.Key.Value, out var administratorGrant) == true)
            allowed &= administratorGrant;
        if (hook.OwnerModuleId == KernelCapabilities.CoreOwner)
            return allowed;

        var requested = GetActionModuleGrant(descriptor.Key, hook.OwnerModuleId, options);
        var unauthorized = requested & ~allowed;
        if (unauthorized != 0)
        {
            throw new KernelGraphCompilationException(
                $"Module '{hook.OwnerModuleId}' requests unauthorized effects '{unauthorized}' " +
                $"for action '{descriptor.Key.Value}'.");
        }
        return requested;
    }

    private static ActionInterceptionCapabilities ResolveActionModuleGrant<TActionValue, TResultValue>(
        ActionDescriptor<TActionValue, TResultValue> descriptor,
        string ownerModuleId,
        KernelGraphCompileOptions options)
    {
        if (ownerModuleId == KernelCapabilities.CoreOwner)
            return KernelCapabilities.AllActions;
        var moduleGrant = GetActionModuleGrant(descriptor.Key, ownerModuleId, options);
        var unauthorized = descriptor.Capabilities & ~moduleGrant;
        if (unauthorized != 0)
            throw new KernelGraphCompilationException(
                $"Module '{ownerModuleId}' requests unauthorized effects '{unauthorized}' " +
                $"for action '{descriptor.Key.Value}'.");
        return moduleGrant;
    }

    private static ActionInterceptionCapabilities GetActionModuleGrant(
        SharpClawActionKey key,
        string ownerModuleId,
        KernelGraphCompileOptions options)
    {
        if (options.ActionModuleCapabilityGrants is not { } grants ||
            !grants.TryGetValue(ownerModuleId, out var moduleGrants) ||
            !moduleGrants.TryGetValue(key.Value, out var moduleGrant))
            throw new KernelGraphCompilationException(
                $"Module '{ownerModuleId}' has no manifest grant for action '{key.Value}'.");
        return moduleGrant;
    }

    private static bool ResolveSensitiveApproval<TActionValue, TResultValue>(
        ActionDescriptor<TActionValue, TResultValue> descriptor,
        string moduleId,
        KernelGraphCompileOptions options,
        Type actionType,
        Type resultType)
    {
        if (!descriptor.ContainsSensitiveData)
            return true;
        if (IsCanonicalCoreSensitiveAction(descriptor, moduleId, actionType, resultType))
            return true;
        var contractTypes = KernelSchemaIdentity.ActionTypes(
            descriptor,
            actionType,
            resultType);
        var schema = KernelSchemaIdentity.Action(descriptor, actionType, resultType);
        return (options.SensitiveActionApprovals ?? []).Any(approval =>
            approval.ModuleId == moduleId &&
            approval.ActionKey == descriptor.Key &&
            approval.ActionVersion == descriptor.Version &&
            approval.ActionType == contractTypes.ActionType.AssemblyQualifiedName &&
            approval.ResultType == contractTypes.ResultType.AssemblyQualifiedName &&
            approval.SchemaIdentity == schema);
    }

    private static bool IsCanonicalCoreSensitiveAction<TActionValue, TResultValue>(
        ActionDescriptor<TActionValue, TResultValue> descriptor,
        string moduleId,
        Type actionType,
        Type resultType) =>
        moduleId == KernelCapabilities.CoreOwner &&
        actionType == typeof(KernelActionEnvelope) &&
        resultType == typeof(object) &&
        SharpClawActionCatalog.Kernel.Contains(descriptor.Key) &&
        descriptor.ContainsSensitiveData &&
        KernelGraphHasher.Flatten("descriptor", descriptor).SequenceEqual(
            KernelGraphHasher.Flatten(
                "descriptor",
                KernelActionCatalog.DescriptorFor(descriptor.Key).ToDescriptor()));

    private static bool Matches(
        KernelHookTargetKind targetKind,
        SharpClawActionKey? key,
        string? category,
        SharpClawActionKey actionKey,
        string actionCategory) => targetKind switch
        {
            KernelHookTargetKind.Exact => key == actionKey,
            KernelHookTargetKind.Category => string.Equals(category, actionCategory, StringComparison.Ordinal),
            KernelHookTargetKind.Any => true,
            _ => false
        };
}

internal interface IEventDefinitionRegistration
{
    dynamic Descriptor { get; }

    SharpClawEventKey Key { get; }

    ICompiledEventDefinition Compile(
        IReadOnlyList<KernelEventHookRegistration> hooks,
        IServiceProvider? serviceProvider,
        KernelGraphCompileOptions options);
}

internal sealed class EventDefinitionRegistration<TEvent>(
    EventDescriptor<TEvent> descriptor,
    string ownerModuleId) : IEventDefinitionRegistration
{
    public dynamic Descriptor => descriptor;

    public SharpClawEventKey Key => descriptor.Key;

    public ICompiledEventDefinition Compile(
        IReadOnlyList<KernelEventHookRegistration> hooks,
        IServiceProvider? serviceProvider,
        KernelGraphCompileOptions options)
    {
        var unsupported = descriptor.Capabilities & ~options.SupportedEventCapabilities;
        if (unsupported != 0)
        {
            throw new KernelGraphCompilationException(
                $"Event '{descriptor.Key.Value}' requires unsupported capabilities: {unsupported}.");
        }
        var effectiveCapabilities = descriptor.Capabilities & options.SupportedEventCapabilities;
        if (options.EventCapabilityGrants?.TryGetValue(descriptor.Key.Value, out var grant) == true)
            effectiveCapabilities &= grant;
        effectiveCapabilities &= ResolveEventModuleGrant(
            descriptor,
            ownerModuleId,
            options);

        var matching = hooks
            .Where(hook => Matches(hook.TargetKind, hook.Key, hook.Category, descriptor.Key, descriptor.Category))
            .ToArray();
        var ordered = KernelHookOrdering.Order(matching);
        var interceptors = new List<IEventFrame<TEvent>>();
        var listeners = new List<KernelEventListener<TEvent>>();
        foreach (var hook in ordered)
        {
            var hookCapabilities = ResolveEventCapabilities(descriptor, hook, options);
            var requiredCapabilities = hook.Kind == KernelEventHookKind.Listener
                ? EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe
                : EventInterceptionCapabilities.Inspect;
            if ((hookCapabilities & requiredCapabilities) != requiredCapabilities)
            {
                throw new KernelGraphCompilationException(
                    $"Module '{hook.OwnerModuleId}' cannot receive event '{descriptor.Key.Value}' " +
                    $"without the effective capabilities '{requiredCapabilities}'.");
            }
            var hookSensitiveApproved = ResolveSensitiveApproval(
                descriptor,
                hook.OwnerModuleId,
                options,
                typeof(TEvent));
            if (hook.Kind == KernelEventHookKind.Interceptor)
            {
                if (hook.IsUntyped)
                {
                    if (!typeof(IAnyEventInterceptor).IsAssignableFrom(hook.HandlerType))
                        throw new KernelGraphCompilationException(
                            $"'{hook.HandlerType.FullName}' does not implement IAnyEventInterceptor.");
                    interceptors.Add(new AnyEventFrame<TEvent>(
                        (IAnyEventInterceptor)KernelServiceResolution.Resolve(hook.HandlerType, serviceProvider),
                        hook.TargetKind,
                        hook.Key,
                        hook.Category,
                        hook.Ordering,
                        hook.OwnerModuleId,
                        hookCapabilities,
                        hookSensitiveApproved));
                }
                else
                {
                    var expected = typeof(IEventInterceptor<>).MakeGenericType(typeof(TEvent));
                    if (!expected.IsAssignableFrom(hook.HandlerType))
                        throw new KernelGraphCompilationException(
                            $"'{hook.HandlerType.FullName}' does not implement '{expected.FullName}'.");
                    interceptors.Add(new TypedEventFrame<TEvent>(
                        (IEventInterceptor<TEvent>)KernelServiceResolution.Resolve(
                            hook.HandlerType,
                            serviceProvider),
                        hook.TargetKind,
                        hook.Key,
                        hook.Category,
                        hook.Ordering,
                        hook.OwnerModuleId,
                        hookCapabilities,
                        hookSensitiveApproved));
                }
            }
            else
            {
                if (hook.IsUntyped)
                {
                    if (!typeof(IAnyEventListener).IsAssignableFrom(hook.HandlerType))
                        throw new KernelGraphCompilationException(
                            $"'{hook.HandlerType.FullName}' does not implement IAnyEventListener.");
                    listeners.Add(new KernelEventListener<TEvent>(
                        (IAnyEventListener)KernelServiceResolution.Resolve(hook.HandlerType, serviceProvider),
                        hook.Delivery,
                        hook.Ordering.Id,
                        hook.TargetKind,
                        hook.Key,
                        hook.Category,
                        hook.OwnerModuleId,
                        hook.HandlerType,
                        hook.Ordering,
                        hookCapabilities,
                        hookSensitiveApproved));
                }
                else
                {
                    var expected = typeof(IEventListener<>).MakeGenericType(typeof(TEvent));
                    if (!expected.IsAssignableFrom(hook.HandlerType))
                        throw new KernelGraphCompilationException(
                            $"'{hook.HandlerType.FullName}' does not implement '{expected.FullName}'.");
                    listeners.Add(new KernelEventListener<TEvent>(
                        (IEventListener<TEvent>)KernelServiceResolution.Resolve(
                            hook.HandlerType,
                            serviceProvider),
                        hook.Delivery,
                        hook.Ordering.Id,
                        hook.TargetKind,
                        hook.Key,
                        hook.Category,
                        hook.OwnerModuleId,
                        hook.HandlerType,
                        hook.Ordering,
                        hookCapabilities,
                        hookSensitiveApproved));
                }
            }
        }

        var sensitiveApproved = ResolveSensitiveApproval(
            descriptor,
            ownerModuleId,
            options,
            typeof(TEvent));
        if (descriptor.ContainsSensitiveData &&
            (interceptors.Any(frame => !frame.SensitiveApproved) ||
             listeners.Any(listener => !listener.SensitiveApproved)))
            sensitiveApproved = false;
        if (descriptor.ContainsSensitiveData && !sensitiveApproved)
            throw new KernelGraphCompilationException($"Sensitive event '{descriptor.Key.Value}' lacks exact approval.");

        return new CompiledEventDefinition<TEvent>(
            descriptor,
            ownerModuleId,
            interceptors,
            listeners,
            effectiveCapabilities,
            sensitiveApproved,
            KernelSchemaIdentity.EventPayload(descriptor, typeof(TEvent)));
    }

    private static EventInterceptionCapabilities ResolveEventCapabilities<TEventValue>(
        EventDescriptor<TEventValue> descriptor,
        KernelEventHookRegistration hook,
        KernelGraphCompileOptions options)
    {
        var allowed = descriptor.Capabilities & options.SupportedEventCapabilities;
        if (options.EventCapabilityGrants?.TryGetValue(descriptor.Key.Value, out var administratorGrant) == true)
            allowed &= administratorGrant;
        if (hook.OwnerModuleId == KernelCapabilities.CoreOwner)
            return allowed;

        var requested = GetEventModuleGrant(descriptor.Key, hook.OwnerModuleId, options);
        var unauthorized = requested & ~allowed;
        if (unauthorized != 0)
        {
            throw new KernelGraphCompilationException(
                $"Module '{hook.OwnerModuleId}' requests unauthorized effects '{unauthorized}' " +
                $"for event '{descriptor.Key.Value}'.");
        }
        return requested;
    }

    private static EventInterceptionCapabilities ResolveEventModuleGrant<TEventValue>(
        EventDescriptor<TEventValue> descriptor,
        string ownerModuleId,
        KernelGraphCompileOptions options)
    {
        if (ownerModuleId == KernelCapabilities.CoreOwner)
            return EventInterceptionCapabilities.Inspect |
                   EventInterceptionCapabilities.Replace |
                   EventInterceptionCapabilities.Cancel |
                   EventInterceptionCapabilities.StopPropagation |
                   EventInterceptionCapabilities.Observe;
        var moduleGrant = GetEventModuleGrant(descriptor.Key, ownerModuleId, options);
        var unauthorized = descriptor.Capabilities & ~moduleGrant;
        if (unauthorized != 0)
            throw new KernelGraphCompilationException(
                $"Module '{ownerModuleId}' requests unauthorized effects '{unauthorized}' " +
                $"for event '{descriptor.Key.Value}'.");
        return moduleGrant;
    }

    private static EventInterceptionCapabilities GetEventModuleGrant(
        SharpClawEventKey key,
        string ownerModuleId,
        KernelGraphCompileOptions options)
    {
        if (options.EventModuleCapabilityGrants is not { } grants ||
            !grants.TryGetValue(ownerModuleId, out var moduleGrants) ||
            !moduleGrants.TryGetValue(key.Value, out var moduleGrant))
            throw new KernelGraphCompilationException(
                $"Module '{ownerModuleId}' has no manifest grant for event '{key.Value}'.");
        return moduleGrant;
    }

    private static bool ResolveSensitiveApproval<TEventValue>(
        EventDescriptor<TEventValue> descriptor,
        string moduleId,
        KernelGraphCompileOptions options,
        Type eventType)
    {
        if (!descriptor.ContainsSensitiveData)
            return true;
        var schema = KernelSchemaIdentity.Event(descriptor, eventType);
        return (options.SensitiveEventApprovals ?? []).Any(approval =>
            approval.ModuleId == moduleId &&
            approval.EventKey == descriptor.Key &&
            approval.EventVersion == descriptor.Version &&
            approval.EventType == eventType.AssemblyQualifiedName &&
            approval.SchemaIdentity == schema);
    }

    private static bool Matches(
        KernelHookTargetKind targetKind,
        SharpClawEventKey? key,
        string? category,
        SharpClawEventKey eventKey,
        string eventCategory) => targetKind switch
        {
            KernelHookTargetKind.Exact => key == eventKey,
            KernelHookTargetKind.Category => string.Equals(category, eventCategory, StringComparison.Ordinal),
            KernelHookTargetKind.Any => true,
            _ => false
        };
}

internal static class KernelHookOrdering
{
    public static IReadOnlyList<T> Order<T>(IReadOnlyList<T> hooks) where T : class
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ordering in hooks.Select(Ordering))
        {
            if (!ids.Add(ordering.Id))
                throw new KernelGraphCompilationException($"Hook ordering id '{ordering.Id}' is registered more than once.");
        }

        var remaining = hooks.ToList();
        var result = new List<T>(hooks.Count);
        while (remaining.Count > 0)
        {
            var candidate = remaining
                .Where(item => !HasIncoming(item, remaining))
                .OrderBy(item => Priority(item))
                .ThenBy(item => Ordering(item).Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (candidate is null)
                throw new KernelGraphCompilationException("Hook ordering contains a cycle.");
            result.Add(candidate);
            remaining.Remove(candidate);
        }

        return result;
    }

    private static bool HasIncoming<T>(T candidate, IReadOnlyList<T> nodes) where T : class =>
        nodes.Any(node => !ReferenceEquals(node, candidate) && MustRunBefore(Ordering(node), Ordering(candidate)));

    private static bool MustRunBefore(HookOrdering left, HookOrdering right) =>
        (left.Before ?? Array.Empty<string>()).Contains(right.Id, StringComparer.Ordinal) ||
        (right.After ?? Array.Empty<string>()).Contains(left.Id, StringComparer.Ordinal);

    private static HookOrdering Ordering<T>(T item) where T : class => item switch
    {
        KernelActionHookRegistration action => action.Ordering,
        KernelEventHookRegistration eventHook => eventHook.Ordering,
        _ => throw new KernelGraphCompilationException("Unknown hook registration.")
    };

    private static int Priority<T>(T item) where T : class => Ordering(item).Priority switch
    {
        HookPriority.Highest => 0,
        HookPriority.High => 1,
        HookPriority.Normal => 2,
        HookPriority.Low => 3,
        HookPriority.Lowest => 4,
        _ => 2
    };
}

public static class KernelSchemaIdentity
{
    public static string Action<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor) =>
        Action(descriptor, typeof(TAction), typeof(TResult));

    public static string Action<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        Type actionType,
        Type resultType)
    {
        var contractTypes = ActionTypes(descriptor, actionType, resultType);
        var inputSchema = ActionInput(descriptor, actionType, resultType);
        var resultSchema = ActionResult(descriptor, actionType, resultType);
        return string.Join(
            "|",
            descriptor.Key.Value,
            KernelGraphHasher.StableScalar(descriptor.Version),
            descriptor.Category,
            contractTypes.ActionType.AssemblyQualifiedName,
            contractTypes.ResultType.AssemblyQualifiedName,
            inputSchema.ContractName,
            KernelGraphHasher.StableScalar(inputSchema.Version),
            inputSchema.ContentHash,
            resultSchema.ContractName,
            KernelGraphHasher.StableScalar(resultSchema.Version),
            resultSchema.ContentHash,
            KernelGraphHasher.StableScalar(descriptor.ProtocolVersionRange.Minimum),
            KernelGraphHasher.StableScalar(descriptor.ProtocolVersionRange.Maximum),
            string.Join(",", descriptor.SafePoints.Select(value => KernelGraphHasher.StableScalar(value))));
    }

    public static (Type ActionType, Type ResultType) ActionTypes<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        Type actionType,
        Type resultType)
    {
        if (actionType == typeof(KernelActionEnvelope) &&
            resultType == typeof(object) &&
            SharpClawActionCatalog.Kernel.Contains(descriptor.Key))
        {
            var contract = KernelActionCatalog.DescriptorFor(descriptor.Key);
            return (contract.InputPayloadType, contract.ResultPayloadType);
        }
        return (actionType, resultType);
    }

    public static string Event<TEvent>(
        EventDescriptor<TEvent> descriptor,
        Type eventType) =>
        string.Join(
            "|",
            descriptor.Key.Value,
            KernelGraphHasher.StableScalar(descriptor.Version),
            descriptor.Category,
            eventType.AssemblyQualifiedName,
            KernelGraphHasher.StableScalar(descriptor.ProtocolVersionRange.Minimum),
            KernelGraphHasher.StableScalar(descriptor.ProtocolVersionRange.Maximum));

    internal static JsonSchemaReference ActionInput<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        Type actionType,
        Type resultType)
    {
        if (actionType == typeof(KernelActionEnvelope) &&
            resultType == typeof(object) &&
            SharpClawActionCatalog.Kernel.Contains(descriptor.Key))
            return KernelActionCatalog.DescriptorFor(descriptor.Key).InputSchema;
        return TypeSchema("action.input", descriptor.Key.Value, descriptor.Version, actionType);
    }

    internal static JsonSchemaReference ActionResult<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        Type actionType,
        Type resultType)
    {
        if (actionType == typeof(KernelActionEnvelope) &&
            resultType == typeof(object) &&
            SharpClawActionCatalog.Kernel.Contains(descriptor.Key))
            return KernelActionCatalog.DescriptorFor(descriptor.Key).ResultSchema;
        return TypeSchema("action.result", descriptor.Key.Value, descriptor.Version, resultType);
    }

    internal static JsonSchemaReference EventPayload<TEvent>(
        EventDescriptor<TEvent> descriptor,
        Type eventType) =>
        TypeSchema("event.payload", descriptor.Key.Value, descriptor.Version, eventType);

    private static JsonSchemaReference TypeSchema(
        string role,
        string key,
        int version,
        Type type)
    {
        var contractName = $"sharpclaw.kernel.{role}.{key}";
        var identity = $"{contractName}|{version}|{type.AssemblyQualifiedName}";
        return new JsonSchemaReference(
            contractName,
            version,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }
}

internal interface ICompiledActionDefinition
{
    object DescriptorObject { get; }

    dynamic Descriptor { get; }

    SharpClawActionKey Key { get; }

    int Version { get; }

    string Category { get; }

    ActionInterceptionCapabilities Capabilities { get; }

    ActionInterceptionCapabilities EffectiveCapabilities { get; }

    ActionInterceptionCapabilities SnapshotCapabilities { get; }

    bool ContainsSensitiveData { get; }

    bool SensitiveApproved { get; }

    bool SnapshotSensitiveApproved { get; }

    string Signature { get; }

    Type ActionType { get; }

    Type ResultType { get; }

    JsonSchemaReference InputSchema { get; }

    JsonSchemaReference ResultSchema { get; }

    string OwnerModuleId { get; }
}

internal sealed class CompiledActionDefinition<TAction, TResult>(
    ActionDescriptor<TAction, TResult> descriptor,
    string ownerModuleId,
    IReadOnlyList<IActionFrame<TAction, TResult>> frames,
    ActionInterceptionCapabilities effectiveCapabilities,
    bool sensitiveApproved,
    JsonSchemaReference inputSchema,
    JsonSchemaReference resultSchema) : ICompiledActionDefinition
{
    public ActionDescriptor<TAction, TResult> Descriptor { get; } = descriptor;

    dynamic ICompiledActionDefinition.Descriptor => Descriptor;

    public object DescriptorObject => Descriptor;

    public SharpClawActionKey Key => Descriptor.Key;

    public int Version => Descriptor.Version;

    public string Category => Descriptor.Category;

    public ActionInterceptionCapabilities Capabilities => Descriptor.Capabilities;

    public ActionInterceptionCapabilities EffectiveCapabilities { get; } = effectiveCapabilities;

    public ActionInterceptionCapabilities SnapshotCapabilities =>
        Frames.Count == 0
            ? EffectiveCapabilities
            : Frames.Aggregate(
                EffectiveCapabilities,
                (capabilities, frame) => capabilities & frame.EffectiveCapabilities);

    public bool ContainsSensitiveData => Descriptor.ContainsSensitiveData;

    public bool SensitiveApproved { get; } = sensitiveApproved;

    public bool SnapshotSensitiveApproved =>
        SensitiveApproved && Frames.All(frame => frame.SensitiveApproved);

    public string Signature => string.Join(
        ",",
        [
            $"descriptor|{KernelGraphHasher.Flatten("value", Descriptor).JoinWith(";")}",
            ..Frames.Select(frame =>
            $"hook|{frame.TargetKind}|{frame.TargetKey?.Value}|{frame.TargetCategory}|" +
            $"{frame.Ordering.Id}|{KernelGraphHasher.StableScalar(frame.Ordering.Priority)}|" +
            $"{string.Join(";", frame.Ordering.Before ?? [])}|{string.Join(";", frame.Ordering.After ?? [])}|" +
            $"{KernelGraphHasher.StableScalar(frame.Ordering.Timeout)}|" +
            $"{KernelGraphHasher.StableScalar(frame.Ordering.FailurePolicy)}|{frame.OwnerModuleId}|" +
            $"{frame.HandlerType.AssemblyQualifiedName}|{frame.IsUntyped}|" +
            $"{KernelGraphHasher.StableScalar((int)frame.EffectiveCapabilities)}|" +
            $"{KernelGraphHasher.StableScalar(frame.SensitiveApproved)}")]);

    public string OwnerModuleId { get; } = ownerModuleId;

    public IReadOnlyList<IActionFrame<TAction, TResult>> Frames { get; } = frames;

    public Type ActionType => typeof(TAction);

    public Type ResultType => typeof(TResult);

    public JsonSchemaReference InputSchema { get; } = inputSchema;

    public JsonSchemaReference ResultSchema { get; } = resultSchema;
}

internal interface IActionFrame<TAction, TResult>
{
    bool IsUntyped { get; }

    KernelHookTargetKind TargetKind { get; }

    SharpClawActionKey? TargetKey { get; }

    string? TargetCategory { get; }

    HookOrdering Ordering { get; }

    string OwnerModuleId { get; }

    Type HandlerType { get; }

    ActionInterceptionCapabilities EffectiveCapabilities { get; }

    bool SensitiveApproved { get; }
}

internal sealed class TypedActionFrame<TAction, TResult>(
    IActionInterceptor<TAction, TResult> interceptor,
    KernelHookTargetKind targetKind,
    SharpClawActionKey? targetKey,
    string? targetCategory,
    HookOrdering ordering,
    string ownerModuleId,
    ActionInterceptionCapabilities effectiveCapabilities,
    bool sensitiveApproved)
    : IActionFrame<TAction, TResult>
{
    public IActionInterceptor<TAction, TResult> Interceptor { get; } = interceptor;

    public bool IsUntyped => false;

    public KernelHookTargetKind TargetKind { get; } = targetKind;

    public SharpClawActionKey? TargetKey { get; } = targetKey;

    public string? TargetCategory { get; } = targetCategory;

    public HookOrdering Ordering { get; } = ordering;

    public string OwnerModuleId { get; } = ownerModuleId;

    public Type HandlerType => Interceptor.GetType();

    public ActionInterceptionCapabilities EffectiveCapabilities { get; } = effectiveCapabilities;

    public bool SensitiveApproved { get; } = sensitiveApproved;
}

internal sealed class AnyActionFrame<TAction, TResult>(
    IAnyActionInterceptor interceptor,
    KernelHookTargetKind targetKind,
    SharpClawActionKey? targetKey,
    string? targetCategory,
    HookOrdering ordering,
    string ownerModuleId,
    ActionInterceptionCapabilities effectiveCapabilities,
    bool sensitiveApproved)
    : IActionFrame<TAction, TResult>
{
    public IAnyActionInterceptor Interceptor { get; } = interceptor;

    public bool IsUntyped => true;

    public KernelHookTargetKind TargetKind { get; } = targetKind;

    public SharpClawActionKey? TargetKey { get; } = targetKey;

    public string? TargetCategory { get; } = targetCategory;

    public HookOrdering Ordering { get; } = ordering;

    public string OwnerModuleId { get; } = ownerModuleId;

    public Type HandlerType => Interceptor.GetType();

    public ActionInterceptionCapabilities EffectiveCapabilities { get; } = effectiveCapabilities;

    public bool SensitiveApproved { get; } = sensitiveApproved;
}

internal interface ICompiledEventDefinition
{
    dynamic Descriptor { get; }

    SharpClawEventKey Key { get; }

    int Version { get; }

    string Category { get; }

    EventInterceptionCapabilities Capabilities { get; }

    EventInterceptionCapabilities EffectiveCapabilities { get; }

    EventInterceptionCapabilities SnapshotCapabilities { get; }

    bool ContainsSensitiveData { get; }

    bool SensitiveApproved { get; }

    bool SnapshotSensitiveApproved { get; }

    string Signature { get; }

    Type EventType { get; }

    JsonSchemaReference PayloadSchema { get; }

    string OwnerModuleId { get; }
}

internal sealed class CompiledEventDefinition<TEvent>(
    EventDescriptor<TEvent> descriptor,
    string ownerModuleId,
    IReadOnlyList<IEventFrame<TEvent>> interceptors,
    IReadOnlyList<KernelEventListener<TEvent>> listeners,
    EventInterceptionCapabilities effectiveCapabilities,
    bool sensitiveApproved,
    JsonSchemaReference payloadSchema) : ICompiledEventDefinition
{
    public EventDescriptor<TEvent> Descriptor { get; } = descriptor;

    dynamic ICompiledEventDefinition.Descriptor => Descriptor;

    public string OwnerModuleId { get; } = ownerModuleId;

    public SharpClawEventKey Key => Descriptor.Key;

    public int Version => Descriptor.Version;

    public string Category => Descriptor.Category;

    public EventInterceptionCapabilities Capabilities => Descriptor.Capabilities;

    public EventInterceptionCapabilities EffectiveCapabilities { get; } = effectiveCapabilities;

    public EventInterceptionCapabilities SnapshotCapabilities =>
        Interceptors.Count == 0 && Listeners.Count == 0
            ? EffectiveCapabilities
            : Interceptors
                .Select(frame => frame.EffectiveCapabilities)
                .Concat(Listeners.Select(listener => listener.EffectiveCapabilities))
                .Aggregate(EffectiveCapabilities, (capabilities, grant) => capabilities & grant);

    public bool ContainsSensitiveData => Descriptor.ContainsSensitiveData;

    public bool SensitiveApproved { get; } = sensitiveApproved;

    public bool SnapshotSensitiveApproved =>
        SensitiveApproved &&
        Interceptors.All(frame => frame.SensitiveApproved) &&
        Listeners.All(listener => listener.SensitiveApproved);

    public string Signature => string.Join(
        ",",
        [
            $"descriptor|{KernelGraphHasher.Flatten("value", Descriptor).JoinWith(";")}",
            ..Interceptors.Select(frame =>
            $"i|{KernelGraphHasher.StableScalar(frame.TargetKind)}|{frame.TargetKey?.Value}|{frame.TargetCategory}|" +
            $"{frame.Ordering.Id}|{KernelGraphHasher.StableScalar(frame.Ordering.Priority)}|" +
            $"{string.Join(";", frame.Ordering.Before ?? [])}|{string.Join(";", frame.Ordering.After ?? [])}|" +
            $"{KernelGraphHasher.StableScalar(frame.Ordering.Timeout)}|" +
            $"{KernelGraphHasher.StableScalar(frame.Ordering.FailurePolicy)}|{frame.OwnerModuleId}|" +
            $"{frame.HandlerType.AssemblyQualifiedName}|{frame.IsUntyped}|" +
            $"{KernelGraphHasher.StableScalar((int)frame.EffectiveCapabilities)}|" +
            $"{KernelGraphHasher.StableScalar(frame.SensitiveApproved)}"),
            ..Listeners.Select(listener =>
                $"l|{KernelGraphHasher.StableScalar(listener.TargetKind)}|{listener.TargetKey?.Value}|" +
                $"{listener.TargetCategory}|{listener.Id}|{listener.OwnerModuleId}|" +
                $"{listener.HandlerType.AssemblyQualifiedName}|{KernelGraphHasher.StableScalar(listener.Delivery)}|" +
                $"{KernelGraphHasher.StableScalar(listener.Ordering.Priority)}|" +
                $"{string.Join(";", listener.Ordering.Before ?? [])}|" +
                $"{string.Join(";", listener.Ordering.After ?? [])}|" +
                $"{KernelGraphHasher.StableScalar(listener.Ordering.Timeout)}|" +
                $"{KernelGraphHasher.StableScalar(listener.Ordering.FailurePolicy)}|" +
                $"{KernelGraphHasher.StableScalar((int)listener.EffectiveCapabilities)}|" +
                $"{KernelGraphHasher.StableScalar(listener.SensitiveApproved)}")]);

    public IReadOnlyList<IEventFrame<TEvent>> Interceptors { get; } = interceptors;

    public IReadOnlyList<KernelEventListener<TEvent>> Listeners { get; } = listeners;

    public Type EventType => typeof(TEvent);

    public JsonSchemaReference PayloadSchema { get; } = payloadSchema;
}

internal interface IEventFrame<TEvent>
{
    bool IsUntyped { get; }

    KernelHookTargetKind TargetKind { get; }

    SharpClawEventKey? TargetKey { get; }

    string? TargetCategory { get; }

    HookOrdering Ordering { get; }

    string OwnerModuleId { get; }

    Type HandlerType { get; }

    EventInterceptionCapabilities EffectiveCapabilities { get; }

    bool SensitiveApproved { get; }
}

internal sealed class TypedEventFrame<TEvent>(
    IEventInterceptor<TEvent> interceptor,
    KernelHookTargetKind targetKind,
    SharpClawEventKey? targetKey,
    string? targetCategory,
    HookOrdering ordering,
    string ownerModuleId,
    EventInterceptionCapabilities effectiveCapabilities,
    bool sensitiveApproved) : IEventFrame<TEvent>
{
    public IEventInterceptor<TEvent> Interceptor { get; } = interceptor;

    public bool IsUntyped => false;

    public KernelHookTargetKind TargetKind { get; } = targetKind;

    public SharpClawEventKey? TargetKey { get; } = targetKey;

    public string? TargetCategory { get; } = targetCategory;

    public HookOrdering Ordering { get; } = ordering;

    public string OwnerModuleId { get; } = ownerModuleId;

    public Type HandlerType => Interceptor.GetType();

    public EventInterceptionCapabilities EffectiveCapabilities { get; } = effectiveCapabilities;

    public bool SensitiveApproved { get; } = sensitiveApproved;
}

internal sealed class AnyEventFrame<TEvent>(
    IAnyEventInterceptor interceptor,
    KernelHookTargetKind targetKind,
    SharpClawEventKey? targetKey,
    string? targetCategory,
    HookOrdering ordering,
    string ownerModuleId,
    EventInterceptionCapabilities effectiveCapabilities,
    bool sensitiveApproved) : IEventFrame<TEvent>
{
    public IAnyEventInterceptor Interceptor { get; } = interceptor;

    public bool IsUntyped => true;

    public KernelHookTargetKind TargetKind { get; } = targetKind;

    public SharpClawEventKey? TargetKey { get; } = targetKey;

    public string? TargetCategory { get; } = targetCategory;

    public HookOrdering Ordering { get; } = ordering;

    public string OwnerModuleId { get; } = ownerModuleId;

    public Type HandlerType => Interceptor.GetType();

    public EventInterceptionCapabilities EffectiveCapabilities { get; } = effectiveCapabilities;

    public bool SensitiveApproved { get; } = sensitiveApproved;
}

internal sealed record KernelEventListener<TEvent>(
    object Listener,
    EventDelivery Delivery,
    string Id,
    KernelHookTargetKind TargetKind,
    SharpClawEventKey? TargetKey,
    string? TargetCategory,
    string OwnerModuleId,
    Type HandlerType,
    HookOrdering Ordering,
    EventInterceptionCapabilities EffectiveCapabilities,
    bool SensitiveApproved);

public sealed class KernelActionDefinitionBuilder(
    KernelGraphBuilder builder,
    string ownerModuleId) : IActionDefinitionBuilder
{
    public void Add<TAction, TResult>(ActionDescriptor<TAction, TResult> descriptor) =>
        builder.Add(descriptor, ownerModuleId);
}

public sealed class KernelEventDefinitionBuilder(
    KernelGraphBuilder builder,
    string ownerModuleId) : IEventDefinitionBuilder
{
    public void Add<TEvent>(EventDescriptor<TEvent> descriptor) =>
        builder.AddEvent(descriptor, ownerModuleId);

    public IEventHookRegistrationBuilder For(SharpClawEventKey key) =>
        new KernelEventHookRegistrationBuilder(builder, ownerModuleId, KernelHookTargetKind.Exact, key, null);

    public IEventHookRegistrationBuilder Category(string category) =>
        new KernelEventHookRegistrationBuilder(builder, ownerModuleId, KernelHookTargetKind.Category, null, category);

    public IEventHookRegistrationBuilder AnyEvent() =>
        new KernelEventHookRegistrationBuilder(builder, ownerModuleId, KernelHookTargetKind.Any, null, null);
}

public sealed class KernelActionHookBuilder(
    KernelGraphBuilder builder,
    string ownerModuleId) : IActionHookBuilder
{
    public IActionHookRegistrationBuilder For(SharpClawActionKey key) =>
        new KernelActionHookRegistrationBuilder(builder, ownerModuleId, KernelHookTargetKind.Exact, key, null);

    public IActionHookRegistrationBuilder Category(string category) =>
        new KernelActionHookRegistrationBuilder(builder, ownerModuleId, KernelHookTargetKind.Category, null, category);

    public IActionHookRegistrationBuilder AnyAction() =>
        new KernelActionHookRegistrationBuilder(builder, ownerModuleId, KernelHookTargetKind.Any, null, null);
}

public sealed class KernelActionHookRegistrationBuilder(
    KernelGraphBuilder builder,
    string ownerModuleId,
    KernelHookTargetKind targetKind,
    SharpClawActionKey? key,
    string? category) : IActionHookRegistrationBuilder
{
    public void Use<TInterceptor>(HookOrdering ordering) =>
        builder.AddActionHook(new KernelActionHookRegistration(
            targetKind,
            key,
            category,
            typeof(TInterceptor),
            false,
            ordering,
            ownerModuleId));

    public void UseAny<TInterceptor>(HookOrdering ordering) =>
        builder.AddActionHook(new KernelActionHookRegistration(
            targetKind,
            key,
            category,
            typeof(TInterceptor),
            true,
            ordering,
            ownerModuleId));
}

public sealed class KernelEventHookBuilder(
    KernelGraphBuilder builder,
    string ownerModuleId) : IEventHookBuilder
{
    public IEventHookRegistrationBuilder For(SharpClawEventKey key) =>
        new KernelEventHookRegistrationBuilder(builder, ownerModuleId, KernelHookTargetKind.Exact, key, null);

    public IEventHookRegistrationBuilder Category(string category) =>
        new KernelEventHookRegistrationBuilder(builder, ownerModuleId, KernelHookTargetKind.Category, null, category);

    public IEventHookRegistrationBuilder AnyEvent() =>
        new KernelEventHookRegistrationBuilder(builder, ownerModuleId, KernelHookTargetKind.Any, null, null);
}

public sealed class KernelEventHookRegistrationBuilder(
    KernelGraphBuilder builder,
    string ownerModuleId,
    KernelHookTargetKind targetKind,
    SharpClawEventKey? key,
    string? category) : IEventHookRegistrationBuilder
{
    public void Intercept<TInterceptor>(HookOrdering ordering) =>
        Add(typeof(TInterceptor), false, KernelEventHookKind.Interceptor, EventDelivery.Inline, ordering);

    public void InterceptAny<TInterceptor>(HookOrdering ordering) =>
        Add(typeof(TInterceptor), true, KernelEventHookKind.Interceptor, EventDelivery.Inline, ordering);

    public void Listen<TListener>(EventDelivery delivery, HookOrdering ordering) =>
        Add(typeof(TListener), false, KernelEventHookKind.Listener, delivery, ordering);

    public void ListenAny<TListener>(EventDelivery delivery, HookOrdering ordering) =>
        Add(typeof(TListener), true, KernelEventHookKind.Listener, delivery, ordering);

    private void Add(
        Type handlerType,
        bool isUntyped,
        KernelEventHookKind kind,
        EventDelivery delivery,
        HookOrdering ordering) =>
        builder.AddEventHook(new KernelEventHookRegistration(
            targetKind,
            key,
            category,
            handlerType,
            isUntyped,
            kind,
            delivery,
            ordering,
            ownerModuleId));
}

public sealed class KernelModuleRegistry
{
    private readonly KernelGraphBuilder _graphBuilder = new();
    private readonly List<ISharpClawModule> _modules = [];
    private readonly HashSet<string> _moduleIds = new(StringComparer.Ordinal);
    private KernelGraph? _compiledGraph;

    public IReadOnlyList<ISharpClawModule> Modules => new ReadOnlyCollection<ISharpClawModule>(_modules);

    public void Add(ISharpClawModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (_moduleIds.Contains(module.Identity.Id))
            throw new KernelGraphCompilationException($"Module '{module.Identity.Id}' is registered more than once.");
        var contributionGraph = new KernelGraphBuilder(
            includeStandardDefinitions: false,
            includeLifecycleDefinitions: false);
        var moduleBuilder = new KernelModuleBuilder(contributionGraph, module.Identity);
        module.Configure(moduleBuilder);
        _graphBuilder.Import(contributionGraph);
        _graphBuilder.AddModule(moduleBuilder.BuildDeclaration());
        _moduleIds.Add(module.Identity.Id);
        _modules.Add(module);
    }

    public KernelGraph Compile(
        IServiceProvider? serviceProvider = null,
        KernelGraphCompileOptions? options = null)
    {
        _compiledGraph = _graphBuilder.Compile(serviceProvider, options);
        return _compiledGraph;
    }

    public async ValueTask StartAsync(
        KernelGraph graph,
        string hostVersion,
        ExtensionFeatureSet features,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _compiledGraph = graph;
        var dispatcher = new KernelActionDispatcher(graph);
        var descriptor = graph.GetStandardAction(new SharpClawActionKey("module.start"));
        foreach (var module in _modules)
        {
            var context = new ModuleStartContext(
                module.Identity,
                hostVersion,
                graph.ActionSnapshot.ContractHash,
                features);
            await dispatcher.RunRequiredAsync<KernelActionEnvelope, object>(
                descriptor,
                new KernelActionEnvelope(descriptor.Key, context),
                async (envelope, ct) =>
                {
                    var effectiveContext = envelope.Payload switch
                    {
                        ModuleStartContext value => value,
                        KernelActionEnvelope nested when nested.Payload is ModuleStartContext value => value,
                        _ => throw new KernelActionExecutionException(
                            "The module.start action returned an invalid ModuleStartContext replacement.")
                    };
                    await module.StartAsync(effectiveContext, ct);
                    return (object)true;
                },
                graph.ActionSnapshot,
                cancellationToken);
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_compiledGraph is null)
            throw new KernelActionExecutionException("The module registry must compile before it can stop modules.");
        var dispatcher = new KernelActionDispatcher(_compiledGraph);
        var descriptor = _compiledGraph.GetStandardAction(new SharpClawActionKey("module.stop"));
        for (var index = _modules.Count - 1; index >= 0; index--)
        {
            var module = _modules[index];
            await dispatcher.RunRequiredAsync<KernelActionEnvelope, object>(
                descriptor,
                new KernelActionEnvelope(descriptor.Key, module.Identity),
                async (_, ct) =>
                {
                    await module.StopAsync(ct);
                    return (object)true;
                },
                _compiledGraph.ActionSnapshot,
                cancellationToken);
        }
    }
}

public sealed class KernelModuleBuilder : ISharpClawModuleBuilder
{
    private readonly ModuleIdentity _identity;

    public KernelModuleBuilder(KernelGraphBuilder graphBuilder, ModuleIdentity identity)
    {
        _identity = identity;
        Actions = new KernelActionDefinitionBuilder(graphBuilder, identity.Id);
        Events = new KernelEventDefinitionBuilder(graphBuilder, identity.Id);
        Hooks = new KernelActionHookBuilder(graphBuilder, identity.Id);
        EventHooks = new KernelEventHookBuilder(graphBuilder, identity.Id);
        Tools = new KernelToolContributionBuilder(graphBuilder, identity.Id);
        Chat = new KernelChatLifecycleBuilder();
        Contracts = new KernelModuleContractBuilder(identity.Id);
        Storage = new KernelModuleStorageBuilder();
        Services = new KernelServiceCollection();
    }

    public IActionDefinitionBuilder Actions { get; }

    public IChatLifecycleBuilder Chat { get; }

    public IModuleContractBuilder Contracts { get; }

    public IEventDefinitionBuilder Events { get; }

    public IActionHookBuilder Hooks { get; }

    public IServiceCollection Services { get; }

    public IModuleStorageBuilder Storage { get; }

    public IToolContributionBuilder Tools { get; }

    public IEventHookBuilder EventHooks { get; }

    internal KernelModuleDeclaration BuildDeclaration()
    {
        var chat = (KernelChatLifecycleBuilder)Chat;
        return new KernelModuleDeclaration(
            _identity,
            ((KernelServiceCollection)Services).ToArray(),
            ((KernelModuleContractBuilder)Contracts).Declarations.ToArray(),
            ((KernelModuleStorageBuilder)Storage).Descriptors.ToArray(),
            chat.ConversationResolver,
            chat.ConversationResolverRegistration,
            chat.ProfileResolver,
            chat.ProfileResolverRegistration,
            chat.ContextContributors.ToArray());
    }
}

public sealed class KernelToolContributionBuilder(
    KernelGraphBuilder builder,
    string ownerModuleId) : IToolContributionBuilder
{
    public void Add<THandler>(ToolDescriptor descriptor) where THandler : IToolHandler =>
        builder.AddTool<THandler>(descriptor, ownerModuleId);
}

public sealed class KernelServiceCollection : List<ServiceDescriptor>, IServiceCollection;

public enum KernelModuleContractKind
{
    Export,
    Requirement
}

public sealed record KernelModuleContractDeclaration(
    string OwnerModuleId,
    Type ContractType,
    string Name,
    int SchemaVersion,
    int MaxBytes,
    KernelModuleContractKind Kind,
    bool Optional);

public sealed class KernelModuleContractBuilder(string ownerModuleId) : IModuleContractBuilder
{
    private readonly List<KernelModuleContractDeclaration> _declarations = [];

    public IReadOnlyList<KernelModuleContractDeclaration> Declarations => _declarations;

    public void Export<T>(string name, int schemaVersion, int maxBytes) =>
        _declarations.Add(new KernelModuleContractDeclaration(
            ownerModuleId,
            typeof(T),
            name,
            schemaVersion,
            maxBytes,
            KernelModuleContractKind.Export,
            false));

    public void Require<T>(string name, int minimumVersion, bool optional) =>
        _declarations.Add(new KernelModuleContractDeclaration(
            ownerModuleId,
            typeof(T),
            name,
            minimumVersion,
            0,
            KernelModuleContractKind.Requirement,
            optional));
}

public sealed class KernelModuleStorageBuilder : IModuleStorageBuilder
{
    private readonly List<ModuleStorageContractDescriptor> _descriptors = [];

    public IReadOnlyList<ModuleStorageContractDescriptor> Descriptors => _descriptors;

    public void Add(ModuleStorageContractDescriptor descriptor) => _descriptors.Add(descriptor);
}

public sealed class KernelChatLifecycleBuilder : IChatLifecycleBuilder
{
    private readonly List<Type> _contextContributors = [];

    public IReadOnlyList<Type> ContextContributors => _contextContributors;

    public Type? ProfileResolver { get; private set; }

    public ExclusiveRegistration? ProfileResolverRegistration { get; private set; }

    public Type? ConversationResolver { get; private set; }

    public ExclusiveRegistration? ConversationResolverRegistration { get; private set; }

    public void AddContextContributor<TContributor>() where TContributor : IChatContextContributor =>
        _contextContributors.Add(typeof(TContributor));

    public void UseChatProfileResolver<TResolver>(ExclusiveRegistration registration)
        where TResolver : IChatProfileResolver
    {
        if (ProfileResolver is not null)
            throw new KernelGraphCompilationException("A chat profile resolver was registered more than once.");
        ProfileResolver = typeof(TResolver);
        ProfileResolverRegistration = registration ?? throw new ArgumentNullException(nameof(registration));
    }

    public void UseConversationResolver<TResolver>(ExclusiveRegistration registration)
        where TResolver : IConversationResolver
    {
        if (ConversationResolver is not null)
            throw new KernelGraphCompilationException("A conversation resolver was registered more than once.");
        ConversationResolver = typeof(TResolver);
        ConversationResolverRegistration = registration ?? throw new ArgumentNullException(nameof(registration));
    }
}
