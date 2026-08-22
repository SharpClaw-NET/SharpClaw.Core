using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelActionTests
{
    [Fact]
    public void Dispatcher_loads_against_the_beta16_terminal_delegate_contract()
    {
        var dispatcherType = typeof(KernelActionDispatcher);
        var dispatcherAssembly = System.Reflection.Assembly.Load(dispatcherType.Assembly.FullName!);
        var loadedType = dispatcherAssembly.GetType(dispatcherType.FullName!);
        Assert.NotNull(loadedType);

        var map = loadedType!.GetInterfaceMap(typeof(IActionDispatcher));
        Assert.Equal(4, map.TargetMethods.Length);
        Assert.Contains(map.TargetMethods, method => method.Name == nameof(IActionDispatcher.RunAsync));
        Assert.Contains(
            typeof(IActionDispatcher).GetMethods(),
            method => method.Name == nameof(IActionDispatcher.RunAsync) &&
                      method.GetParameters()[2].ParameterType.ToString().Contains(
                          "ActionContext",
                          StringComparison.Ordinal));
    }

    [Fact]
    public async Task Typed_and_wildcard_interceptors_run_in_one_compiled_chain()
    {
        var key = new SharpClawActionKey("sample.action");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        builder.Hooks.For(key).Use<TypedInterceptor>(Order("typed"));
        builder.Hooks.AnyAction().UseAny<WildcardInterceptor>(Order("wildcard"));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);

        var outcome = await dispatcher.RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (context, _) => ValueTask.FromResult<object>(context.Action.Payload + "-terminal"),
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("input-replaced-terminal", outcome.Result as string);
    }

    [Fact]
    public async Task Terminal_receives_the_effective_action_and_complete_dispatch_context()
    {
        var key = new SharpClawActionKey("context.action");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        builder.Hooks.For(key).Use<ReplaceContextInputInterceptor>(Order("replace-context"));
        var graph = builder.Compile();
        var caller = new RequestPrincipal(
            "caller",
            "Caller",
            new HashSet<string>(["operator"], StringComparer.Ordinal),
            true);
        var features = new ExtensionFeatureSet(
            [new ExtensionFeature("feature", 1, "sample.module", 100, JsonSerializer.SerializeToElement(true))]);
        var traceId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var dispatcher = new KernelActionDispatcher(
            graph,
            new KernelActionExecutionContext(caller, features, traceId, idempotencyKey));
        ActionContext<KernelActionEnvelope>? terminalContext = null;

        var outcome = await dispatcher.RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "A"),
            (context, _) =>
            {
                terminalContext = context;
                return ValueTask.FromResult<object>(context.Action.Payload!);
            },
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("B", outcome.Result);
        Assert.NotNull(terminalContext);
        Assert.Equal("B", terminalContext!.Action.Payload);
        Assert.Equal(traceId, terminalContext.TraceId);
        Assert.Equal(idempotencyKey, terminalContext.IdempotencyKey);
        Assert.Equal(0, terminalContext.Depth);
        Assert.Equal(1, terminalContext.Attempt);
        Assert.Same(graph.ActionSnapshot, terminalContext.Snapshot);
    }

    [Fact]
    public void Unsupported_action_effects_fail_during_graph_compilation()
    {
        var key = new SharpClawActionKey("unsupported.action");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key, ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel));

        var exception = Assert.Throws<KernelGraphCompilationException>(() => builder.Compile(
            options: new KernelGraphCompileOptions
            {
                SupportedActionCapabilities = ActionInterceptionCapabilities.Inspect
            }));

        Assert.Contains("unsupported.action", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Cancel", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeat_uses_the_declared_policy_and_keeps_one_terminal_path()
    {
        var key = new SharpClawActionKey("repeat.action");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(
            key,
            KernelActionCapabilities,
            new ActionRepeatPolicy(ActionRepeatKind.Idempotent, 2, TimeSpan.Zero, "sample")));
        builder.Hooks.For(key).Use<RepeatInterceptor>(Order("repeat"));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            repeatEvidenceAuthority: new MatchingRepeatEvidenceAuthority());
        var terminalCalls = 0;

        var outcome = await dispatcher.RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult<object>("done");
            },
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("done", outcome.Result);
        Assert.Equal(1, terminalCalls);
    }

    [Fact]
    public async Task Defer_creates_a_store_neutral_continuation()
    {
        var key = new SharpClawActionKey("defer.action");
        var host = new StoreBackedContinuationHost(new TestDurableContinuationStore());
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        builder.Hooks.For(key).Use<DeferInterceptor>(Order("defer"));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(graph, host);

        var outcome = await dispatcher.RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (_, _) => ValueTask.FromResult<object>("not-called"),
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Deferred, outcome.Kind);
        Assert.NotNull(outcome.Continuation);
        var state = await host.GetAsync(outcome.Continuation!.TokenId, CancellationToken.None);
        Assert.Equal(ContinuationState.Pending, state!.State);
    }

    [Fact]
    public async Task Uncertain_terminal_outcome_is_recorded_without_automatic_repeat()
    {
        var key = new SharpClawActionKey("uncertain.action");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        var graph = builder.Compile();
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            new StoreBackedContinuationHost(new TestDurableContinuationStore()));

        var outcome = await dispatcher.RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (_, _) => throw new ActionOutcomeUncertainException(
                new ActionUncertainty(
                    "UNKNOWN_RECEIPT",
                    "The provider receipt is unavailable.",
                    ActionExecutionStage.TerminalReturned,
                    "receipt-1",
                    new ActionRecoveryReference(Guid.NewGuid(), key, 1, Guid.NewGuid()),
                    DateTimeOffset.UtcNow)),
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Uncertain, outcome.Kind);
        Assert.NotNull(outcome.Uncertainty);
        Assert.False(outcome.Uncertainty!.AutomaticRepeatAllowed);
    }

    [Fact]
    public void K01_through_K14_compile_as_a_single_action_catalog()
    {
        var graph = new KernelGraphBuilder().Compile();

        Assert.Equal(14, KernelActionCatalog.Coverage.Count);
        Assert.Equal(
            14,
            KernelActionCatalog.Coverage.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(KernelActionCatalog.Coverage, entry => Assert.True(graph.ContainsAction(entry.ActionKey)));
        Assert.NotEmpty(graph.ActionSnapshot.ContractHash);
    }

    [Fact]
    public void Module_registry_compiles_module_actions_without_host_policy_types()
    {
        var registry = new KernelModuleRegistry();
        registry.Add(new SampleModule());

        var graph = registry.Compile(
            options: new KernelGraphCompileOptions
            {
                ActionModuleCapabilityGrants = new Dictionary<
                    string,
                    IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
                {
                    ["sample.module"] = new Dictionary<string, ActionInterceptionCapabilities>
                    {
                        ["module.action"] = KernelActionCapabilities
                    }
                }
            });

        Assert.Single(registry.Modules);
        Assert.True(graph.ContainsAction(new SharpClawActionKey("module.action")));
    }

    private static ActionDescriptor<KernelActionEnvelope, object> Descriptor(
        SharpClawActionKey key,
        ActionInterceptionCapabilities capabilities = KernelActionCapabilities,
        ActionRepeatPolicy? repeatPolicy = null) =>
        new(
            key,
            1,
            "sample",
            capabilities,
            false,
            false,
            repeatPolicy ?? new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "sample"),
            new ActionContinuationPolicy(TimeSpan.FromHours(1), true, true),
            TimeSpan.FromSeconds(10));

    private static HookOrdering Order(string id) =>
        new(id, HookPriority.Normal, Array.Empty<string>(), Array.Empty<string>(), null, HookFailurePolicy.FailAction);

    private const ActionInterceptionCapabilities KernelActionCapabilities =
        ActionInterceptionCapabilities.Inspect |
        ActionInterceptionCapabilities.ReplaceInput |
        ActionInterceptionCapabilities.Cancel |
        ActionInterceptionCapabilities.ReplaceResult |
        ActionInterceptionCapabilities.Defer |
        ActionInterceptionCapabilities.Repeat |
        ActionInterceptionCapabilities.Wrap |
        ActionInterceptionCapabilities.Observe |
        ActionInterceptionCapabilities.PublishEvents;

    private sealed class TypedInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            control.ProceedWithInputAsync(
                new ActionReplacement<KernelActionEnvelope>(
                    context.Action with { Payload = "input-replaced" },
                    "test replacement"),
                cancellationToken);
    }

    private sealed class ReplaceContextInputInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            control.ProceedWithInputAsync(
                new ActionReplacement<KernelActionEnvelope>(
                    context.Action with { Payload = "B" },
                    "replace action for terminal context test"),
                cancellationToken);
    }

    private sealed class WildcardInterceptor : IAnyActionInterceptor
    {
        public ValueTask<IUntypedActionOutcome> InvokeAsync(
            UntypedActionContext context,
            IUntypedActionControl control,
            CancellationToken cancellationToken) =>
            control.ProceedAsync(cancellationToken);
    }

    private sealed class RepeatInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) => context.Attempt == 1
                ? control.RepeatAsync(
                    new ActionRepeatRequest<KernelActionEnvelope>(context.Action, "retry once", null),
                    cancellationToken)
                : control.ProceedAsync(cancellationToken);
    }

    private sealed class DeferInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            control.DeferAsync(
                new ActionDeferRequest(DateTimeOffset.UtcNow.AddMinutes(1), "approval required"),
                cancellationToken);
    }

    private sealed class SampleModule : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("sample.module", "Sample", "sample");

        public void Configure(ISharpClawModuleBuilder builder)
        {
            builder.Actions.Add(Descriptor(new SharpClawActionKey("module.action")));
        }
    }
}
