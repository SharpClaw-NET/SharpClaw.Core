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
