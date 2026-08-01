using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
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

    public KernelGraphBuilder(bool includeStandardDefinitions = true)
    {
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

    internal void AddActionHook(KernelActionHookRegistration registration) => _actionHooks.Add(registration);

    internal void AddEventHook(KernelEventHookRegistration registration) => _eventHooks.Add(registration);

    private void AddStandardDefinitions()
    {
        var keys = SharpClawActionCatalog.Kernel
            .DistinctBy(key => key.Value, StringComparer.Ordinal)
            .ToArray();

        foreach (var key in keys)
        {
            Add(
                new ActionDescriptor<KernelActionEnvelope, object>(
                    key,
                    1,
                    KernelActionCatalog.CategoryFor(key),
                    KernelCapabilities.AllActions,
                    false,
                    false,
                    KernelCapabilities.NoRepeat,
                    KernelCapabilities.DurableContinuation,
                    TimeSpan.FromSeconds(30)),
                "core");
        }
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

        var actions = CompileActions(builder, serviceProvider, options);
        var events = CompileEvents(builder, serviceProvider, options);
        var tools = CompileTools(builder);
        var actionGrants = actions.Values
            .Select(action => new ActionCapabilityGrant(
                action.Key,
                action.Version,
                action.EffectiveCapabilities,
                action.SensitiveApproved,
                false))
            .ToArray();
        var eventGrants = events.Values
            .Select(eventDefinition => new EventCapabilityGrant(
                eventDefinition.Key,
                eventDefinition.Version,
                eventDefinition.EffectiveCapabilities,
                eventDefinition.SensitiveApproved,
                false))
            .ToArray();
        var contractHash = ComputeContractHash(actions.Values, events.Values, tools, options);
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

    private static IReadOnlyList<KernelToolRegistration> CompileTools(KernelGraphBuilder builder)
    {
        var result = new List<KernelToolRegistration>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in builder.Tools)
        {
            if (!names.Add(tool.Descriptor.Name))
                throw new KernelGraphCompilationException($"Tool '{tool.Descriptor.Name}' is registered more than once.");
            result.Add(tool);
        }

        return new ReadOnlyCollection<KernelToolRegistration>(result);
    }

    private static string ComputeContractHash(
        IEnumerable<ICompiledActionDefinition> actions,
        IEnumerable<ICompiledEventDefinition> events,
        IEnumerable<KernelToolRegistration> tools,
        KernelGraphCompileOptions options)
    {
        var content = string.Join(
            "\n",
            actions.OrderBy(action => action.Key.Value, StringComparer.Ordinal)
                .Select(action =>
                    $"a|{action.Key.Value}|{action.Version}|{action.Category}|{(int)action.Capabilities}|" +
                    $"{(int)action.EffectiveCapabilities}|{action.ContainsSensitiveData}|{action.SensitiveApproved}|" +
                    $"{action.DescriptorObject}|{action.OwnerModuleId}|{action.Signature}"),
            events.OrderBy(eventDefinition => eventDefinition.Key.Value, StringComparer.Ordinal)
                .Select(eventDefinition =>
                    $"e|{eventDefinition.Key.Value}|{eventDefinition.Version}|{eventDefinition.Category}|" +
                    $"{(int)eventDefinition.Capabilities}|{(int)eventDefinition.EffectiveCapabilities}|" +
                    $"{eventDefinition.ContainsSensitiveData}|{eventDefinition.SensitiveApproved}|" +
                    $"{eventDefinition.OwnerModuleId}|{eventDefinition.Signature}"),
            tools.OrderBy(tool => tool.Descriptor.Name, StringComparer.Ordinal)
                .Select(tool => $"t|{tool.Descriptor.Name}|{tool.Descriptor.Version}|{tool.OwnerModuleId}|{tool.HandlerType.FullName}"),
            options.ActionCapabilityGrants?.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"ag|{pair.Key}|{(int)pair.Value}") ?? [],
            options.EventCapabilityGrants?.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"eg|{pair.Key}|{(int)pair.Value}") ?? [],
            options.ApprovedSensitiveActions.OrderBy(key => key, StringComparer.Ordinal).Select(key => $"sa|{key}"),
            options.ApprovedSensitiveEvents.OrderBy(key => key, StringComparer.Ordinal).Select(key => $"se|{key}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}

public sealed class KernelGraph
{
    private readonly IReadOnlyDictionary<string, ICompiledActionDefinition> _actions;
    private readonly IReadOnlyDictionary<string, ICompiledEventDefinition> _events;

    internal KernelGraph(
        IReadOnlyDictionary<string, ICompiledActionDefinition> actions,
        IReadOnlyDictionary<string, ICompiledEventDefinition> events,
        IReadOnlyList<KernelToolRegistration> tools,
        ActionPipelineSnapshot actionSnapshot,
        ChatPipelineSnapshot chatSnapshot,
        int maximumActionDepth)
    {
        _actions = new ReadOnlyDictionary<string, ICompiledActionDefinition>(
            new Dictionary<string, ICompiledActionDefinition>(actions, StringComparer.Ordinal));
        _events = new ReadOnlyDictionary<string, ICompiledEventDefinition>(
            new Dictionary<string, ICompiledEventDefinition>(events, StringComparer.Ordinal));
        Tools = tools;
        ActionSnapshot = actionSnapshot;
        ChatSnapshot = chatSnapshot;
        MaximumActionDepth = maximumActionDepth;
    }

    public ActionPipelineSnapshot ActionSnapshot { get; }

    public ChatPipelineSnapshot ChatSnapshot { get; }

    public IReadOnlyList<KernelToolRegistration> Tools { get; }

    public int MaximumActionDepth { get; }

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
        var sensitiveApproved = !descriptor.ContainsSensitiveData ||
            options.ApprovedSensitiveActions.Contains(descriptor.Key.Value);
        if (!sensitiveApproved)
            throw new KernelGraphCompilationException($"Sensitive action '{descriptor.Key.Value}' lacks approval.");
        var effectiveCapabilities = descriptor.Capabilities;
        if (options.ActionCapabilityGrants?.TryGetValue(descriptor.Key.Value, out var grant) == true)
            effectiveCapabilities &= grant;

        var matching = hooks
            .Where(hook => Matches(hook.TargetKind, hook.Key, hook.Category, descriptor.Key, descriptor.Category))
            .ToArray();
        var ordered = KernelHookOrdering.Order(matching);
        var frames = new List<IActionFrame<TAction, TResult>>();
        foreach (var hook in ordered)
        {
            if (hook.IsUntyped)
            {
                if (!typeof(IAnyActionInterceptor).IsAssignableFrom(hook.HandlerType))
                    throw new KernelGraphCompilationException(
                        $"'{hook.HandlerType.FullName}' does not implement IAnyActionInterceptor.");
                frames.Add(new AnyActionFrame<TAction, TResult>(
                    (IAnyActionInterceptor)KernelServiceResolution.Resolve(hook.HandlerType, serviceProvider),
                    hook.Ordering,
                    hook.OwnerModuleId));
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
                    hook.Ordering,
                    hook.OwnerModuleId));
            }
        }

        return new CompiledActionDefinition<TAction, TResult>(
            descriptor,
            ownerModuleId,
            frames,
            effectiveCapabilities,
            sensitiveApproved);
    }

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
        var sensitiveApproved = !descriptor.ContainsSensitiveData ||
            options.ApprovedSensitiveEvents.Contains(descriptor.Key.Value);
        if (!sensitiveApproved)
            throw new KernelGraphCompilationException($"Sensitive event '{descriptor.Key.Value}' lacks approval.");
        var effectiveCapabilities = descriptor.Capabilities;
        if (options.EventCapabilityGrants?.TryGetValue(descriptor.Key.Value, out var grant) == true)
            effectiveCapabilities &= grant;

        var matching = hooks
            .Where(hook => Matches(hook.TargetKind, hook.Key, hook.Category, descriptor.Key, descriptor.Category))
            .ToArray();
        var ordered = KernelHookOrdering.Order(matching);
        var interceptors = new List<IEventFrame<TEvent>>();
        var listeners = new List<KernelEventListener<TEvent>>();
        foreach (var hook in ordered)
        {
            if (hook.Kind == KernelEventHookKind.Interceptor)
            {
                if (hook.IsUntyped)
                {
                    if (!typeof(IAnyEventInterceptor).IsAssignableFrom(hook.HandlerType))
                        throw new KernelGraphCompilationException(
                            $"'{hook.HandlerType.FullName}' does not implement IAnyEventInterceptor.");
                    interceptors.Add(new AnyEventFrame<TEvent>(
                        (IAnyEventInterceptor)KernelServiceResolution.Resolve(hook.HandlerType, serviceProvider),
                        hook.Ordering,
                        hook.OwnerModuleId));
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
                        hook.Ordering,
                        hook.OwnerModuleId));
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
                        hook.OwnerModuleId,
                        hook.HandlerType,
                        hook.Ordering));
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
                        hook.OwnerModuleId,
                        hook.HandlerType,
                        hook.Ordering));
                }
            }
        }

        return new CompiledEventDefinition<TEvent>(
            descriptor,
            ownerModuleId,
            interceptors,
            listeners,
            effectiveCapabilities,
            sensitiveApproved);
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

internal interface ICompiledActionDefinition
{
    object DescriptorObject { get; }

    dynamic Descriptor { get; }

    SharpClawActionKey Key { get; }

    int Version { get; }

    string Category { get; }

    ActionInterceptionCapabilities Capabilities { get; }

    ActionInterceptionCapabilities EffectiveCapabilities { get; }

    bool ContainsSensitiveData { get; }

    bool SensitiveApproved { get; }

    string Signature { get; }

    Type ActionType { get; }

    Type ResultType { get; }

    string OwnerModuleId { get; }
}

internal sealed class CompiledActionDefinition<TAction, TResult>(
    ActionDescriptor<TAction, TResult> descriptor,
    string ownerModuleId,
    IReadOnlyList<IActionFrame<TAction, TResult>> frames,
    ActionInterceptionCapabilities effectiveCapabilities,
    bool sensitiveApproved) : ICompiledActionDefinition
{
    public ActionDescriptor<TAction, TResult> Descriptor { get; } = descriptor;

    dynamic ICompiledActionDefinition.Descriptor => Descriptor;

    public object DescriptorObject => Descriptor;

    public SharpClawActionKey Key => Descriptor.Key;

    public int Version => Descriptor.Version;

    public string Category => Descriptor.Category;

    public ActionInterceptionCapabilities Capabilities => Descriptor.Capabilities;

    public ActionInterceptionCapabilities EffectiveCapabilities { get; } = effectiveCapabilities;

    public bool ContainsSensitiveData => Descriptor.ContainsSensitiveData;

    public bool SensitiveApproved { get; } = sensitiveApproved;

    public string Signature => string.Join(
        ",",
        [
            $"descriptor|{Descriptor.HasIrreversibleEffects}|{Descriptor.RepeatPolicy}|" +
            $"{Descriptor.ContinuationPolicy}|{Descriptor.DefaultTimeout}|" +
            $"{string.Join(",", Descriptor.SafePoints)}|{Descriptor.ProtocolVersionRange}",
            ..Frames.Select(frame =>
            $"{frame.Ordering.Id}|{frame.OwnerModuleId}|{frame.HandlerType.FullName}|" +
            $"{frame.Ordering.Timeout}|{frame.Ordering.FailurePolicy}")]);

    public string OwnerModuleId { get; } = ownerModuleId;

    public IReadOnlyList<IActionFrame<TAction, TResult>> Frames { get; } = frames;

    public Type ActionType => typeof(TAction);

    public Type ResultType => typeof(TResult);
}

internal interface IActionFrame<TAction, TResult>
{
    bool IsUntyped { get; }

    HookOrdering Ordering { get; }

    string OwnerModuleId { get; }

    Type HandlerType { get; }
}

internal sealed class TypedActionFrame<TAction, TResult>(
    IActionInterceptor<TAction, TResult> interceptor,
    HookOrdering ordering,
    string ownerModuleId)
    : IActionFrame<TAction, TResult>
{
    public IActionInterceptor<TAction, TResult> Interceptor { get; } = interceptor;

    public bool IsUntyped => false;

    public HookOrdering Ordering { get; } = ordering;

    public string OwnerModuleId { get; } = ownerModuleId;

    public Type HandlerType => Interceptor.GetType();
}

internal sealed class AnyActionFrame<TAction, TResult>(
    IAnyActionInterceptor interceptor,
    HookOrdering ordering,
    string ownerModuleId)
    : IActionFrame<TAction, TResult>
{
    public IAnyActionInterceptor Interceptor { get; } = interceptor;

    public bool IsUntyped => true;

    public HookOrdering Ordering { get; } = ordering;

    public string OwnerModuleId { get; } = ownerModuleId;

    public Type HandlerType => Interceptor.GetType();
}

internal interface ICompiledEventDefinition
{
    dynamic Descriptor { get; }

    SharpClawEventKey Key { get; }

    int Version { get; }

    string Category { get; }

    EventInterceptionCapabilities Capabilities { get; }

    EventInterceptionCapabilities EffectiveCapabilities { get; }

    bool ContainsSensitiveData { get; }

    bool SensitiveApproved { get; }

    string Signature { get; }

    Type EventType { get; }

    string OwnerModuleId { get; }
}

internal sealed class CompiledEventDefinition<TEvent>(
    EventDescriptor<TEvent> descriptor,
    string ownerModuleId,
    IReadOnlyList<IEventFrame<TEvent>> interceptors,
    IReadOnlyList<KernelEventListener<TEvent>> listeners,
    EventInterceptionCapabilities effectiveCapabilities,
    bool sensitiveApproved) : ICompiledEventDefinition
{
    public EventDescriptor<TEvent> Descriptor { get; } = descriptor;

    dynamic ICompiledEventDefinition.Descriptor => Descriptor;

    public string OwnerModuleId { get; } = ownerModuleId;

    public SharpClawEventKey Key => Descriptor.Key;

    public int Version => Descriptor.Version;

    public string Category => Descriptor.Category;

    public EventInterceptionCapabilities Capabilities => Descriptor.Capabilities;

    public EventInterceptionCapabilities EffectiveCapabilities { get; } = effectiveCapabilities;

    public bool ContainsSensitiveData => Descriptor.ContainsSensitiveData;

    public bool SensitiveApproved { get; } = sensitiveApproved;

    public string Signature => string.Join(
        ",",
        [
            $"descriptor|{Descriptor.DurableByDefault}|{Descriptor.DeliveryClasses}|{Descriptor.ProtocolVersionRange}",
            ..Interceptors.Select(frame =>
            $"i|{frame.Ordering.Id}|{frame.OwnerModuleId}|{frame.HandlerType.FullName}|" +
            $"{frame.Ordering.Timeout}|{frame.Ordering.FailurePolicy}"),
            ..Listeners.Select(listener =>
                $"l|{listener.Id}|{listener.OwnerModuleId}|{listener.HandlerType.FullName}|{listener.Delivery}|" +
                $"{listener.Ordering.Timeout}|{listener.Ordering.FailurePolicy}")]);

    public IReadOnlyList<IEventFrame<TEvent>> Interceptors { get; } = interceptors;

    public IReadOnlyList<KernelEventListener<TEvent>> Listeners { get; } = listeners;

    public Type EventType => typeof(TEvent);
}

internal interface IEventFrame<TEvent>
{
    bool IsUntyped { get; }

    HookOrdering Ordering { get; }

    string OwnerModuleId { get; }

    Type HandlerType { get; }
}

internal sealed class TypedEventFrame<TEvent>(
    IEventInterceptor<TEvent> interceptor,
    HookOrdering ordering,
    string ownerModuleId) : IEventFrame<TEvent>
{
    public IEventInterceptor<TEvent> Interceptor { get; } = interceptor;

    public bool IsUntyped => false;

    public HookOrdering Ordering { get; } = ordering;

    public string OwnerModuleId { get; } = ownerModuleId;

    public Type HandlerType => Interceptor.GetType();
}

internal sealed class AnyEventFrame<TEvent>(
    IAnyEventInterceptor interceptor,
    HookOrdering ordering,
    string ownerModuleId) : IEventFrame<TEvent>
{
    public IAnyEventInterceptor Interceptor { get; } = interceptor;

    public bool IsUntyped => true;

    public HookOrdering Ordering { get; } = ordering;

    public string OwnerModuleId { get; } = ownerModuleId;

    public Type HandlerType => Interceptor.GetType();
}

internal sealed record KernelEventListener<TEvent>(
    object Listener,
    EventDelivery Delivery,
    string Id,
    string OwnerModuleId,
    Type HandlerType,
    HookOrdering Ordering);

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
        if (!_moduleIds.Add(module.Identity.Id))
            throw new KernelGraphCompilationException($"Module '{module.Identity.Id}' is registered more than once.");
        _modules.Add(module);
        module.Configure(new KernelModuleBuilder(_graphBuilder, module.Identity));
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
                async (_, ct) =>
                {
                    await module.StartAsync(context, ct);
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
    public KernelModuleBuilder(KernelGraphBuilder graphBuilder, ModuleIdentity identity)
    {
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
}

public sealed class KernelToolContributionBuilder(
    KernelGraphBuilder builder,
    string ownerModuleId) : IToolContributionBuilder
{
    public void Add<THandler>(ToolDescriptor descriptor) where THandler : IToolHandler =>
        builder.AddTool<THandler>(descriptor, ownerModuleId);
}

public sealed class KernelServiceCollection : List<ServiceDescriptor>, IServiceCollection;

public sealed record KernelModuleContractDeclaration(
    string OwnerModuleId,
    Type ContractType,
    string Name,
    int MinimumVersion,
    int MaximumVersion,
    bool IsRequired);

public sealed class KernelModuleContractBuilder(string ownerModuleId) : IModuleContractBuilder
{
    private readonly List<KernelModuleContractDeclaration> _declarations = [];

    public IReadOnlyList<KernelModuleContractDeclaration> Declarations => _declarations;

    public void Export<T>(string name, int version, int maximumVersion) =>
        _declarations.Add(new KernelModuleContractDeclaration(
            ownerModuleId,
            typeof(T),
            name,
            version,
            maximumVersion,
            false));

    public void Require<T>(string name, int minimumVersion, bool optional) =>
        _declarations.Add(new KernelModuleContractDeclaration(
            ownerModuleId,
            typeof(T),
            name,
            minimumVersion,
            minimumVersion,
            !optional));
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

    public Type? ConversationResolver { get; private set; }

    public void AddContextContributor<TContributor>() where TContributor : IChatContextContributor =>
        _contextContributors.Add(typeof(TContributor));

    public void UseChatProfileResolver<TResolver>(ExclusiveRegistration registration)
        where TResolver : IChatProfileResolver
    {
        if (ProfileResolver is not null)
            throw new KernelGraphCompilationException("A chat profile resolver was registered more than once.");
        ProfileResolver = typeof(TResolver);
    }

    public void UseConversationResolver<TResolver>(ExclusiveRegistration registration)
        where TResolver : IConversationResolver
    {
        if (ConversationResolver is not null)
            throw new KernelGraphCompilationException("A conversation resolver was registered more than once.");
        ConversationResolver = typeof(TResolver);
    }
}
