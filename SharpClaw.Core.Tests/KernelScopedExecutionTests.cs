using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelScopedExecutionTests
{
    [Fact]
    public async Task Scoped_behaviors_use_one_bounded_instance_per_execution()
    {
        var actionKey = new SharpClawActionKey("scope.action");
        var eventKey = new SharpClawEventKey("scope.event");
        var tool = new ToolDescriptor(
            "scope_tool",
            "Tests one scoped tool.",
            JsonSerializer.SerializeToElement(new { type = "object" }));
        var action = new ActionDescriptor<KernelActionEnvelope, object>(
            actionKey,
            1,
            "scope",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap,
            false,
            false,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "scope"),
            null,
            TimeSpan.FromSeconds(10));
        var evt = new EventDescriptor<ScopeEvent>(
            eventKey,
            1,
            "scope",
            EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
            false,
            false);

        var services = new ServiceCollection();
        var capture = new ScopeCapture();
        services.AddSingleton(capture);
        services.AddAction("scope", action);
        services.AddEvent("scope", evt);
        services.AddScoped<ScopedActionInterceptor>();
        services.AddScoped<ScopedEventListener>();
        services.AddScoped<ScopedToolHandler>();
        services.AddScoped<IChatContextContributor, ScopedContextContributor>();
        services.AddSingleton(new ActionHookBinding(
            "scope",
            BehaviorTargetKind.Exact,
            actionKey,
            null,
            typeof(ScopedActionInterceptor),
            false,
            new HookOrdering("scope.action"),
            typeof(ScopedActionInterceptor).AssemblyQualifiedName!));
        services.AddSingleton(new EventHookBinding(
            "scope",
            BehaviorTargetKind.Exact,
            eventKey,
            null,
            typeof(ScopedEventListener),
            false,
            EventHookKind.Listener,
            EventDelivery.Inline,
            new HookOrdering("scope.event"),
            typeof(ScopedEventListener).AssemblyQualifiedName!));
        services.AddSingleton(new ToolHandlerBinding(
            "scope",
            tool,
            typeof(ScopedToolHandler),
            typeof(ScopedToolHandler).AssemblyQualifiedName!));

        var graph = services.Compile(new KernelGraphCompileOptions
        {
            ActionRegistrationCapabilityGrants = new Dictionary<
                string,
                IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
            {
                ["scope"] = new Dictionary<string, ActionInterceptionCapabilities>
                {
                    [actionKey.Value] =
                        ActionInterceptionCapabilities.Inspect |
                        ActionInterceptionCapabilities.Wrap,
                },
            },
            EventRegistrationCapabilityGrants = new Dictionary<
                string,
                IReadOnlyDictionary<string, EventInterceptionCapabilities>>
            {
                ["scope"] = new Dictionary<string, EventInterceptionCapabilities>
                {
                    [eventKey.Value] =
                        EventInterceptionCapabilities.Inspect |
                        EventInterceptionCapabilities.Observe,
                },
            },
        });
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var eventDispatcher = new KernelEventDispatcher(graph);
        var tools = new UnifiedToolPipeline(graph, dispatcher);
        var chat = graph.CreateChatContextAssembler(dispatcher);

        for (var index = 0; index < 2; index++)
        {
            await dispatcher.RunRequiredAsync(
                action,
                new KernelActionEnvelope(actionKey, index),
                static (context, _) => ValueTask.FromResult<object>(context.Action.Payload!),
                graph.ActionSnapshot,
                CancellationToken.None);
            await eventDispatcher.DispatchAsync(evt, new ScopeEvent(index), graph.ActionSnapshot);
            await tools.InvokeAsync(
                KernelTestExecution.CreateToolInvocation("scope_tool"),
                CancellationToken.None);
            await chat.BuildAsync(
                new ChatContextRequest(
                    Guid.NewGuid(),
                    new ChatProfile("provider", Guid.NewGuid()),
                    []),
                CancellationToken.None);
        }

        Assert.Equal(2, capture.ActionInstances.Count);
        Assert.Equal(2, capture.EventInstances.Count);
        Assert.Equal(2, capture.ToolInstances.Count);
        Assert.Equal(2, capture.ChatInstances.Count);
        Assert.Equal(2, capture.Disposals["action"]);
        Assert.Equal(2, capture.Disposals["event"]);
        Assert.Equal(2, capture.Disposals["tool"]);
        Assert.Equal(2, capture.Disposals["chat"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detached_action_keeps_its_scope_until_nested_work_completes(
        bool cancelCaller)
    {
        var rootKey = new SharpClawActionKey("scope.action.detached");
        var nestedKey = new SharpClawActionKey("scope.action.nested");
        var root = Action(rootKey);
        var nested = Action(nestedKey);
        var probe = new DetachedScopeProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddAction("scope", root);
        services.AddAction("scope", nested);
        services.AddScoped<DetachedScopedService>();
        services.AddScoped<DetachedActionInterceptor>();
        services.AddScoped<NestedActionInterceptor>();
        services.AddSingleton(new ActionHookBinding(
            "scope",
            BehaviorTargetKind.Exact,
            rootKey,
            null,
            typeof(DetachedActionInterceptor),
            false,
            new HookOrdering(
                "scope.action.detached",
                Timeout: cancelCaller ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(20)),
            typeof(DetachedActionInterceptor).AssemblyQualifiedName!));
        services.AddSingleton(new ActionHookBinding(
            "scope",
            BehaviorTargetKind.Exact,
            nestedKey,
            null,
            typeof(NestedActionInterceptor),
            false,
            new HookOrdering("scope.action.nested"),
            typeof(NestedActionInterceptor).AssemblyQualifiedName!));
        var graph = services.Compile(ActionOptions(rootKey, nestedKey));
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            new StoreBackedContinuationHost(new TestDurableContinuationStore()));
        probe.NestedOperation = async () =>
        {
            var nestedOutcome = await dispatcher.RunAsync(
                nested,
                new KernelActionEnvelope(nestedKey, "nested"),
                static (_, _) => ValueTask.FromResult<object>("nested"),
                graph.ActionSnapshot,
                CancellationToken.None);
            Assert.Equal(ActionOutcomeKind.Completed, nestedOutcome.Kind);
        };
        using var callerCancellation = new CancellationTokenSource();

        var dispatch = dispatcher.RunAsync(
            root,
            new KernelActionEnvelope(rootKey, "root"),
            static (_, _) => ValueTask.FromResult<object>("root"),
            graph.ActionSnapshot,
            callerCancellation.Token).AsTask();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
            callerCancellation.Cancel();

        var outcome = await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ActionOutcomeKind.Uncertain, outcome.Kind);
        Assert.Equal(0, probe.DisposalCount);

        probe.Release.TrySetResult(true);
        await probe.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForDisposalsAsync(probe, 1);
        Assert.Equal(probe.RootInstances.Single(), probe.NestedInstances.Single());

        probe.ReleasePostCompletion.TrySetResult(true);
        await probe.PostCompletionOperation.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForDisposalsAsync(probe, 2);
        Assert.NotEqual(probe.RootInstances.Single(), probe.NestedInstances.Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detached_event_keeps_its_scope_until_nested_work_completes(
        bool cancelCaller)
    {
        var rootKey = new SharpClawEventKey("scope.event.detached");
        var nestedKey = new SharpClawEventKey("scope.event.nested");
        var root = Event(rootKey);
        var nested = Event(nestedKey);
        var probe = new DetachedScopeProbe();
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddEvent("scope", root);
        services.AddEvent("scope", nested);
        services.AddScoped<DetachedScopedService>();
        services.AddScoped<DetachedEventInterceptor>();
        services.AddScoped<NestedEventInterceptor>();
        services.AddSingleton(new EventHookBinding(
            "scope",
            BehaviorTargetKind.Exact,
            rootKey,
            null,
            typeof(DetachedEventInterceptor),
            false,
            EventHookKind.Interceptor,
            EventDelivery.Inline,
            new HookOrdering(
                "scope.event.detached",
                Timeout: cancelCaller ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(20)),
            typeof(DetachedEventInterceptor).AssemblyQualifiedName!));
        services.AddSingleton(new EventHookBinding(
            "scope",
            BehaviorTargetKind.Exact,
            nestedKey,
            null,
            typeof(NestedEventInterceptor),
            false,
            EventHookKind.Interceptor,
            EventDelivery.Inline,
            new HookOrdering("scope.event.nested"),
            typeof(NestedEventInterceptor).AssemblyQualifiedName!));
        var graph = services.Compile(EventOptions(rootKey, nestedKey));
        var dispatcher = new KernelEventDispatcher(graph);
        probe.NestedOperation = async () =>
        {
            var nestedOutcome = await dispatcher.DispatchAsync(
                nested,
                new ScopeEvent(2),
                graph.ActionSnapshot,
                cancellationToken: CancellationToken.None);
            Assert.Equal(EventInterceptionKind.Continued, nestedOutcome.Kind);
        };
        using var callerCancellation = new CancellationTokenSource();

        var dispatch = dispatcher.DispatchAsync(
            root,
            new ScopeEvent(1),
            graph.ActionSnapshot,
            cancellationToken: callerCancellation.Token).AsTask();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
            callerCancellation.Cancel();

        var outcome = await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(EventInterceptionKind.Failed, outcome.Kind);
        Assert.Equal("EVENT_OUTCOME_UNCERTAIN", outcome.Error?.Code);
        Assert.Equal(0, probe.DisposalCount);

        probe.Release.TrySetResult(true);
        await probe.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForDisposalsAsync(probe, 1);
        Assert.Equal(probe.RootInstances.Single(), probe.NestedInstances.Single());

        probe.ReleasePostCompletion.TrySetResult(true);
        await probe.PostCompletionOperation.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForDisposalsAsync(probe, 2);
        Assert.NotEqual(probe.RootInstances.Single(), probe.NestedInstances.Last());
    }

    [Fact]
    public async Task Completed_root_authority_does_not_flow_into_a_delayed_child()
    {
        var rootKey = new SharpClawActionKey("scope.authority.root");
        var childKey = new SharpClawActionKey("scope.authority.child");
        var root = Action(rootKey);
        var child = Action(childKey);
        var services = new ServiceCollection();
        services.AddAction("scope", root);
        services.AddAction("scope", child);
        var graph = services.Compile(ActionOptions(rootKey, childKey));
        using var defaultFeatureDocument = JsonDocument.Parse("{\"authority\":\"default\"}");
        var defaultCaller = new RequestPrincipal(
            "default-caller",
            "Default Caller",
            new HashSet<string>(["reader"], StringComparer.Ordinal),
            IsAuthenticated: true);
        var defaultFeatures = new ExtensionFeatureSet(
        [
            new ExtensionFeature(
                "scope.authority",
                1,
                "default",
                1024,
                defaultFeatureDocument.RootElement.Clone()),
        ]);
        var defaultTraceId = Guid.NewGuid();
        var defaultIdempotencyKey = Guid.NewGuid();
        var dispatcher = new KernelActionDispatcher(
            graph,
            new KernelActionExecutionContext(
                defaultCaller,
                defaultFeatures,
                defaultTraceId,
                defaultIdempotencyKey));
        using var privilegedFeatureDocument = JsonDocument.Parse("{\"authority\":\"privileged\"}");
        var privilegedContext = new KernelActionExecutionContext(
            new RequestPrincipal(
                "privileged-caller",
                "Privileged Caller",
                new HashSet<string>(["administrator"], StringComparer.Ordinal),
                IsAuthenticated: true),
            new ExtensionFeatureSet(
            [
                new ExtensionFeature(
                    "scope.authority",
                    1,
                    "privileged",
                    1024,
                    privilegedFeatureDocument.RootElement.Clone()),
            ]),
            Guid.NewGuid(),
            Guid.NewGuid());
        var childStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ActionContext<KernelActionEnvelope>>? delayedChild = null;

        var rootOutcome = await dispatcher.RunWithContextAsync<KernelActionEnvelope, object>(
            privilegedContext,
            root,
            new KernelActionEnvelope(rootKey, "root"),
            async (_, _) =>
            {
                delayedChild = Task.Run(async () =>
                {
                    childStarted.TrySetResult(true);
                    await releaseChild.Task;
                    ActionContext<KernelActionEnvelope>? captured = null;
                    await dispatcher.RunRequiredAsync(
                        child,
                        new KernelActionEnvelope(childKey, "child"),
                        (context, _) =>
                        {
                            captured = context;
                            return ValueTask.FromResult<object>("child");
                        },
                        graph.ActionSnapshot,
                        CancellationToken.None);
                    return captured!;
                });
                await childStarted.Task;
                return "root";
            },
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, rootOutcome.Kind);
        releaseChild.TrySetResult(true);
        var childContext = await delayedChild!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(childContext.ParentInvocationId);
        Assert.Equal(0, childContext.Depth);
        Assert.Equal(defaultCaller.SubjectId, childContext.Caller.SubjectId);
        Assert.Equal(defaultCaller.Roles, childContext.Caller.Roles);
        Assert.Equal(defaultTraceId, childContext.TraceId);
        Assert.Equal(defaultIdempotencyKey, childContext.IdempotencyKey);
        Assert.Equal("default", childContext.Features.Items.Single().OwnerId);
    }

    private static ActionDescriptor<KernelActionEnvelope, object> Action(SharpClawActionKey key) =>
        new(
            key,
            1,
            "scope",
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap,
            false,
            false,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "scope"),
            new ActionContinuationPolicy(TimeSpan.FromMinutes(1), true, true),
            TimeSpan.FromSeconds(10));

    private static EventDescriptor<ScopeEvent> Event(SharpClawEventKey key) =>
        new(
            key,
            1,
            "scope",
            EventInterceptionCapabilities.Inspect | EventInterceptionCapabilities.Observe,
            false,
            false);

    private static KernelGraphCompileOptions ActionOptions(
        SharpClawActionKey rootKey,
        SharpClawActionKey nestedKey) =>
        new()
        {
            ActionRegistrationCapabilityGrants = new Dictionary<
                string,
                IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
            {
                ["scope"] = new Dictionary<string, ActionInterceptionCapabilities>
                {
                    [rootKey.Value] =
                        ActionInterceptionCapabilities.Inspect |
                        ActionInterceptionCapabilities.Wrap,
                    [nestedKey.Value] =
                        ActionInterceptionCapabilities.Inspect |
                        ActionInterceptionCapabilities.Wrap,
                },
            },
        };

    private static KernelGraphCompileOptions EventOptions(
        SharpClawEventKey rootKey,
        SharpClawEventKey nestedKey) =>
        new()
        {
            EventRegistrationCapabilityGrants = new Dictionary<
                string,
                IReadOnlyDictionary<string, EventInterceptionCapabilities>>
            {
                ["scope"] = new Dictionary<string, EventInterceptionCapabilities>
                {
                    [rootKey.Value] =
                        EventInterceptionCapabilities.Inspect |
                        EventInterceptionCapabilities.Observe,
                    [nestedKey.Value] =
                        EventInterceptionCapabilities.Inspect |
                        EventInterceptionCapabilities.Observe,
                },
            },
        };

    private static async Task WaitForDisposalsAsync(DetachedScopeProbe probe, int expected)
    {
        for (var attempt = 0; attempt < 100 && probe.DisposalCount < expected; attempt++)
            await Task.Delay(10);
        Assert.Equal(expected, probe.DisposalCount);
    }

    private sealed class ScopeCapture
    {
        public HashSet<Guid> ActionInstances { get; } = [];
        public HashSet<Guid> EventInstances { get; } = [];
        public HashSet<Guid> ToolInstances { get; } = [];
        public HashSet<Guid> ChatInstances { get; } = [];
        public Dictionary<string, int> Disposals { get; } = [];

        public void RecordDisposal(string category) =>
            Disposals[category] = Disposals.GetValueOrDefault(category) + 1;
    }

    private sealed class DetachedScopeProbe
    {
        private int _disposalCount;

        public ConcurrentQueue<Guid> RootInstances { get; } = new();
        public ConcurrentQueue<Guid> NestedInstances { get; } = new();
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleasePostCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<ValueTask> NestedOperation { get; set; } = null!;
        public Task PostCompletionOperation { get; set; } = null!;
        public int DisposalCount => Volatile.Read(ref _disposalCount);

        public void RecordDisposal() => Interlocked.Increment(ref _disposalCount);
    }

    private sealed class DetachedScopedService(DetachedScopeProbe probe) : IDisposable
    {
        public Guid InstanceId { get; } = Guid.NewGuid();

        public void Dispose() => probe.RecordDisposal();
    }

    private sealed class DetachedActionInterceptor(
        DetachedScopeProbe probe,
        DetachedScopedService scoped) : IActionInterceptor<KernelActionEnvelope, object>
    {
        public async ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            probe.RootInstances.Enqueue(scoped.InstanceId);
            probe.Started.TrySetResult(true);
            await probe.Release.Task;
            try
            {
                await probe.NestedOperation();
                probe.PostCompletionOperation = Task.Run(async () =>
                {
                    await probe.ReleasePostCompletion.Task;
                    await probe.NestedOperation();
                });
                throw new InvalidOperationException("The detached action completed after its boundary returned.");
            }
            finally
            {
                probe.Completed.TrySetResult(true);
            }
        }
    }

    private sealed class NestedActionInterceptor(
        DetachedScopeProbe probe,
        DetachedScopedService scoped) : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            probe.NestedInstances.Enqueue(scoped.InstanceId);
            return control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class DetachedEventInterceptor(
        DetachedScopeProbe probe,
        DetachedScopedService scoped) : IEventInterceptor<ScopeEvent>
    {
        public async ValueTask<IEventInterception<ScopeEvent>> InterceptAsync(
            EventContext<ScopeEvent> context,
            IEventControl<ScopeEvent> control,
            CancellationToken cancellationToken)
        {
            probe.RootInstances.Enqueue(scoped.InstanceId);
            probe.Started.TrySetResult(true);
            await probe.Release.Task;
            try
            {
                await probe.NestedOperation();
                probe.PostCompletionOperation = Task.Run(async () =>
                {
                    await probe.ReleasePostCompletion.Task;
                    await probe.NestedOperation();
                });
                throw new InvalidOperationException("The detached event completed after its boundary returned.");
            }
            finally
            {
                probe.Completed.TrySetResult(true);
            }
        }
    }

    private sealed class NestedEventInterceptor(
        DetachedScopeProbe probe,
        DetachedScopedService scoped) : IEventInterceptor<ScopeEvent>
    {
        public ValueTask<IEventInterception<ScopeEvent>> InterceptAsync(
            EventContext<ScopeEvent> context,
            IEventControl<ScopeEvent> control,
            CancellationToken cancellationToken)
        {
            probe.NestedInstances.Enqueue(scoped.InstanceId);
            return ValueTask.FromResult(control.Continue());
        }
    }

    private abstract class ScopedBehavior(ScopeCapture capture, string category) : IDisposable
    {
        protected Guid InstanceId { get; } = Guid.NewGuid();
        protected ScopeCapture Capture { get; } = capture;
        public void Dispose() => Capture.RecordDisposal(category);
    }

    private sealed class ScopedActionInterceptor(ScopeCapture capture) : ScopedBehavior(capture, "action"),
        IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            Capture.ActionInstances.Add(InstanceId);
            return control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class ScopedEventListener(ScopeCapture capture) : ScopedBehavior(capture, "event"),
        IEventListener<ScopeEvent>
    {
        public ValueTask OnEventAsync(EventEnvelope<ScopeEvent> envelope, CancellationToken cancellationToken)
        {
            Capture.EventInstances.Add(InstanceId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScopedToolHandler(ScopeCapture capture) : ScopedBehavior(capture, "tool"), IToolHandler
    {
        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            Capture.ToolInstances.Add(InstanceId);
            return ValueTask.FromResult(ToolResult.Text("scoped"));
        }
    }

    private sealed class ScopedContextContributor(ScopeCapture capture) : ScopedBehavior(capture, "chat"),
        IChatContextContributor
    {
        public ValueTask<ChatContextContribution> ContributeAsync(
            ChatContextRequest request,
            ChatOperationContext context,
            CancellationToken cancellationToken)
        {
            Capture.ChatInstances.Add(InstanceId);
            return ValueTask.FromResult(ChatContextContribution.Empty);
        }
    }

    private sealed record ScopeEvent(int Value);
}
