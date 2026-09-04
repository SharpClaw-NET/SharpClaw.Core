using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Core.Kernel;

public sealed class KernelGraphBuilder : IActionDefinitionSink, IEventDefinitionSink
{
    private readonly List<IActionDefinitionRegistration> _actions = [];
    private readonly List<IEventDefinitionRegistration> _events = [];
    private readonly List<KernelActionHookRegistration> _actionHooks = [];
    private readonly List<KernelEventHookRegistration> _eventHooks = [];
    private readonly List<KernelToolRegistration> _tools = [];
    private bool _serviceBindingsImported;

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

    public KernelActionDefinitionBuilder Actions => new(this, "core");

    public KernelEventDefinitionBuilder Events => new(this, "core");

    public KernelActionHookBuilder Hooks => new(this, "core");

    public void Add<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        string OwnerId = "core") =>
        _actions.Add(new ActionDefinitionRegistration<TAction, TResult>(descriptor, OwnerId));

    void IActionDefinitionSink.Add<TAction, TResult>(
        string sourceId,
        ActionDescriptor<TAction, TResult> descriptor) =>
        Add(descriptor, sourceId);

    public void AddEvent<TEvent>(
        EventDescriptor<TEvent> descriptor,
        string OwnerId = "core") =>
        _events.Add(new EventDefinitionRegistration<TEvent>(descriptor, OwnerId));

    void IEventDefinitionSink.Add<TEvent>(
        string sourceId,
        EventDescriptor<TEvent> descriptor) =>
        AddEvent(descriptor, sourceId);

    public void AddTool<THandler>(ToolDescriptor descriptor, string OwnerId = "core") =>
        _tools.Add(new KernelToolRegistration(
            descriptor,
            OwnerId,
            typeof(THandler),
            HandlerIdentity: typeof(THandler).AssemblyQualifiedName));

    internal void AddBoundTool(
        ToolDescriptor descriptor,
        string OwnerId,
        IToolHandler handler,
        string handlerIdentity) =>
        _tools.Add(new KernelToolRegistration(
            descriptor,
            OwnerId,
            handler.GetType(),
            handler,
            handlerIdentity));

    public KernelGraph Compile(
        IServiceProvider serviceProvider,
        KernelGraphCompileOptions? options = null)
    {
        ImportServiceBindings(serviceProvider);
        return new KernelSnapshotCompiler().Compile(this, serviceProvider, options);
    }

    internal IReadOnlyList<IActionDefinitionRegistration> ActionDefinitions => _actions;

    internal IReadOnlyList<IEventDefinitionRegistration> EventDefinitions => _events;

    internal IReadOnlyList<KernelActionHookRegistration> ActionHooks => _actionHooks;

    internal IReadOnlyList<KernelEventHookRegistration> EventHooks => _eventHooks;

    internal IReadOnlyList<KernelToolRegistration> Tools => _tools;

    internal void AddActionHook(KernelActionHookRegistration registration) => _actionHooks.Add(registration);

    internal void AddEventHook(KernelEventHookRegistration registration) => _eventHooks.Add(registration);

    internal void Import(KernelGraphBuilder source)
    {
        _actions.AddRange(source._actions);
        _events.AddRange(source._events);
        _actionHooks.AddRange(source._actionHooks);
        _eventHooks.AddRange(source._eventHooks);
        _tools.AddRange(source._tools);
    }

    private void ImportServiceBindings(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        if (_serviceBindingsImported)
            return;

        foreach (var binding in serviceProvider.GetServices<IActionDefinitionBinding>())
            binding.AddTo(this);
        foreach (var binding in serviceProvider.GetServices<IEventDefinitionBinding>())
            binding.AddTo(this);
        foreach (var binding in serviceProvider.GetServices<ActionHookBinding>())
        {
            _actionHooks.Add(new KernelActionHookRegistration(
                ToKernelTarget(binding.TargetKind),
                binding.ActionKey,
                binding.Category,
                binding.HandlerType,
                binding.BoundHandler,
                binding.IsUntyped,
                binding.Ordering,
                binding.SourceId,
                binding.HandlerIdentity));
        }
        foreach (var binding in serviceProvider.GetServices<EventHookBinding>())
        {
            _eventHooks.Add(new KernelEventHookRegistration(
                ToKernelTarget(binding.TargetKind),
                binding.EventKey,
                binding.Category,
                binding.HandlerType,
                binding.BoundHandler,
                binding.IsUntyped,
                binding.Kind == EventHookKind.Interceptor
                    ? KernelEventHookKind.Interceptor
                    : KernelEventHookKind.Listener,
                binding.Delivery,
                binding.Ordering,
                binding.SourceId,
                binding.HandlerIdentity));
        }
        foreach (var binding in serviceProvider.GetServices<ToolHandlerBinding>())
        {
            _tools.Add(new KernelToolRegistration(
                binding.Descriptor,
                binding.SourceId,
                binding.HandlerType,
                binding.BoundHandler,
                binding.HandlerIdentity));
        }

        _serviceBindingsImported = true;
    }

    private static KernelHookTargetKind ToKernelTarget(BehaviorTargetKind targetKind) =>
        targetKind switch
        {
            BehaviorTargetKind.Exact => KernelHookTargetKind.Exact,
            BehaviorTargetKind.Category => KernelHookTargetKind.Category,
            BehaviorTargetKind.Any => KernelHookTargetKind.Any,
            _ => throw new KernelGraphCompilationException("The behavior target is not supported."),
        };

    private void AddStandardDefinitions()
    {
        foreach (var manifest in KernelActionCatalog.Descriptors)
        {
            if (!manifest.IsJobsAction)
                Add(manifest.ToDescriptor(), KernelCapabilities.CoreOwner);
        }
    }

    private void AddLifecycleDefinitions()
    {
        foreach (var descriptor in KernelActionLifecycleEvents.Descriptors)
            AddEvent(descriptor, KernelCapabilities.CoreOwner);
    }
}

public sealed class KernelActionDefinitionBuilder(
    KernelGraphBuilder builder,
    string sourceId)
{
    public void Add<TAction, TResult>(ActionDescriptor<TAction, TResult> descriptor) =>
        builder.Add(descriptor, sourceId);
}

public sealed class KernelEventDefinitionBuilder(
    KernelGraphBuilder builder,
    string sourceId)
{
    public void Add<TEvent>(EventDescriptor<TEvent> descriptor) =>
        builder.AddEvent(descriptor, sourceId);

    public KernelEventHookRegistrationBuilder For(SharpClawEventKey key) =>
        new(builder, sourceId, KernelHookTargetKind.Exact, key, null);

    public KernelEventHookRegistrationBuilder Category(string category) =>
        new(builder, sourceId, KernelHookTargetKind.Category, null, category);

    public KernelEventHookRegistrationBuilder AnyEvent() =>
        new(builder, sourceId, KernelHookTargetKind.Any, null, null);
}

public sealed class KernelActionHookBuilder(
    KernelGraphBuilder builder,
    string sourceId)
{
    public KernelActionHookRegistrationBuilder For(SharpClawActionKey key) =>
        new(builder, sourceId, KernelHookTargetKind.Exact, key, null);

    public KernelActionHookRegistrationBuilder Category(string category) =>
        new(builder, sourceId, KernelHookTargetKind.Category, null, category);

    public KernelActionHookRegistrationBuilder AnyAction() =>
        new(builder, sourceId, KernelHookTargetKind.Any, null, null);
}

public sealed class KernelActionHookRegistrationBuilder(
    KernelGraphBuilder builder,
    string sourceId,
    KernelHookTargetKind targetKind,
    SharpClawActionKey? key,
    string? category)
{
    public void Use<TInterceptor>(HookOrdering ordering) =>
        builder.AddActionHook(new KernelActionHookRegistration(
            targetKind,
            key,
            category,
            typeof(TInterceptor),
            null,
            false,
            ordering,
            sourceId,
            typeof(TInterceptor).AssemblyQualifiedName!));

    public void UseAny<TInterceptor>(HookOrdering ordering) =>
        builder.AddActionHook(new KernelActionHookRegistration(
            targetKind,
            key,
            category,
            typeof(TInterceptor),
            null,
            true,
            ordering,
            sourceId,
            typeof(TInterceptor).AssemblyQualifiedName!));
}

public sealed class KernelEventHookRegistrationBuilder(
    KernelGraphBuilder builder,
    string sourceId,
    KernelHookTargetKind targetKind,
    SharpClawEventKey? key,
    string? category)
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
            null,
            isUntyped,
            kind,
            delivery,
            ordering,
            sourceId,
            handlerType.AssemblyQualifiedName!));
}

public sealed class KernelSnapshotCompiler
{
    public KernelGraph Compile(
        KernelGraphBuilder builder,
        IServiceProvider serviceProvider,
        KernelGraphCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        options ??= new KernelGraphCompileOptions();
        options = KernelGraphCompileOptions.Freeze(options);
        var actionHooks = FreezeActionHooks(builder.ActionHooks);
        var eventHooks = FreezeEventHooks(builder.EventHooks);
        if (options.MaximumActionDepth < 1)
            throw new KernelGraphCompilationException("Maximum action depth must be positive.");

        var services = new KernelServiceGraph(serviceProvider);
        var actions = CompileActions(builder, services.Services, options, actionHooks);
        var events = CompileEvents(builder, services.Services, options, eventHooks);
        var tools = CompileTools(builder, services.Services);
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
            actionHooks,
            eventHooks,
            services,
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
            services,
            snapshot,
            chatSnapshot,
            options.MaximumActionDepth,
            actionHooks,
            services.Services,
            options);
    }

    private static Dictionary<string, ICompiledActionDefinition> CompileActions(
        KernelGraphBuilder builder,
        IServiceProvider serviceProvider,
        KernelGraphCompileOptions options,
        IReadOnlyList<KernelActionHookRegistration> actionHooks)
    {
        ValidateJobsCatalog(builder);
        var result = new Dictionary<string, ICompiledActionDefinition>(StringComparer.Ordinal);
        foreach (var definition in builder.ActionDefinitions)
        {
            if (!result.TryAdd(definition.Key.Value, definition.Compile(
                    actionHooks,
                    serviceProvider,
                    options)))
            {
                throw new KernelGraphCompilationException(
                    $"Action key '{definition.Descriptor.Key.Value}' is registered more than once.");
            }
        }

        return result;
    }

    private static void ValidateJobsCatalog(KernelGraphBuilder builder)
    {
        var invalid = builder.ActionDefinitions
            .Where(definition => definition.IsJobsAction && !definition.MatchesJobsCatalog)
            .Select(definition => definition.Key.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        if (invalid.Length > 0)
        {
            throw new KernelGraphCompilationException(
                $"Jobs action(s) use a typed descriptor that does not match the catalog profile: " +
                $"{string.Join(", ", invalid)}.");
        }

        var registered = builder.ActionDefinitions
            .Select(definition => definition.Key.Value)
            .Where(key => key.StartsWith("jobs.", StringComparison.Ordinal))
            .ToArray();
        if (registered.Length == 0)
            return;

        var expected = SharpClawActionCatalog.Jobs
            .Select(key => key.Value)
            .ToHashSet(StringComparer.Ordinal);
        var actual = registered.ToHashSet(StringComparer.Ordinal);
        var missing = expected.Except(actual, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var extra = actual.Except(expected, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || extra.Length > 0 || registered.Length != actual.Count)
        {
            throw new KernelGraphCompilationException(
                $"Jobs registration must register the catalog exactly once. Missing: " +
                $"{string.Join(", ", missing)}. Extra: {string.Join(", ", extra)}.");
        }

        var definitions = builder.ActionDefinitions
            .Where(definition => definition.IsJobsAction)
            .ToDictionary(definition => definition.Key.Value, StringComparer.Ordinal);
        foreach (var family in SharpClawActionCatalog.JobsFamilies)
        {
            var root = definitions[family];
            var before = definitions[$"{family}.before"];
            var after = definitions[$"{family}.after"];

            if (!string.Equals(root.OwnerId, before.OwnerId, StringComparison.Ordinal) ||
                !string.Equals(root.OwnerId, after.OwnerId, StringComparison.Ordinal))
            {
                throw new KernelGraphCompilationException(
                    $"Jobs family '{family}' must use one registration owner for root, before, and after descriptors.");
            }

            var expectedBeforeType = typeof(JobCheckpoint<>).MakeGenericType(root.ActionType);
            if (before.ActionType != expectedBeforeType || before.ResultType != expectedBeforeType)
            {
                throw new KernelGraphCompilationException(
                    $"Jobs family '{family}' before checkpoint types must match the root input type " +
                    $"'{root.ActionType.FullName}'.");
            }

            var expectedAfterType = typeof(JobCheckpoint<>).MakeGenericType(root.ResultType);
            if (after.ActionType != expectedAfterType || after.ResultType != expectedAfterType)
            {
                throw new KernelGraphCompilationException(
                    $"Jobs family '{family}' after checkpoint types must match the root result type " +
                    $"'{root.ResultType.FullName}'.");
            }
        }
    }

    private static Dictionary<string, ICompiledEventDefinition> CompileEvents(
        KernelGraphBuilder builder,
        IServiceProvider serviceProvider,
        KernelGraphCompileOptions options,
        IReadOnlyList<KernelEventHookRegistration> eventHooks)
    {
        var result = new Dictionary<string, ICompiledEventDefinition>(StringComparer.Ordinal);
        foreach (var definition in builder.EventDefinitions)
        {
            if (!result.TryAdd(definition.Key.Value, definition.Compile(
                    eventHooks,
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
            IToolHandler handler;
            try
            {
                handler = tool.Handler
                    ?? KernelServiceResolution.Resolve(tool.HandlerType, serviceProvider) as IToolHandler
                    ?? throw new KernelGraphCompilationException(
                        $"Tool handler '{tool.HandlerType.FullName}' cannot be resolved as IToolHandler.");
                if (handler is null)
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
                    $"Registration '{tool.OwnerId}' tool handler '{tool.HandlerType.FullName}' " +
                    $"cannot be resolved: {exception.Message}");
            }
            result.Add(tool with
            {
                Handler = handler,
                HandlerIdentity = tool.HandlerIdentity ?? tool.HandlerType.AssemblyQualifiedName,
            });
        }

        return new ReadOnlyCollection<KernelToolRegistration>(result);
    }

    private static string ComputeContractHash(
        IEnumerable<ICompiledActionDefinition> actions,
        IEnumerable<ICompiledEventDefinition> events,
        IEnumerable<KernelToolRegistration> tools,
        IEnumerable<KernelActionHookRegistration> actionHooks,
        IEnumerable<KernelEventHookRegistration> eventHooks,
        KernelServiceGraph services,
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
                        $"{KernelGraphHasher.StableScalar(action.SensitiveApproved)}|{action.OwnerId}|" +
                        $"{action.ActionType.AssemblyQualifiedName}|{action.ResultType.AssemblyQualifiedName}");
            records.AddRange(KernelGraphHasher.Flatten("action.input-schema", action.InputSchema));
            records.AddRange(KernelGraphHasher.Flatten("action.result-schema", action.ResultSchema));
            if (SharpClawActionCatalog.Kernel.Contains(action.Key))
            {
                var contract = KernelActionCatalog.DescriptorFor(action.Key);
                records.Add(
                    $"action.payload-contract|{action.Key.Value}|" +
                    $"{contract.InputPayloadType?.AssemblyQualifiedName ?? "registration-typed"}|" +
                    $"{contract.ResultPayloadType?.AssemblyQualifiedName ?? "registration-typed"}");
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
                        $"{eventDefinition.OwnerId}|" +
                        $"{eventDefinition.EventType.AssemblyQualifiedName}");
            records.AddRange(KernelGraphHasher.Flatten("event.payload-schema", eventDefinition.PayloadSchema));
            records.Add($"event.signature|{eventDefinition.Signature}");
        }
        foreach (var tool in tools.OrderBy(tool => tool.Descriptor.Name, StringComparer.Ordinal))
        {
            records.AddRange(KernelGraphHasher.Flatten("tool.descriptor", tool.Descriptor));
            records.Add($"tool.owner|{tool.OwnerId}|{tool.HandlerIdentity}");
        }
        foreach (var hook in actionHooks
                     .OrderBy(hook => hook.OwnerId, StringComparer.Ordinal)
                     .ThenBy(hook => hook.TargetKind)
                     .ThenBy(hook => hook.Key?.Value, StringComparer.Ordinal)
                     .ThenBy(hook => hook.Category, StringComparer.Ordinal)
                     .ThenBy(hook => hook.Ordering.Id, StringComparer.Ordinal))
            records.Add(
                $"action.registration|{hook.OwnerId}|{hook.TargetKind}|{hook.Key?.Value}|" +
                $"{hook.Category}|{hook.HandlerIdentity}|{hook.IsUntyped}|" +
                KernelGraphHasher.Flatten("ordering", hook.Ordering).JoinWith(";"));
        foreach (var hook in eventHooks
                     .OrderBy(hook => hook.OwnerId, StringComparer.Ordinal)
                     .ThenBy(hook => hook.TargetKind)
                     .ThenBy(hook => hook.Key?.Value, StringComparer.Ordinal)
                     .ThenBy(hook => hook.Category, StringComparer.Ordinal)
                     .ThenBy(hook => hook.Ordering.Id, StringComparer.Ordinal))
            records.Add(
                $"event.registration|{hook.OwnerId}|{hook.TargetKind}|{hook.Key?.Value}|" +
                $"{hook.Category}|{hook.HandlerIdentity}|{hook.IsUntyped}|{hook.Kind}|{hook.Delivery}|" +
                KernelGraphHasher.Flatten("ordering", hook.Ordering).JoinWith(";"));
        records.AddRange(services.HashRecords);
        AddDictionary(records, "action.grant", options.ActionCapabilityGrants);
        AddDictionary(records, "event.grant", options.EventCapabilityGrants);
        AddNestedDictionary(records, "action.registration-grant", options.ActionRegistrationCapabilityGrants);
        AddNestedDictionary(records, "event.registration-grant", options.EventRegistrationCapabilityGrants);
        AddApprovalBoundary(records, "sensitive.action", options.SensitiveActionApprovals);
        AddApprovalBoundary(records, "sensitive.external-action", options.ExternalSensitiveActionApprovals);
        AddApprovalBoundary(records, "sensitive.event", options.SensitiveEventApprovals);
        AddApprovalBoundary(records, "sensitive.external-event", options.ExternalSensitiveEventApprovals);
        foreach (var approval in (options.SensitiveActionApprovals ?? []).OrderBy(approval => approval.SourceId, StringComparer.Ordinal)
                     .ThenBy(approval => approval.ActionKey.Value, StringComparer.Ordinal)
                     .ThenBy(approval => approval.ActionVersion)
                     .ThenBy(approval => approval.ActionType, StringComparer.Ordinal)
                     .ThenBy(approval => approval.ResultType, StringComparer.Ordinal)
                     .ThenBy(approval => approval.SchemaIdentity, StringComparer.Ordinal))
            records.AddRange(KernelGraphHasher.Flatten("sensitive.action", approval));
        foreach (var approval in (options.SensitiveEventApprovals ?? []).OrderBy(approval => approval.SourceId, StringComparer.Ordinal)
                     .ThenBy(approval => approval.EventKey.Value, StringComparer.Ordinal)
                     .ThenBy(approval => approval.EventVersion)
                     .ThenBy(approval => approval.EventType, StringComparer.Ordinal)
                     .ThenBy(approval => approval.SchemaIdentity, StringComparer.Ordinal))
            records.AddRange(KernelGraphHasher.Flatten("sensitive.event", approval));
        foreach (var approval in (options.ExternalSensitiveActionApprovals ?? []).OrderBy(approval => approval.SourceId, StringComparer.Ordinal)
                     .ThenBy(approval => approval.ActionKey.Value, StringComparer.Ordinal)
                     .ThenBy(approval => approval.ActionVersion))
            records.AddRange(KernelGraphHasher.Flatten("sensitive.external-action", approval));
        foreach (var approval in (options.ExternalSensitiveEventApprovals ?? []).OrderBy(approval => approval.SourceId, StringComparer.Ordinal)
                     .ThenBy(approval => approval.EventKey.Value, StringComparer.Ordinal)
                     .ThenBy(approval => approval.EventVersion))
            records.AddRange(KernelGraphHasher.Flatten("sensitive.external-event", approval));

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
        foreach (var registration in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (registration.Value.Count == 0)
            {
                records.Add($"{prefix}|{registration.Key}|<empty>");
                continue;
            }
            foreach (var grant in registration.Value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                records.Add(
                    $"{prefix}|{registration.Key}|{grant.Key}|{KernelGraphHasher.StableScalar(grant.Value)}");
        }
    }

    private static IReadOnlyList<KernelActionHookRegistration> FreezeActionHooks(
        IReadOnlyList<KernelActionHookRegistration> hooks) =>
        new ReadOnlyCollection<KernelActionHookRegistration>(hooks
            .Select(hook => hook with { Ordering = FreezeOrdering(hook.Ordering) })
            .ToArray());

    private static IReadOnlyList<KernelEventHookRegistration> FreezeEventHooks(
        IReadOnlyList<KernelEventHookRegistration> hooks) =>
        new ReadOnlyCollection<KernelEventHookRegistration>(hooks
            .Select(hook => hook with { Ordering = FreezeOrdering(hook.Ordering) })
            .ToArray());

    private static HookOrdering FreezeOrdering(HookOrdering ordering) =>
        new(
            ordering.Id,
            ordering.Priority,
            new ReadOnlyCollection<string>((ordering.Before ?? []).ToArray()),
            new ReadOnlyCollection<string>((ordering.After ?? []).ToArray()),
            ordering.Timeout,
            ordering.FailurePolicy);
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
    private readonly IReadOnlyList<KernelActionHookRegistration> _actionHooks;
    private readonly IServiceProvider _serviceProvider;
    private readonly KernelGraphCompileOptions _compileOptions;

    internal KernelGraph(
        IReadOnlyDictionary<string, ICompiledActionDefinition> actions,
        IReadOnlyDictionary<string, ICompiledEventDefinition> events,
        IReadOnlyList<KernelToolRegistration> tools,
        KernelServiceGraph services,
        ActionPipelineSnapshot actionSnapshot,
        ChatPipelineSnapshot chatSnapshot,
        int maximumActionDepth,
        IReadOnlyList<KernelActionHookRegistration> actionHooks,
        IServiceProvider serviceProvider,
        KernelGraphCompileOptions compileOptions)
    {
        _actions = new ReadOnlyDictionary<string, ICompiledActionDefinition>(
            new Dictionary<string, ICompiledActionDefinition>(actions, StringComparer.Ordinal));
        _events = new ReadOnlyDictionary<string, ICompiledEventDefinition>(
            new Dictionary<string, ICompiledEventDefinition>(events, StringComparer.Ordinal));
        Tools = tools;
        Services = services;
        ActionSnapshot = actionSnapshot;
        ChatSnapshot = chatSnapshot;
        MaximumActionDepth = maximumActionDepth;
        _actionHooks = actionHooks.ToArray();
        _serviceProvider = serviceProvider;
        _compileOptions = compileOptions;
    }

    public ActionPipelineSnapshot ActionSnapshot { get; }

    public ChatPipelineSnapshot ChatSnapshot { get; }

    public IReadOnlyList<KernelToolRegistration> Tools { get; }

    public KernelServiceGraph Services { get; }

    public int MaximumActionDepth { get; }

    public object? GetService(Type serviceType) => Services.Services.GetService(serviceType);

    public TService GetRequiredService<TService>() where TService : notnull =>
        (TService)(GetService(typeof(TService)) ?? throw new KernelGraphCompilationException(
            $"Kernel registration service '{typeof(TService).FullName}' is not registered."));

    public KernelChatContextAssembler CreateChatContextAssembler(KernelActionDispatcher dispatcher) =>
        new(this, dispatcher, Services.ContextContributors);

    public bool ContainsAction(SharpClawActionKey key) => _actions.ContainsKey(key.Value);

    public bool ContainsEvent(SharpClawEventKey key) => _events.ContainsKey(key.Value);

    public ActionDescriptor<KernelActionEnvelope, object> GetStandardAction(SharpClawActionKey key)
    {
        if (SharpClawActionCatalog.Jobs.Contains(key))
            throw new KernelActionExecutionException(
                $"Jobs action '{key.Value}' has a typed descriptor. Use the Jobs descriptor accessor.");
        return GetAction<KernelActionEnvelope, object>(key).Descriptor;
    }

    public ActionDescriptor<TAction, TResult> GetJobsAction<TAction, TResult>(SharpClawActionKey key)
    {
        EnsureJobsRoot(key);
        return GetAction<TAction, TResult>(key).Descriptor;
    }

    public ActionDescriptor<JobCheckpoint<TValue>, JobCheckpoint<TValue>>
        GetJobsBeforeAction<TValue>(SharpClawActionKey key)
    {
        EnsureJobsCheckpoint(key, before: true);
        return GetAction<JobCheckpoint<TValue>, JobCheckpoint<TValue>>(key).Descriptor;
    }

    public ActionDescriptor<JobCheckpoint<TResult>, JobCheckpoint<TResult>>
        GetJobsAfterAction<TResult>(SharpClawActionKey key)
    {
        EnsureJobsCheckpoint(key, before: false);
        return GetAction<JobCheckpoint<TResult>, JobCheckpoint<TResult>>(key).Descriptor;
    }

    private static void EnsureJobsRoot(SharpClawActionKey key)
    {
        if (!SharpClawActionCatalog.Jobs.Contains(key) ||
            key.Value.EndsWith(".before", StringComparison.Ordinal) ||
            key.Value.EndsWith(".after", StringComparison.Ordinal))
            throw new KernelActionExecutionException(
                $"Action '{key.Value}' is not a registered Jobs root action.");
    }

    private static void EnsureJobsCheckpoint(SharpClawActionKey key, bool before)
    {
        var valid = SharpClawActionCatalog.Jobs.Contains(key) &&
                    key.Value.EndsWith(before ? ".before" : ".after", StringComparison.Ordinal);
        if (!valid)
            throw new KernelActionExecutionException(
                $"Action '{key.Value}' is not a registered Jobs {(before ? "before" : "after")} checkpoint.");
    }

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

    internal KernelExternalActionPolicy<TAction, TResult> CompileExternalAction<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        SidecarExternalActionDispatchAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(authority);
        var OwnerId = authority.SourceId;
        if (string.IsNullOrWhiteSpace(OwnerId))
            throw new KernelGraphCompilationException("An external action owner is required.");

        var registration = new ActionDefinitionRegistration<TAction, TResult>(descriptor, OwnerId);
        var definition = registration.Compile(_actionHooks, _serviceProvider, _compileOptions) as
            CompiledActionDefinition<TAction, TResult> ??
            throw new KernelGraphCompilationException(
                $"External action '{descriptor.Key.Value}' did not compile as its typed descriptor.");
        return KernelExternalActionPolicy<TAction, TResult>.Create(
            definition,
            ActionSnapshot,
            authority);
    }

    internal KernelExternalActionPolicy<JsonElement, JsonElement> CompileExternalSerializedAction(
        SidecarActionDefinition definition,
        SidecarActionDescriptorIdentity descriptorIdentity,
        ActionPipelineSnapshot snapshot,
        SidecarExternalActionDispatchAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(descriptorIdentity);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(authority);
        var OwnerId = authority.SourceId;
        if (string.IsNullOrWhiteSpace(OwnerId))
            throw new KernelGraphCompilationException("An external action owner is required.");

        var descriptor = new ActionDescriptor<JsonElement, JsonElement>(
            definition.ActionKey,
            definition.Version,
            definition.Category,
            definition.Capabilities,
            definition.ContainsSensitiveData,
            definition.HasIrreversibleEffects,
            definition.RepeatPolicy,
            definition.ContinuationPolicy,
            definition.DefaultTimeout)
        {
            InputSchema = definition.InputSchema,
            ResultSchema = definition.ResultSchema,
            SafePoints = definition.SafePoints,
            ProtocolVersionRange = definition.ProtocolVersionRange,
        };
        var contractIdentity = new KernelExternalActionContractIdentity(
            descriptorIdentity.InputTypeIdentity,
            descriptorIdentity.ResultTypeIdentity,
            definition.InputSchema,
            definition.ResultSchema,
            KernelSchemaIdentity.Action(definition, descriptorIdentity));
        var options = ExternalSerializedOptions(
            definition,
            descriptorIdentity,
            snapshot,
            OwnerId,
            contractIdentity.SchemaIdentity);
        var registration = new ActionDefinitionRegistration<JsonElement, JsonElement>(
            descriptor,
            OwnerId,
            contractIdentity);
        var compiled = registration.Compile(_actionHooks, _serviceProvider, options) as
            CompiledActionDefinition<JsonElement, JsonElement> ??
            throw new KernelGraphCompilationException(
                $"External action '{descriptor.Key.Value}' did not compile as a serialized descriptor.");
        var grant = SingleExternalGrant(snapshot, definition);
        var effectiveSensitiveApproved = !definition.ContainsSensitiveData || grant.SensitiveApproved;
        if (compiled.SnapshotCapabilities != grant.Capabilities ||
            compiled.SnapshotSensitiveApproved != effectiveSensitiveApproved)
        {
            throw new KernelGraphCompilationException(
                $"External action '{descriptor.Key.Value}' does not match its effective snapshot grant.");
        }
        return KernelExternalActionPolicy<JsonElement, JsonElement>.Create(
            compiled,
            ActionSnapshot,
            authority);
    }

    private KernelGraphCompileOptions ExternalSerializedOptions(
        SidecarActionDefinition definition,
        SidecarActionDescriptorIdentity descriptorIdentity,
        ActionPipelineSnapshot snapshot,
        string OwnerId,
        string schemaIdentity)
    {
        var grant = SingleExternalGrant(snapshot, definition);
        if (grant.Capabilities != definition.Capabilities)
        {
            throw new KernelGraphCompilationException(
                $"External action '{definition.ActionKey.Value}' does not have an exact capability grant.");
        }
        if (definition.ContainsSensitiveData && !grant.SensitiveApproved)
        {
            throw new KernelGraphCompilationException(
                $"Sensitive external action '{definition.ActionKey.Value}' lacks exact approval.");
        }
        if (!definition.ContainsSensitiveData && grant.SensitiveApproved)
        {
            throw new KernelGraphCompilationException(
                $"Non-sensitive external action '{definition.ActionKey.Value}' has an invalid sensitive approval.");
        }

        var registrationGrants = new Dictionary<
            string,
            IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(StringComparer.Ordinal);
        foreach (var registration in _compileOptions.ActionRegistrationCapabilityGrants ??
                 new Dictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>())
        {
            registrationGrants.Add(
                registration.Key,
                new Dictionary<string, ActionInterceptionCapabilities>(registration.Value, StringComparer.Ordinal));
        }
        var ownerGrants = registrationGrants.TryGetValue(OwnerId, out var existing)
            ? new Dictionary<string, ActionInterceptionCapabilities>(existing, StringComparer.Ordinal)
            : new Dictionary<string, ActionInterceptionCapabilities>(StringComparer.Ordinal);
        ownerGrants[definition.ActionKey.Value] = grant.Capabilities;
        registrationGrants[OwnerId] = ownerGrants;

        var actionGrants = new Dictionary<string, ActionInterceptionCapabilities>(
            _compileOptions.ActionCapabilityGrants
            ?? new Dictionary<string, ActionInterceptionCapabilities>(),
            StringComparer.Ordinal)
        {
            [definition.ActionKey.Value] = grant.Capabilities,
        };

        var approvals = (_compileOptions.SensitiveActionApprovals ?? []).ToList();
        if (definition.ContainsSensitiveData)
        {
            approvals.Add(new KernelSensitiveActionApproval(
                OwnerId,
                definition.ActionKey,
                definition.Version,
                descriptorIdentity.InputTypeIdentity,
                descriptorIdentity.ResultTypeIdentity,
                schemaIdentity));
        }

        return KernelGraphCompileOptions.Freeze(new KernelGraphCompileOptions
        {
            SupportedActionCapabilities = _compileOptions.SupportedActionCapabilities,
            SupportedEventCapabilities = _compileOptions.SupportedEventCapabilities,
            ActionCapabilityGrants = actionGrants,
            ActionRegistrationCapabilityGrants = registrationGrants,
            EventCapabilityGrants = _compileOptions.EventCapabilityGrants,
            EventRegistrationCapabilityGrants = _compileOptions.EventRegistrationCapabilityGrants,
            SensitiveActionApprovals = approvals,
            ExternalSensitiveActionApprovals = _compileOptions.ExternalSensitiveActionApprovals,
            SensitiveEventApprovals = _compileOptions.SensitiveEventApprovals,
            ExternalSensitiveEventApprovals = _compileOptions.ExternalSensitiveEventApprovals,
            MaximumActionDepth = _compileOptions.MaximumActionDepth,
        });
    }

    private static ActionCapabilityGrant SingleExternalGrant(
        ActionPipelineSnapshot snapshot,
        SidecarActionDefinition definition)
    {
        var grants = snapshot.ActionGrants
            .Where(grant => grant.ActionKey == definition.ActionKey && grant.ActionVersion == definition.Version)
            .ToArray();
        if (grants.Length != 1)
        {
            throw new KernelGraphCompilationException(
                $"External action '{definition.ActionKey.Value}' requires one exact snapshot grant.");
        }
        return grants[0];
    }

    internal ICompiledEventDefinition GetEvent(SharpClawEventKey key)
    {
        if (!_events.TryGetValue(key.Value, out var definition))
            throw new KernelActionExecutionException($"Event '{key.Value}' is not registered in the compiled graph.");
        return definition;
    }
}

internal sealed record KernelExternalActionPolicy<TAction, TResult>(
    CompiledActionDefinition<TAction, TResult> Definition,
    string SnapshotContractHash,
    string AuthorityBindingHash,
    string Identity)
{
    public static KernelExternalActionPolicy<TAction, TResult> Create(
        CompiledActionDefinition<TAction, TResult> definition,
        ActionPipelineSnapshot snapshot,
        SidecarExternalActionDispatchAuthority authority)
    {
        var authorityBindingHash = authority.EffectiveHostEntry.Authority.CanonicalBindingHash;
        return new(
            definition,
            snapshot.ContractHash,
            authorityBindingHash,
            ComputeIdentity(definition, snapshot.ContractHash, authority, authorityBindingHash));
    }

    public bool Matches(
        ActionPipelineSnapshot snapshot,
        SidecarExternalActionDispatchAuthority authority) =>
        string.Equals(SnapshotContractHash, snapshot.ContractHash, StringComparison.Ordinal) &&
        string.Equals(
            AuthorityBindingHash,
            authority.EffectiveHostEntry.Authority.CanonicalBindingHash,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            Identity,
            ComputeIdentity(Definition, snapshot.ContractHash, authority, AuthorityBindingHash),
            StringComparison.Ordinal);

    private static string ComputeIdentity(
        CompiledActionDefinition<TAction, TResult> definition,
        string snapshotContractHash,
        SidecarExternalActionDispatchAuthority authority,
        string authorityBindingHash)
    {
        var material = string.Join(
            "\n",
            [
                "external-policy-v1",
                snapshotContractHash,
                authority.SourceId,
                authority.GraphId,
                authority.Descriptor.DescriptorHash,
                authority.Action.ContentHash,
                authorityBindingHash,
                definition.Signature,
                KernelGraphHasher.StableScalar((int)definition.EffectiveCapabilities),
                KernelGraphHasher.StableScalar(definition.SnapshotSensitiveApproved)
            ]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
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
    IAnyActionInterceptor? BoundHandler,
    bool IsUntyped,
    HookOrdering Ordering,
    string OwnerId,
    string HandlerIdentity);

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
    object? BoundHandler,
    bool IsUntyped,
    KernelEventHookKind Kind,
    EventDelivery Delivery,
    HookOrdering Ordering,
    string OwnerId,
    string HandlerIdentity);

internal interface IActionDefinitionRegistration
{
    object DescriptorObject { get; }

    dynamic Descriptor { get; }

    SharpClawActionKey Key { get; }

    Type ActionType { get; }

    Type ResultType { get; }

    string OwnerId { get; }

    bool IsJobsAction { get; }

    bool MatchesJobsCatalog { get; }

    ICompiledActionDefinition Compile(
        IReadOnlyList<KernelActionHookRegistration> hooks,
        IServiceProvider serviceProvider,
        KernelGraphCompileOptions options);
}

internal sealed record KernelExternalActionContractIdentity(
    string ActionTypeIdentity,
    string ResultTypeIdentity,
    JsonSchemaReference InputSchema,
    JsonSchemaReference ResultSchema,
    string SchemaIdentity);

internal sealed class ActionDefinitionRegistration<TAction, TResult>(
    ActionDescriptor<TAction, TResult> descriptor,
    string ownerId,
    KernelExternalActionContractIdentity? externalContractIdentity = null) : IActionDefinitionRegistration
{
    public object DescriptorObject => descriptor;

    public dynamic Descriptor => descriptor;

    public SharpClawActionKey Key => descriptor.Key;

    public Type ActionType => typeof(TAction);

    public Type ResultType => typeof(TResult);

    public string OwnerId => ownerId;

    public bool IsJobsAction => descriptor.Key.Value.StartsWith("jobs.", StringComparison.Ordinal);

    public bool MatchesJobsCatalog =>
        !IsJobsAction ||
        (SharpClawActionCatalog.Jobs.Contains(descriptor.Key) &&
         KernelActionCatalog.DescriptorFor(descriptor.Key).MatchesDescriptor(descriptor));

    public ICompiledActionDefinition Compile(
        IReadOnlyList<KernelActionHookRegistration> hooks,
        IServiceProvider serviceProvider,
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
        effectiveCapabilities &= ResolveActionRegistrationGrant(
            descriptor,
            OwnerId,
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
                    $"Registration '{hook.OwnerId}' cannot receive action '{descriptor.Key.Value}' " +
                    "without the Inspect capability.");
            }
            var hookSensitiveApproved = ResolveSensitiveApproval(
                descriptor,
                hook.OwnerId,
                options,
                typeof(TAction),
                typeof(TResult),
                externalContractIdentity);
            if (hook.IsUntyped)
            {
                if (!typeof(IAnyActionInterceptor).IsAssignableFrom(hook.HandlerType))
                    throw new KernelGraphCompilationException(
                        $"'{hook.HandlerType.FullName}' does not implement IAnyActionInterceptor.");
                frames.Add(new AnyActionFrame<TAction, TResult>(
                    hook.BoundHandler
                    ?? (IAnyActionInterceptor)KernelServiceResolution.Resolve(hook.HandlerType, serviceProvider),
                    hook.TargetKind,
                    hook.Key,
                    hook.Category,
                    hook.Ordering,
                    hook.OwnerId,
                    hook.HandlerIdentity,
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
                    hook.OwnerId,
                    hook.HandlerIdentity,
                    hookCapabilities,
                    hookSensitiveApproved));
            }
        }

        var sensitiveApproved = ResolveSensitiveApproval(
            descriptor,
            OwnerId,
            options,
            typeof(TAction),
            typeof(TResult),
            externalContractIdentity);
        if (descriptor.ContainsSensitiveData && frames.Any(frame => !frame.SensitiveApproved))
            sensitiveApproved = false;
        if (descriptor.ContainsSensitiveData && !sensitiveApproved)
            throw new KernelGraphCompilationException($"Sensitive action '{descriptor.Key.Value}' lacks exact approval.");

        return new CompiledActionDefinition<TAction, TResult>(
            descriptor,
            OwnerId,
            frames,
            effectiveCapabilities,
            sensitiveApproved,
            externalContractIdentity?.InputSchema ??
                KernelSchemaIdentity.ActionInput(descriptor, typeof(TAction), typeof(TResult)),
            externalContractIdentity?.ResultSchema ??
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
        if (hook.OwnerId == KernelCapabilities.CoreOwner)
            return allowed;

        var requested = GetActionRegistrationGrant(descriptor.Key, hook.OwnerId, options);
        var unauthorized = requested & ~allowed;
        if (unauthorized != 0)
        {
            throw new KernelGraphCompilationException(
                $"Registration '{hook.OwnerId}' requests unauthorized effects '{unauthorized}' " +
                $"for action '{descriptor.Key.Value}'.");
        }
        return requested;
    }

    private static ActionInterceptionCapabilities ResolveActionRegistrationGrant<TActionValue, TResultValue>(
        ActionDescriptor<TActionValue, TResultValue> descriptor,
        string OwnerId,
        KernelGraphCompileOptions options)
    {
        if (OwnerId == KernelCapabilities.CoreOwner)
            return KernelCapabilities.AllActions;
        var registrationGrant = GetActionRegistrationGrant(descriptor.Key, OwnerId, options);
        var unauthorized = descriptor.Capabilities & ~registrationGrant;
        if (unauthorized != 0)
            throw new KernelGraphCompilationException(
                $"Registration '{OwnerId}' requests unauthorized effects '{unauthorized}' " +
                $"for action '{descriptor.Key.Value}'.");
        return registrationGrant;
    }

    private static ActionInterceptionCapabilities GetActionRegistrationGrant(
        SharpClawActionKey key,
        string OwnerId,
        KernelGraphCompileOptions options)
    {
        if (options.ActionRegistrationCapabilityGrants is not { } grants ||
            !grants.TryGetValue(OwnerId, out var registrationGrants) ||
            !registrationGrants.TryGetValue(key.Value, out var registrationGrant))
            throw new KernelGraphCompilationException(
                $"Registration '{OwnerId}' has no manifest grant for action '{key.Value}'.");
        return registrationGrant;
    }

    private static bool ResolveSensitiveApproval<TActionValue, TResultValue>(
        ActionDescriptor<TActionValue, TResultValue> descriptor,
        string SourceId,
        KernelGraphCompileOptions options,
        Type actionType,
        Type resultType,
        KernelExternalActionContractIdentity? externalContractIdentity = null)
    {
        if (!descriptor.ContainsSensitiveData)
            return true;
        if ((options.ExternalSensitiveActionApprovals ?? []).Any(approval =>
                approval.SourceId == SourceId &&
                approval.ActionKey == descriptor.Key &&
                approval.ActionVersion == descriptor.Version &&
                approval.InputSchema == descriptor.InputSchema &&
                approval.ResultSchema == descriptor.ResultSchema))
        {
            return true;
        }
        if (externalContractIdentity is not null)
        {
            return (options.SensitiveActionApprovals ?? []).Any(approval =>
                approval.SourceId == SourceId &&
                approval.ActionKey == descriptor.Key &&
                approval.ActionVersion == descriptor.Version &&
                approval.ActionType == externalContractIdentity.ActionTypeIdentity &&
                approval.ResultType == externalContractIdentity.ResultTypeIdentity &&
                approval.SchemaIdentity == externalContractIdentity.SchemaIdentity);
        }
        if (IsCanonicalCoreSensitiveAction(descriptor, SourceId, actionType, resultType))
            return true;
        var contractTypes = KernelSchemaIdentity.ActionTypes(
            descriptor,
            actionType,
            resultType);
        var schema = KernelSchemaIdentity.Action(descriptor, actionType, resultType);
        return (options.SensitiveActionApprovals ?? []).Any(approval =>
            approval.SourceId == SourceId &&
            approval.ActionKey == descriptor.Key &&
            approval.ActionVersion == descriptor.Version &&
            approval.ActionType == contractTypes.ActionType.AssemblyQualifiedName &&
            approval.ResultType == contractTypes.ResultType.AssemblyQualifiedName &&
            approval.SchemaIdentity == schema);
    }

    private static bool IsCanonicalCoreSensitiveAction<TActionValue, TResultValue>(
        ActionDescriptor<TActionValue, TResultValue> descriptor,
        string SourceId,
        Type actionType,
        Type resultType) =>
        SourceId == KernelCapabilities.CoreOwner &&
        descriptor.ContainsSensitiveData &&
        SharpClawActionCatalog.All.Contains(descriptor.Key) &&
        KernelActionCatalog.DescriptorFor(descriptor.Key).MatchesDescriptor(descriptor);

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
        IServiceProvider serviceProvider,
        KernelGraphCompileOptions options);
}

internal sealed class EventDefinitionRegistration<TEvent>(
    EventDescriptor<TEvent> descriptor,
    string OwnerId) : IEventDefinitionRegistration
{
    public dynamic Descriptor => descriptor;

    public SharpClawEventKey Key => descriptor.Key;

    public ICompiledEventDefinition Compile(
        IReadOnlyList<KernelEventHookRegistration> hooks,
        IServiceProvider serviceProvider,
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
        effectiveCapabilities &= ResolveEventRegistrationGrant(
            descriptor,
            OwnerId,
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
                    $"Registration '{hook.OwnerId}' cannot receive event '{descriptor.Key.Value}' " +
                    $"without the effective capabilities '{requiredCapabilities}'.");
            }
            var hookSensitiveApproved = ResolveSensitiveApproval(
                descriptor,
                hook.OwnerId,
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
                        hook.BoundHandler as IAnyEventInterceptor
                        ?? (IAnyEventInterceptor)KernelServiceResolution.Resolve(hook.HandlerType, serviceProvider),
                        hook.TargetKind,
                        hook.Key,
                        hook.Category,
                        hook.Ordering,
                        hook.OwnerId,
                        hook.HandlerIdentity,
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
                        hook.OwnerId,
                        hook.HandlerIdentity,
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
                        hook.BoundHandler as IAnyEventListener
                        ?? (IAnyEventListener)KernelServiceResolution.Resolve(hook.HandlerType, serviceProvider),
                        hook.Delivery,
                        hook.Ordering.Id,
                        hook.TargetKind,
                        hook.Key,
                        hook.Category,
                        hook.OwnerId,
                        hook.HandlerType,
                        hook.HandlerIdentity,
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
                        hook.OwnerId,
                        hook.HandlerType,
                        hook.HandlerIdentity,
                        hook.Ordering,
                        hookCapabilities,
                        hookSensitiveApproved));
                }
            }
        }

        var sensitiveApproved = ResolveSensitiveApproval(
            descriptor,
            OwnerId,
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
            OwnerId,
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
        if (hook.OwnerId == KernelCapabilities.CoreOwner)
            return allowed;

        var requested = GetEventRegistrationGrant(descriptor.Key, hook.OwnerId, options);
        var unauthorized = requested & ~allowed;
        if (unauthorized != 0)
        {
            throw new KernelGraphCompilationException(
                $"Registration '{hook.OwnerId}' requests unauthorized effects '{unauthorized}' " +
                $"for event '{descriptor.Key.Value}'.");
        }
        return requested;
    }

    private static EventInterceptionCapabilities ResolveEventRegistrationGrant<TEventValue>(
        EventDescriptor<TEventValue> descriptor,
        string OwnerId,
        KernelGraphCompileOptions options)
    {
        if (OwnerId == KernelCapabilities.CoreOwner)
            return EventInterceptionCapabilities.Inspect |
                   EventInterceptionCapabilities.Replace |
                   EventInterceptionCapabilities.Cancel |
                   EventInterceptionCapabilities.StopPropagation |
                   EventInterceptionCapabilities.Observe;
        var registrationGrant = GetEventRegistrationGrant(descriptor.Key, OwnerId, options);
        var unauthorized = descriptor.Capabilities & ~registrationGrant;
        if (unauthorized != 0)
            throw new KernelGraphCompilationException(
                $"Registration '{OwnerId}' requests unauthorized effects '{unauthorized}' " +
                $"for event '{descriptor.Key.Value}'.");
        return registrationGrant;
    }

    private static EventInterceptionCapabilities GetEventRegistrationGrant(
        SharpClawEventKey key,
        string OwnerId,
        KernelGraphCompileOptions options)
    {
        if (options.EventRegistrationCapabilityGrants is not { } grants ||
            !grants.TryGetValue(OwnerId, out var registrationGrants) ||
            !registrationGrants.TryGetValue(key.Value, out var registrationGrant))
            throw new KernelGraphCompilationException(
                $"Registration '{OwnerId}' has no manifest grant for event '{key.Value}'.");
        return registrationGrant;
    }

    private static bool ResolveSensitiveApproval<TEventValue>(
        EventDescriptor<TEventValue> descriptor,
        string SourceId,
        KernelGraphCompileOptions options,
        Type eventType)
    {
        if (!descriptor.ContainsSensitiveData)
            return true;
        var payloadSchema = KernelSchemaIdentity.EventPayload(descriptor, eventType);
        if ((options.ExternalSensitiveEventApprovals ?? []).Any(approval =>
                approval.SourceId == SourceId &&
                approval.EventKey == descriptor.Key &&
                approval.EventVersion == descriptor.Version &&
                approval.PayloadSchema == payloadSchema))
        {
            return true;
        }
        var schema = KernelSchemaIdentity.Event(descriptor, eventType);
        return (options.SensitiveEventApprovals ?? []).Any(approval =>
            approval.SourceId == SourceId &&
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

    public static string Action(
        SidecarActionDefinition definition,
        SidecarActionDescriptorIdentity descriptor)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!SidecarExternalActionDispatchAuthorityValidator.DescriptorMatchesDefinition(descriptor, definition))
            throw new KernelGraphCompilationException("The external action descriptor is incomplete or inconsistent.");

        var inputSchema = TypeSchema(
            "action.input",
            definition.ActionKey.Value,
            definition.Version,
            descriptor.InputTypeIdentity);
        var resultSchema = TypeSchema(
            "action.result",
            definition.ActionKey.Value,
            definition.Version,
            descriptor.ResultTypeIdentity);
        return string.Join(
            "|",
            definition.ActionKey.Value,
            KernelGraphHasher.StableScalar(definition.Version),
            definition.Category,
            descriptor.InputTypeIdentity,
            descriptor.ResultTypeIdentity,
            inputSchema.ContractName,
            KernelGraphHasher.StableScalar(inputSchema.Version),
            inputSchema.ContentHash,
            resultSchema.ContractName,
            KernelGraphHasher.StableScalar(resultSchema.Version),
            resultSchema.ContentHash,
            KernelGraphHasher.StableScalar(definition.ProtocolVersionRange.Minimum),
            KernelGraphHasher.StableScalar(definition.ProtocolVersionRange.Maximum),
            string.Join(",", definition.SafePoints.Select(value => KernelGraphHasher.StableScalar(value))));
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
            if (contract.InputPayloadType is { } input && contract.ResultPayloadType is { } result)
                return (input, result);
            throw new KernelGraphCompilationException(
                $"Action '{descriptor.Key.Value}' requires a registration-owned typed descriptor.");
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

    public static JsonSchemaReference EventPayload<TEvent>(
        EventDescriptor<TEvent> descriptor,
        Type eventType) =>
        TypeSchema("event.payload", descriptor.Key.Value, descriptor.Version, eventType);

    private static JsonSchemaReference TypeSchema(
        string role,
        string key,
        int version,
        Type type) =>
        TypeSchema(role, key, version, type.AssemblyQualifiedName ?? type.FullName ?? type.Name);

    private static JsonSchemaReference TypeSchema(
        string role,
        string key,
        int version,
        string typeIdentity)
    {
        var contractName = $"sharpclaw.kernel.{role}.{key}";
        var identity = $"{contractName}|{version}|{typeIdentity}";
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

    string OwnerId { get; }
}

internal sealed class CompiledActionDefinition<TAction, TResult>(
    ActionDescriptor<TAction, TResult> descriptor,
    string OwnerId,
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
            $"{KernelGraphHasher.StableScalar(frame.Ordering.FailurePolicy)}|{frame.OwnerId}|" +
            $"{frame.HandlerIdentity}|{frame.IsUntyped}|" +
            $"{KernelGraphHasher.StableScalar((int)frame.EffectiveCapabilities)}|" +
            $"{KernelGraphHasher.StableScalar(frame.SensitiveApproved)}")]);

    public string OwnerId { get; } = OwnerId;

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

    string OwnerId { get; }

    Type HandlerType { get; }

    string HandlerIdentity { get; }

    ActionInterceptionCapabilities EffectiveCapabilities { get; }

    bool SensitiveApproved { get; }
}

internal sealed class TypedActionFrame<TAction, TResult>(
    IActionInterceptor<TAction, TResult> interceptor,
    KernelHookTargetKind targetKind,
    SharpClawActionKey? targetKey,
    string? targetCategory,
    HookOrdering ordering,
    string OwnerId,
    string handlerIdentity,
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

    public string OwnerId { get; } = OwnerId;

    public Type HandlerType => Interceptor.GetType();

    public string HandlerIdentity { get; } = handlerIdentity;

    public ActionInterceptionCapabilities EffectiveCapabilities { get; } = effectiveCapabilities;

    public bool SensitiveApproved { get; } = sensitiveApproved;
}

internal sealed class AnyActionFrame<TAction, TResult>(
    IAnyActionInterceptor interceptor,
    KernelHookTargetKind targetKind,
    SharpClawActionKey? targetKey,
    string? targetCategory,
    HookOrdering ordering,
    string OwnerId,
    string handlerIdentity,
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

    public string OwnerId { get; } = OwnerId;

    public Type HandlerType => Interceptor.GetType();

    public string HandlerIdentity { get; } = handlerIdentity;

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

    string OwnerId { get; }
}

internal sealed class CompiledEventDefinition<TEvent>(
    EventDescriptor<TEvent> descriptor,
    string OwnerId,
    IReadOnlyList<IEventFrame<TEvent>> interceptors,
    IReadOnlyList<KernelEventListener<TEvent>> listeners,
    EventInterceptionCapabilities effectiveCapabilities,
    bool sensitiveApproved,
    JsonSchemaReference payloadSchema) : ICompiledEventDefinition
{
    public EventDescriptor<TEvent> Descriptor { get; } = descriptor;

    dynamic ICompiledEventDefinition.Descriptor => Descriptor;

    public string OwnerId { get; } = OwnerId;

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
            $"{KernelGraphHasher.StableScalar(frame.Ordering.FailurePolicy)}|{frame.OwnerId}|" +
            $"{frame.HandlerIdentity}|{frame.IsUntyped}|" +
            $"{KernelGraphHasher.StableScalar((int)frame.EffectiveCapabilities)}|" +
            $"{KernelGraphHasher.StableScalar(frame.SensitiveApproved)}"),
            ..Listeners.Select(listener =>
                $"l|{KernelGraphHasher.StableScalar(listener.TargetKind)}|{listener.TargetKey?.Value}|" +
                $"{listener.TargetCategory}|{listener.Id}|{listener.OwnerId}|" +
                $"{listener.HandlerIdentity}|{KernelGraphHasher.StableScalar(listener.Delivery)}|" +
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

    string OwnerId { get; }

    Type HandlerType { get; }

    string HandlerIdentity { get; }

    EventInterceptionCapabilities EffectiveCapabilities { get; }

    bool SensitiveApproved { get; }
}

internal sealed class TypedEventFrame<TEvent>(
    IEventInterceptor<TEvent> interceptor,
    KernelHookTargetKind targetKind,
    SharpClawEventKey? targetKey,
    string? targetCategory,
    HookOrdering ordering,
    string OwnerId,
    string handlerIdentity,
    EventInterceptionCapabilities effectiveCapabilities,
    bool sensitiveApproved) : IEventFrame<TEvent>
{
    public IEventInterceptor<TEvent> Interceptor { get; } = interceptor;

    public bool IsUntyped => false;

    public KernelHookTargetKind TargetKind { get; } = targetKind;

    public SharpClawEventKey? TargetKey { get; } = targetKey;

    public string? TargetCategory { get; } = targetCategory;

    public HookOrdering Ordering { get; } = ordering;

    public string OwnerId { get; } = OwnerId;

    public Type HandlerType => Interceptor.GetType();

    public string HandlerIdentity { get; } = handlerIdentity;

    public EventInterceptionCapabilities EffectiveCapabilities { get; } = effectiveCapabilities;

    public bool SensitiveApproved { get; } = sensitiveApproved;
}

internal sealed class AnyEventFrame<TEvent>(
    IAnyEventInterceptor interceptor,
    KernelHookTargetKind targetKind,
    SharpClawEventKey? targetKey,
    string? targetCategory,
    HookOrdering ordering,
    string OwnerId,
    string handlerIdentity,
    EventInterceptionCapabilities effectiveCapabilities,
    bool sensitiveApproved) : IEventFrame<TEvent>
{
    public IAnyEventInterceptor Interceptor { get; } = interceptor;

    public bool IsUntyped => true;

    public KernelHookTargetKind TargetKind { get; } = targetKind;

    public SharpClawEventKey? TargetKey { get; } = targetKey;

    public string? TargetCategory { get; } = targetCategory;

    public HookOrdering Ordering { get; } = ordering;

    public string OwnerId { get; } = OwnerId;

    public Type HandlerType => Interceptor.GetType();

    public string HandlerIdentity { get; } = handlerIdentity;

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
    string OwnerId,
    Type HandlerType,
    string HandlerIdentity,
    HookOrdering Ordering,
    EventInterceptionCapabilities EffectiveCapabilities,
    bool SensitiveApproved);
