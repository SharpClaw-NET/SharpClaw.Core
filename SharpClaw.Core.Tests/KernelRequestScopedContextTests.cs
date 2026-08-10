using System.Collections.Concurrent;
using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelRequestScopedContextTests
{
    [Fact]
    public async Task One_dispatcher_accepts_distinct_concurrent_root_contexts()
    {
        var key = NewKey("request.concurrent");
        var capture = ContextCapture.Register(key, 2, waitForRelease: true);
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        builder.Hooks.For(key).Use<ContextCaptureInterceptor>(Order("capture"));
        var graph = builder.Compile();
        var dispatcher = new KernelActionDispatcher(
            graph,
            Context("default"),
            eventWriter: new LifecycleWriter());
        var firstContext = Context("caller-a");
        var secondContext = Context("caller-b");

        var first = dispatcher.RunWithContextAsync(
            firstContext,
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "first"),
            (_, _) => ValueTask.FromResult<object>("first"),
            graph.ActionSnapshot,
            CancellationToken.None).AsTask();
        var second = dispatcher.RunWithContextAsync(
            secondContext,
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "second"),
            (_, _) => ValueTask.FromResult<object>("second"),
            graph.ActionSnapshot,
            CancellationToken.None).AsTask();

        await capture.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        capture.Release.TrySetResult(true);
        var outcomes = await Task.WhenAll(first, second);

        Assert.All(outcomes, outcome => Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind));
        var observations = capture.Items.OrderBy(item => item.Caller.SubjectId).ToArray();
        Assert.Equal(["caller-a", "caller-b"], observations.Select(item => item.Caller.SubjectId));
        Assert.Equal(
            [firstContext.TraceId, secondContext.TraceId],
            observations.Select(item => item.TraceId));
        Assert.Equal(
            [firstContext.IdempotencyKey, secondContext.IdempotencyKey],
            observations.Select(item => item.IdempotencyKey));
    }

    [Fact]
    public async Task Nested_dispatch_inherits_the_root_context_on_the_same_dispatcher()
    {
        var outerKey = NewKey("request.outer");
        var innerKey = NewKey("request.inner");
        var capture = ContextCapture.Register(innerKey, 2);
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(outerKey));
        builder.Add(Descriptor(innerKey));
        builder.Hooks.For(innerKey).Use<ContextCaptureInterceptor>(Order("capture"));
        var graph = builder.Compile();
        var dispatcher = new KernelActionDispatcher(
            graph,
            Context("default"),
            eventWriter: new LifecycleWriter());
        var first = Context("caller-a");
        var second = Context("caller-b");

        var outer = await dispatcher.RunWithContextAsync(
            first,
            graph.GetStandardAction(outerKey),
            new KernelActionEnvelope(outerKey, "outer"),
            async (_, cancellationToken) =>
            {
                var nested = await dispatcher.RunRequiredWithContextAsync(
                    second,
                    graph.GetStandardAction(innerKey),
                    new KernelActionEnvelope(innerKey, "nested"),
                    (_, _) => ValueTask.FromResult<object>("nested"),
                    graph.ActionSnapshot,
                    cancellationToken);
                return nested;
            },
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outer.Kind);
        await dispatcher.RunWithContextAsync(
            second,
            graph.GetStandardAction(innerKey),
            new KernelActionEnvelope(innerKey, "root"),
            (_, _) => ValueTask.FromResult<object>("root"),
            graph.ActionSnapshot,
            CancellationToken.None);

        var observations = capture.Items.ToArray();
        var nestedObservation = Assert.Single(observations, item => item.Depth == 1);
        Assert.Equal("caller-a", nestedObservation.Caller.SubjectId);
        Assert.Equal(first.TraceId, nestedObservation.TraceId);
        Assert.Equal(first.IdempotencyKey, nestedObservation.IdempotencyKey);
        Assert.NotNull(nestedObservation.ParentInvocationId);

        var rootObservation = Assert.Single(observations, item => item.Depth == 0);
        Assert.Equal("caller-b", rootObservation.Caller.SubjectId);
        Assert.Equal(second.TraceId, rootObservation.TraceId);
        Assert.Equal(second.IdempotencyKey, rootObservation.IdempotencyKey);
        Assert.Null(rootObservation.ParentInvocationId);
    }

    [Fact]
    public async Task Request_context_flows_through_outcomes_repeat_evidence_and_lifecycle_events()
    {
        var modes = new[] { "complete", "cancel", "fail", "defer", "uncertain" };
        var builder = new KernelGraphBuilder(false);
        var keys = modes.ToDictionary(
            mode => mode,
            mode => NewKey($"request.outcome.{mode}"),
            StringComparer.Ordinal);
        foreach (var pair in keys)
        {
            builder.Add(Descriptor(pair.Value));
            builder.Hooks.For(pair.Value).Use<OutcomeContextInterceptor>(Order(pair.Value.Value));
            OutcomeContextInterceptor.Modes[pair.Value.Value] = pair.Key;
        }

        var repeatKey = NewKey("request.outcome.repeat");
        builder.Add(Descriptor(
            repeatKey,
            new ActionRepeatPolicy(ActionRepeatKind.Idempotent, 2, TimeSpan.Zero, "request")));
        builder.Hooks.For(repeatKey).Use<RepeatContextInterceptor>(Order("repeat"));
        var writer = new LifecycleWriter();
        var evidence = new MatchingEvidenceAuthority();
        var continuationHost = new StoreBackedContinuationHost(new TestDurableContinuationStore());
        var graph = builder.Compile();
        var dispatcher = new KernelActionDispatcher(
            graph,
            Context("default"),
            continuationHost,
            eventWriter: writer,
            repeatEvidenceAuthority: evidence);

        foreach (var mode in modes)
        {
            var context = Context($"caller-{mode}");
            var outcome = await dispatcher.RunWithContextAsync(
                context,
                graph.GetStandardAction(keys[mode]),
                new KernelActionEnvelope(keys[mode], mode),
                (_, _) => ValueTask.FromResult<object>("terminal"),
                graph.ActionSnapshot,
                CancellationToken.None);

            Assert.Equal(
                mode switch
                {
                    "complete" => ActionOutcomeKind.Completed,
                    "cancel" => ActionOutcomeKind.Cancelled,
                    "fail" => ActionOutcomeKind.Failed,
                    "defer" => ActionOutcomeKind.Deferred,
                    "uncertain" => ActionOutcomeKind.Uncertain,
                    _ => throw new InvalidOperationException()
                },
                outcome.Kind);
            var observation = Assert.Single(
                OutcomeContextInterceptor.Items,
                item => item.ActionKey == keys[mode]);
            Assert.Equal(context.Caller.SubjectId, observation.Caller.SubjectId);
            Assert.Equal(context.TraceId, observation.TraceId);
            Assert.Equal(context.IdempotencyKey, observation.IdempotencyKey);
            Assert.Contains(
                writer.Items,
                item => item.ActionKey == keys[mode] && item.TraceId == context.TraceId);
        }

        var repeatContext = Context("caller-repeat");
        var repeatOutcome = await dispatcher.RunWithContextAsync(
            repeatContext,
            graph.GetStandardAction(repeatKey),
            new KernelActionEnvelope(repeatKey, "repeat"),
            (_, _) => ValueTask.FromResult<object>("repeated"),
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, repeatOutcome.Kind);
        var repeatRequest = Assert.Single(evidence.Requests);
        Assert.Equal(repeatContext.IdempotencyKey, repeatRequest.IdempotencyKey);
        Assert.All(
            RepeatContextInterceptor.Items,
            item =>
            {
                Assert.Equal(repeatContext.Caller.SubjectId, item.Caller.SubjectId);
                Assert.Equal(repeatContext.TraceId, item.TraceId);
                Assert.Equal(repeatContext.IdempotencyKey, item.IdempotencyKey);
            });
    }

    [Fact]
    public async Task Root_context_does_not_leak_after_failure_and_legacy_calls_keep_constructor_context()
    {
        var key = NewKey("request.cleanup");
        var capture = ContextCapture.Register(key, 3);
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        builder.Hooks.For(key).Use<ContextCaptureInterceptor>(Order("capture"));
        var graph = builder.Compile();
        var constructorContext = Context("constructor");
        var dispatcher = new KernelActionDispatcher(
            graph,
            constructorContext,
            eventWriter: new LifecycleWriter());

        var failed = await dispatcher.RunWithContextAsync(
            Context("failed"),
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "failed"),
            (_, _) => throw new InvalidOperationException("terminal failure"),
            graph.ActionSnapshot,
            CancellationToken.None);
        Assert.Equal(ActionOutcomeKind.Failed, failed.Kind);

        await dispatcher.RunWithContextAsync(
            Context("after-failure"),
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "after-failure"),
            (_, _) => ValueTask.FromResult<object>("recovered"),
            graph.ActionSnapshot,
            CancellationToken.None);

        await dispatcher.RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "legacy"),
            (_, _) => ValueTask.FromResult<object>("legacy"),
            graph.ActionSnapshot,
            CancellationToken.None);

        var observations = capture.Items.ToArray();
        Assert.Equal(
            ["failed", "after-failure", "constructor"],
            observations.Select(item => item.Caller.SubjectId));
    }

    private static SharpClawActionKey NewKey(string prefix) =>
        new($"{prefix}.{Guid.NewGuid():N}");

    private static KernelActionExecutionContext Context(string subjectId) =>
        ContextFor(subjectId);

    private static KernelActionExecutionContext ContextFor(string subjectId) =>
        new(
            new RequestPrincipal(
                subjectId,
                subjectId,
                new HashSet<string>(StringComparer.Ordinal) { "operator" },
                true),
            new ExtensionFeatureSet(
            [
                new ExtensionFeature(
                    $"feature.{subjectId}",
                    1,
                    "host",
                    256,
                    JsonSerializer.SerializeToElement(new { enabled = true }))
            ]),
            Guid.NewGuid(),
            Guid.NewGuid());

    private static ActionDescriptor<KernelActionEnvelope, object> Descriptor(
        SharpClawActionKey key,
        ActionRepeatPolicy? repeatPolicy = null) =>
        new(
            key,
            1,
            "request-context-tests",
            AllCapabilities,
            false,
            false,
            repeatPolicy ?? new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "request"),
            new ActionContinuationPolicy(TimeSpan.FromHours(1), true, true),
            TimeSpan.FromSeconds(10));

    private static HookOrdering Order(string id) =>
        new(id, HookPriority.Normal, [], [], TimeSpan.FromSeconds(5), HookFailurePolicy.FailAction);

    private const ActionInterceptionCapabilities AllCapabilities =
        ActionInterceptionCapabilities.Inspect |
        ActionInterceptionCapabilities.ReplaceInput |
        ActionInterceptionCapabilities.Cancel |
        ActionInterceptionCapabilities.ReplaceResult |
        ActionInterceptionCapabilities.Defer |
        ActionInterceptionCapabilities.Repeat |
        ActionInterceptionCapabilities.Wrap |
        ActionInterceptionCapabilities.Observe |
        ActionInterceptionCapabilities.PublishEvents;

    private sealed record Observation(
        SharpClawActionKey ActionKey,
        RequestPrincipal Caller,
        Guid TraceId,
        Guid IdempotencyKey,
        Guid InvocationId,
        Guid? ParentInvocationId,
        int Depth);

    private sealed class ContextCaptureState(int expected, bool waitForRelease)
    {
        public ConcurrentQueue<Observation> Items { get; } = new();
        public TaskCompletionSource<bool> Observed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WaitForRelease { get; } = waitForRelease;

        public void Add(ActionContext<KernelActionEnvelope> context)
        {
            Items.Enqueue(new Observation(
                context.ActionKey,
                context.Caller,
                context.TraceId,
                context.IdempotencyKey,
                context.InvocationId,
                context.ParentInvocationId,
                context.Depth));
            if (Items.Count >= expected)
                Observed.TrySetResult(true);
        }
    }

    private static class ContextCapture
    {
        private static readonly ConcurrentDictionary<string, ContextCaptureState> States = new();

        public static ContextCaptureState Register(
            SharpClawActionKey key,
            int expected,
            bool waitForRelease = false)
        {
            var state = new ContextCaptureState(expected, waitForRelease);
            States[key.Value] = state;
            return state;
        }

        public static bool TryGet(string key, out ContextCaptureState state) =>
            States.TryGetValue(key, out state!);
    }

    private sealed class ContextCaptureInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public async ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            if (ContextCapture.TryGet(context.ActionKey.Value, out var state))
            {
                state.Add(context);
                if (state.WaitForRelease)
                    await state.Release.Task.WaitAsync(cancellationToken);
            }

            return await control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class OutcomeContextInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public static ConcurrentQueue<Observation> Items { get; } = new();
        public static ConcurrentDictionary<string, string> Modes { get; } = new();

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            Items.Enqueue(new Observation(
                context.ActionKey,
                context.Caller,
                context.TraceId,
                context.IdempotencyKey,
                context.InvocationId,
                context.ParentInvocationId,
                context.Depth));
            return Modes[context.ActionKey.Value] switch
            {
                "complete" => control.ProceedAsync(cancellationToken),
                "cancel" => ValueTask.FromResult<IActionOutcome<object>>(
                    control.Cancel("TEST_CANCELLED", "The request was cancelled.")),
                "fail" => ValueTask.FromResult<IActionOutcome<object>>(
                    control.Fail(new ExecutionError("TEST_FAILED", "The request failed."))),
                "defer" => control.DeferAsync(
                    new ActionDeferRequest(DateTimeOffset.UtcNow.AddMinutes(1), "wait"),
                    cancellationToken),
                "uncertain" => ValueTask.FromException<IActionOutcome<object>>(
                    new ActionOutcomeUncertainException(new ActionUncertainty(
                        "TEST_UNCERTAIN",
                        "The request result is unknown.",
                        ActionExecutionStage.TerminalReturned,
                        "receipt",
                        new ActionRecoveryReference(
                            Guid.NewGuid(),
                            context.ActionKey,
                            1,
                            context.IdempotencyKey),
                        DateTimeOffset.UtcNow))),
                _ => throw new InvalidOperationException("Unknown test outcome mode.")
            };
        }
    }

    private sealed class RepeatContextInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public static ConcurrentQueue<Observation> Items { get; } = new();

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            Items.Enqueue(new Observation(
                context.ActionKey,
                context.Caller,
                context.TraceId,
                context.IdempotencyKey,
                context.InvocationId,
                context.ParentInvocationId,
                context.Depth));
            return context.Attempt == 1
                ? control.RepeatAsync(
                    new ActionRepeatRequest<KernelActionEnvelope>(context.Action, "host-evidence", null),
                    cancellationToken)
                : control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class MatchingEvidenceAuthority : IKernelActionRepeatEvidenceAuthority
    {
        private readonly ConcurrentQueue<KernelActionRepeatEvidenceRequest> _requests = new();

        public IReadOnlyList<KernelActionRepeatEvidenceRequest> Requests => _requests.ToArray();

        public ValueTask<KernelActionRepeatEvidence?> AuthorizeAsync(
            KernelActionRepeatEvidenceRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Enqueue(request);
            return ValueTask.FromResult<KernelActionRepeatEvidence?>(new KernelActionRepeatEvidence(
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

    private sealed class LifecycleWriter : ICommittedEventWriter
    {
        public ConcurrentQueue<KernelActionLifecycleEvent> Items { get; } = new();

        public ValueTask PublishAsync<TEvent>(
            EventDescriptor<TEvent> descriptor,
            TEvent payload,
            CancellationToken cancellationToken)
        {
            if (payload is KernelActionLifecycleEvent lifecycle)
                Items.Enqueue(lifecycle);
            return ValueTask.CompletedTask;
        }
    }
}
