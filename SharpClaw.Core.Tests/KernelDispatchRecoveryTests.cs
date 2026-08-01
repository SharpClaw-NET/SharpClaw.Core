using System.Collections.Concurrent;
using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelDispatchAuthorityTests
{
    [Fact]
    public async Task All_action_hook_targets_receive_immutable_host_authority()
    {
        AuthorityCapture.Reset();
        var roles = new HashSet<string>(StringComparer.Ordinal) { "operator" };
        var features = new List<ExtensionFeature>
        {
            new(
                "sample.feature",
                1,
                "host",
                256,
                JsonSerializer.SerializeToElement(new { enabled = true }))
        };
        var traceId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var executionContext = new KernelActionExecutionContext(
            new RequestPrincipal("caller-a", "Caller A", roles, true),
            new ExtensionFeatureSet(features),
            traceId,
            idempotencyKey);
        roles.Add("substituted");
        features.Clear();

        var key = new SharpClawActionKey("authority.dispatch");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(key));
        builder.Hooks.For(key).Use<TypedAuthorityInterceptor>(Order("typed"));
        builder.Hooks.For(key).UseAny<ExactAuthorityInterceptor>(Order("exact"));
        builder.Hooks.Category("authority").UseAny<CategoryAuthorityInterceptor>(Order("category"));
        builder.Hooks.AnyAction().UseAny<WildcardAuthorityInterceptor>(Order("wildcard"));
        var graph = builder.Compile();
        var dispatcher = new KernelActionDispatcher(graph, executionContext);

        var outcome = await dispatcher.RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "input"),
            (_, _) => ValueTask.FromResult<object>("done"),
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(
            ["category", "exact", "typed", "wildcard"],
            AuthorityCapture.Items.Select(item => item.Target).Order().ToArray());
        Assert.All(AuthorityCapture.Items, item =>
        {
            Assert.Equal("caller-a", item.Caller.SubjectId);
            Assert.True(item.Caller.IsAuthenticated);
            Assert.NotNull(item.Caller.Roles);
            Assert.Contains("operator", item.Caller.Roles!);
            Assert.DoesNotContain("substituted", item.Caller.Roles!);
            Assert.Equal(traceId, item.TraceId);
            Assert.Equal(idempotencyKey, item.IdempotencyKey);
            var feature = Assert.Single(item.Features.Items);
            Assert.Equal("sample.feature", feature.ContractName);
            Assert.True(feature.Value.GetProperty("enabled").GetBoolean());
        });
    }

    [Fact]
    public async Task Nested_dispatch_preserves_parent_authority_and_isolates_the_next_request()
    {
        NestedAuthorityInterceptor.Reset();
        var outerKey = new SharpClawActionKey("authority.outer");
        var innerKey = new SharpClawActionKey("authority.inner");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(outerKey));
        builder.Add(Descriptor(innerKey));
        builder.Hooks.For(innerKey).Use<NestedAuthorityInterceptor>(Order("nested"));
        var graph = builder.Compile();
        var firstContext = Context("caller-a", "feature-a");
        var secondContext = Context("caller-b", "feature-b");
        var firstDispatcher = new KernelActionDispatcher(graph, firstContext);
        var secondDispatcher = new KernelActionDispatcher(graph, secondContext);

        var outer = await firstDispatcher.RunAsync(
            graph.GetStandardAction(outerKey),
            new KernelActionEnvelope(outerKey, "input"),
            async (_, cancellationToken) => await secondDispatcher.RunRequiredAsync(
                graph.GetStandardAction(innerKey),
                new KernelActionEnvelope(innerKey, secondContext.Caller),
                (_, _) => ValueTask.FromResult<object>("nested"),
                graph.ActionSnapshot,
                cancellationToken),
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outer.Kind);
        var nested = Assert.Single(NestedAuthorityInterceptor.Items);
        Assert.Equal("caller-a", nested.Caller.SubjectId);
        Assert.Equal(firstContext.TraceId, nested.TraceId);
        Assert.Equal(firstContext.IdempotencyKey, nested.IdempotencyKey);
        Assert.Equal(1, nested.Depth);
        Assert.NotNull(nested.ParentInvocationId);
        Assert.Equal("feature-a", Assert.Single(nested.Features.Items).ContractName);

        await secondDispatcher.RunAsync(
            graph.GetStandardAction(innerKey),
            new KernelActionEnvelope(innerKey, firstContext.Caller),
            (_, _) => ValueTask.FromResult<object>("separate"),
            graph.ActionSnapshot,
            CancellationToken.None);

        var separate = NestedAuthorityInterceptor.Items[1];
        Assert.Equal("caller-b", separate.Caller.SubjectId);
        Assert.Equal(secondContext.TraceId, separate.TraceId);
        Assert.Equal(secondContext.IdempotencyKey, separate.IdempotencyKey);
        Assert.Equal(0, separate.Depth);
        Assert.Null(separate.ParentInvocationId);
        Assert.Equal("feature-b", Assert.Single(separate.Features.Items).ContractName);
    }

    [Theory]
    [InlineData(ActionRepeatKind.ConflictOnly, KernelActionRepeatEvidenceKind.Conflict)]
    [InlineData(ActionRepeatKind.Receipted, KernelActionRepeatEvidenceKind.DurableReceipt)]
    public async Task Host_evidence_authorizes_repeat_with_new_attempt_identity(
        ActionRepeatKind repeatKind,
        KernelActionRepeatEvidenceKind evidenceKind)
    {
        RepeatAuthorityInterceptor.Reset();
        var key = new SharpClawActionKey($"repeat.{repeatKind}");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(
            key,
            new ActionRepeatPolicy(repeatKind, 2, TimeSpan.Zero, "tenant-a")));
        builder.Hooks.For(key).Use<RepeatAuthorityInterceptor>(Order("repeat"));
        var graph = builder.Compile();
        var authority = new ConfigurableRepeatEvidenceAuthority(ValidEvidence);
        var executionContext = Context("caller-a", "feature-a");
        var terminalCalls = 0;
        var dispatcher = new KernelActionDispatcher(
            graph,
            executionContext,
            repeatEvidenceAuthority: authority);

        var outcome = await dispatcher.RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "conflict and receipt text is not authority"),
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult<object>("done");
            },
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(1, terminalCalls);
        Assert.Equal([1, 2], RepeatAuthorityInterceptor.Items.Select(item => item.Attempt));
        Assert.Equal(2, RepeatAuthorityInterceptor.Items.Select(item => item.InvocationId).Distinct().Count());
        Assert.All(RepeatAuthorityInterceptor.Items, item =>
        {
            Assert.Equal(executionContext.TraceId, item.TraceId);
            Assert.Equal(executionContext.IdempotencyKey, item.IdempotencyKey);
            Assert.Equal("caller-a", item.Caller.SubjectId);
            Assert.Equal("feature-a", Assert.Single(item.Features.Items).ContractName);
        });
        var request = Assert.Single(authority.Requests);
        Assert.Equal(evidenceKind, request.RequiredKind);
        Assert.Equal(RepeatAuthorityInterceptor.Items[0].InvocationId, request.PriorInvocationId);
        Assert.Equal(RepeatAuthorityInterceptor.Items[1].InvocationId, request.NextInvocationId);
        Assert.Equal(1, request.PriorAttempt);
        Assert.Equal(2, request.NextAttempt);
        Assert.Equal("tenant-a", request.IdempotencyScope);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("forged")]
    [InlineData("stale")]
    [InlineData("expired")]
    [InlineData("wrong-action")]
    [InlineData("wrong-version")]
    [InlineData("wrong-prior-invocation")]
    [InlineData("wrong-prior-attempt")]
    [InlineData("wrong-next-invocation")]
    [InlineData("wrong-next-attempt")]
    [InlineData("wrong-scope")]
    [InlineData("wrong-idempotency")]
    public async Task Invalid_repeat_evidence_fails_before_a_new_attempt(string defect)
    {
        RepeatAuthorityInterceptor.Reset();
        var key = new SharpClawActionKey("repeat.invalid");
        var builder = new KernelGraphBuilder(false);
        builder.Add(Descriptor(
            key,
            new ActionRepeatPolicy(ActionRepeatKind.ConflictOnly, 2, TimeSpan.Zero, "tenant-a")));
        builder.Hooks.For(key).Use<RepeatAuthorityInterceptor>(Order("repeat"));
        var graph = builder.Compile();
        var authority = new ConfigurableRepeatEvidenceAuthority(
            request => InvalidEvidence(request, defect));
        var terminalCalls = 0;
        var dispatcher = new KernelActionDispatcher(
            graph,
            Context("caller-a", "feature-a"),
            repeatEvidenceAuthority: authority);

        var outcome = await dispatcher.RunAsync(
            graph.GetStandardAction(key),
            new KernelActionEnvelope(key, "conflict"),
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult<object>("done");
            },
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("ACTION_REPEAT_EVIDENCE_INVALID", outcome.Error?.Code);
        Assert.Equal(0, terminalCalls);
        Assert.Single(RepeatAuthorityInterceptor.Items);
        Assert.Single(authority.Requests);
    }

    private static KernelActionExecutionContext Context(string subjectId, string featureName) =>
        new(
            new RequestPrincipal(
                subjectId,
                subjectId,
                new HashSet<string>(StringComparer.Ordinal) { "operator" },
                true),
            new ExtensionFeatureSet(
            [
                new ExtensionFeature(
                    featureName,
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
            "authority",
            AllCapabilities,
            false,
            false,
            repeatPolicy ?? new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "authority"),
            new ActionContinuationPolicy(TimeSpan.FromHours(1), true, true),
            TimeSpan.FromSeconds(10));

    private static HookOrdering Order(string id) =>
        new(id, HookPriority.Normal, [], [], TimeSpan.FromSeconds(5), HookFailurePolicy.FailAction);

    private static KernelActionRepeatEvidence ValidEvidence(KernelActionRepeatEvidenceRequest request) =>
        new(
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
            request.RequestedAt.AddMinutes(1));

    private static KernelActionRepeatEvidence? InvalidEvidence(
        KernelActionRepeatEvidenceRequest request,
        string defect)
    {
        if (defect == "missing")
            return null;
        var evidence = ValidEvidence(request);
        return defect switch
        {
            "forged" => evidence with { Kind = KernelActionRepeatEvidenceKind.DurableReceipt },
            "stale" => evidence with { IssuedAt = request.RequestedAt.AddTicks(-1) },
            "expired" => evidence with { ExpiresAt = request.RequestedAt },
            "wrong-action" => evidence with { ActionKey = new SharpClawActionKey("repeat.other") },
            "wrong-version" => evidence with { ActionVersion = request.ActionVersion + 1 },
            "wrong-prior-invocation" => evidence with { PriorInvocationId = Guid.NewGuid() },
            "wrong-prior-attempt" => evidence with { PriorAttempt = request.PriorAttempt + 1 },
            "wrong-next-invocation" => evidence with { NextInvocationId = Guid.NewGuid() },
            "wrong-next-attempt" => evidence with { NextAttempt = request.NextAttempt + 1 },
            "wrong-scope" => evidence with { IdempotencyScope = "tenant-b" },
            "wrong-idempotency" => evidence with { IdempotencyKey = Guid.NewGuid() },
            _ => throw new ArgumentOutOfRangeException(nameof(defect), defect, null)
        };
    }

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

    private sealed record AuthorityObservation(
        string Target,
        RequestPrincipal Caller,
        ExtensionFeatureSet Features,
        Guid TraceId,
        Guid IdempotencyKey,
        Guid InvocationId,
        Guid? ParentInvocationId,
        int Depth,
        int Attempt);

    private static class AuthorityCapture
    {
        private static readonly ConcurrentQueue<AuthorityObservation> Values = new();

        public static IReadOnlyList<AuthorityObservation> Items => Values.ToArray();

        public static void Reset()
        {
            while (Values.TryDequeue(out _))
            {
            }
        }

        public static void Add(string target, ActionContext<KernelActionEnvelope> context) =>
            Values.Enqueue(new AuthorityObservation(
                target,
                context.Caller,
                context.Features,
                context.TraceId,
                context.IdempotencyKey,
                context.InvocationId,
                context.ParentInvocationId,
                context.Depth,
                context.Attempt));

        public static void Add(string target, UntypedActionContext context) =>
            Values.Enqueue(new AuthorityObservation(
                target,
                context.Caller,
                context.Features,
                context.TraceId,
                context.IdempotencyKey,
                context.InvocationId,
                context.ParentInvocationId,
                context.Depth,
                context.Attempt));
    }

    private sealed class TypedAuthorityInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            AuthorityCapture.Add("typed", context);
            return control.ProceedAsync(cancellationToken);
        }
    }

    private abstract class UntypedAuthorityInterceptor(string target) : IAnyActionInterceptor
    {
        public ValueTask<IUntypedActionOutcome> InvokeAsync(
            UntypedActionContext context,
            IUntypedActionControl control,
            CancellationToken cancellationToken)
        {
            AuthorityCapture.Add(target, context);
            return control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class ExactAuthorityInterceptor() : UntypedAuthorityInterceptor("exact");

    private sealed class CategoryAuthorityInterceptor() : UntypedAuthorityInterceptor("category");

    private sealed class WildcardAuthorityInterceptor() : UntypedAuthorityInterceptor("wildcard");

    private sealed class NestedAuthorityInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        private static readonly ConcurrentQueue<AuthorityObservation> Values = new();

        public static IReadOnlyList<AuthorityObservation> Items => Values.ToArray();

        public static void Reset()
        {
            while (Values.TryDequeue(out _))
            {
            }
        }

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            Values.Enqueue(new AuthorityObservation(
                "nested",
                context.Caller,
                context.Features,
                context.TraceId,
                context.IdempotencyKey,
                context.InvocationId,
                context.ParentInvocationId,
                context.Depth,
                context.Attempt));
            return control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class RepeatAuthorityInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        private static readonly ConcurrentQueue<AuthorityObservation> Values = new();

        public static IReadOnlyList<AuthorityObservation> Items => Values.ToArray();

        public static void Reset()
        {
            while (Values.TryDequeue(out _))
            {
            }
        }

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            Values.Enqueue(new AuthorityObservation(
                "repeat",
                context.Caller,
                context.Features,
                context.TraceId,
                context.IdempotencyKey,
                context.InvocationId,
                context.ParentInvocationId,
                context.Depth,
                context.Attempt));
            return context.Attempt == 1
                ? control.RepeatAsync(
                    new ActionRepeatRequest<KernelActionEnvelope>(context.Action, "conflict", null),
                    cancellationToken)
                : control.ProceedAsync(cancellationToken);
        }
    }

    private sealed class ConfigurableRepeatEvidenceAuthority(
        Func<KernelActionRepeatEvidenceRequest, KernelActionRepeatEvidence?> create) :
        IKernelActionRepeatEvidenceAuthority
    {
        private readonly ConcurrentQueue<KernelActionRepeatEvidenceRequest> _requests = new();

        public IReadOnlyList<KernelActionRepeatEvidenceRequest> Requests => _requests.ToArray();

        public ValueTask<KernelActionRepeatEvidence?> AuthorizeAsync(
            KernelActionRepeatEvidenceRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Enqueue(request);
            return ValueTask.FromResult(create(request));
        }
    }
}

public sealed class KernelContinuationRecoveryTests
{
    [Fact]
    public async Task Abandoned_cancel_request_resolves_to_cancelled()
    {
        var store = new TestDurableContinuationStore();
        var now = DateTimeOffset.UtcNow;
        var firstHost = new StoreBackedContinuationHost(store, TimeSpan.FromSeconds(1));
        var recoveryHost = new StoreBackedContinuationHost(store, TimeSpan.FromSeconds(1));
        var request = Request(now, ContinuationExecutionStage.BeforeTerminal);
        var token = await firstHost.CreateAsync(request, CancellationToken.None);
        var pending = await firstHost.GetAsync(token.TokenId, CancellationToken.None);
        var claim = Claim("worker-a", now.AddSeconds(1), 1, pending!.Revision, request.ContractHash);
        var claimed = await firstHost.ClaimAsync(
            token.TokenId,
            token.Secret,
            claim,
            now,
            CancellationToken.None);
        claim = WithRevision(claim, claimed!.Revision);
        var requested = await firstHost.CancelAsync(
            token.TokenId,
            token.Secret,
            claim,
            now,
            CancellationToken.None);
        Assert.Equal(ContinuationState.CancelRequested, requested!.State);
        Assert.Equal(now, requested.CancellationRequestedAt);

        var recoveryTime = now.AddSeconds(2);
        var abandoned = await recoveryHost.ExpireAsync(
            token.TokenId,
            recoveryTime,
            CancellationToken.None);
        Assert.Equal(ContinuationState.OutcomeUncertain, abandoned!.State);
        Assert.Equal(now, abandoned.CancellationRequestedAt);
        var recoveryClaim = Claim(
            "worker-b",
            recoveryTime.AddMinutes(1),
            abandoned.Generation + 1,
            abandoned.Revision,
            request.ContractHash);
        var recoveryClaimed = await recoveryHost.ClaimContinuationRecoveryAsync(
            token.TokenId,
            token.Secret,
            recoveryClaim,
            recoveryTime,
            CancellationToken.None);
        recoveryClaim = WithRevision(recoveryClaim, recoveryClaimed!.Revision);
        var recovered = await recoveryHost.RecoverContinuationAsync(
            token.TokenId,
            token.Secret,
            recoveryClaim,
            recoveryTime,
            CancellationToken.None);

        Assert.Equal(ContinuationState.Cancelled, recovered!.State);
        Assert.Equal(now, recovered.CancellationRequestedAt);
        Assert.Null(recovered.RecoveryReference);
        Assert.Null(await firstHost.CompleteAsync(
            token.TokenId,
            token.Secret,
            WithRevision(claim, recovered.Revision),
            "stale",
            recoveryTime,
            CancellationToken.None));
    }

    [Theory]
    [InlineData(ContinuationExecutionStage.BeforeTerminal, ContinuationState.Pending)]
    [InlineData(ContinuationExecutionStage.TerminalStarted, ContinuationState.OutcomeUncertain)]
    [InlineData(ContinuationExecutionStage.TerminalReceipted, ContinuationState.Completed)]
    [InlineData(ContinuationExecutionStage.OutcomePersisted, ContinuationState.Completed)]
    [InlineData(ContinuationExecutionStage.DeliveryStarted, ContinuationState.Completed)]
    public async Task Abandoned_stage_uses_the_required_process_boundary_recovery(
        ContinuationExecutionStage stage,
        ContinuationState expectedState)
    {
        var store = new TestDurableContinuationStore();
        var now = DateTimeOffset.UtcNow;
        var firstHost = new StoreBackedContinuationHost(
            store,
            TimeSpan.FromSeconds(1),
            receiptResolver: new BoundReceiptResolver(),
            retentionPeriod: TimeSpan.FromSeconds(1));
        var recoveryHost = new StoreBackedContinuationHost(
            store,
            TimeSpan.FromSeconds(1),
            receiptResolver: new BoundReceiptResolver(),
            retentionPeriod: TimeSpan.FromSeconds(1));
        var request = Request(now, stage);
        var token = await firstHost.CreateAsync(request, CancellationToken.None);
        var pending = await firstHost.GetAsync(token.TokenId, CancellationToken.None);
        var activeClaim = Claim("worker-a", now.AddSeconds(1), 1, pending!.Revision, request.ContractHash);
        var claimed = await firstHost.ClaimAsync(
            token.TokenId,
            token.Secret,
            activeClaim,
            now,
            CancellationToken.None);
        activeClaim = WithRevision(activeClaim, claimed!.Revision);

        async ValueTask AdvanceAsync(KernelContinuationExecutionUpdate update)
        {
            var advanced = await firstHost.SetExecutionStateAsync(
                token.TokenId,
                token.Secret,
                activeClaim,
                update,
                now,
                CancellationToken.None);
            Assert.NotNull(advanced);
            activeClaim = WithRevision(activeClaim, advanced!.Revision);
        }

        if (stage != ContinuationExecutionStage.BeforeTerminal)
        {
            await AdvanceAsync(new KernelContinuationExecutionUpdate(
                ContinuationExecutionStage.TerminalStarted,
                ActionOutcomeCertainty.Uncertain));
        }
        if (stage == ContinuationExecutionStage.TerminalReceipted)
        {
            await AdvanceAsync(new KernelContinuationExecutionUpdate(
                ContinuationExecutionStage.TerminalReceipted,
                ActionOutcomeCertainty.Certain,
                ReceiptReference: "receipt-1"));
        }
        if (stage is ContinuationExecutionStage.OutcomePersisted or
            ContinuationExecutionStage.DeliveryStarted)
        {
            await AdvanceAsync(new KernelContinuationExecutionUpdate(
                ContinuationExecutionStage.OutcomePersisted,
                ActionOutcomeCertainty.Certain,
                PersistedOutcome: "persisted-outcome"));
        }
        if (stage == ContinuationExecutionStage.DeliveryStarted)
        {
            var completed = await firstHost.CompleteAsync(
                token.TokenId,
                token.Secret,
                activeClaim,
                "persisted-outcome",
                now,
                CancellationToken.None);
            Assert.NotNull(completed);
            activeClaim = WithRevision(activeClaim, completed!.Revision);
            var deliveryStarted = await firstHost.BeginDeliveryAsync(
                token.TokenId,
                token.Secret,
                activeClaim,
                now,
                CancellationToken.None);
            Assert.NotNull(deliveryStarted);
            activeClaim = WithRevision(activeClaim, deliveryStarted!.Revision);
        }

        var recoveryTime = now.AddSeconds(2);
        var abandoned = await recoveryHost.ExpireAsync(
            token.TokenId,
            recoveryTime,
            CancellationToken.None);
        Assert.Equal(ContinuationState.OutcomeUncertain, abandoned!.State);
        Assert.Equal(stage, abandoned.ExecutionStage);
        Assert.NotNull(abandoned.RecoveryReference);
        Assert.Equal("protected-input", abandoned.ProtectedInput);
        var recoveryReference = abandoned.RecoveryReference;
        var recoveryClaim = Claim(
            "worker-b",
            recoveryTime.AddMinutes(1),
            abandoned.Generation + 1,
            abandoned.Revision,
            request.ContractHash);
        var recoveryClaimed = await recoveryHost.ClaimContinuationRecoveryAsync(
            token.TokenId,
            token.Secret,
            recoveryClaim,
            recoveryTime,
            CancellationToken.None);
        Assert.NotNull(recoveryClaimed);

        var staleClaim = WithRevision(activeClaim, recoveryClaimed!.Revision);
        Assert.Null(await firstHost.ResumeAsync(
            token.TokenId,
            token.Secret,
            staleClaim,
            recoveryTime,
            CancellationToken.None));
        Assert.Null(await firstHost.SetExecutionStateAsync(
            token.TokenId,
            token.Secret,
            staleClaim,
            new KernelContinuationExecutionUpdate(
                ContinuationExecutionStage.OutcomePersisted,
                ActionOutcomeCertainty.Certain,
                PersistedOutcome: "stale"),
            recoveryTime,
            CancellationToken.None));

        recoveryClaim = WithRevision(recoveryClaim, recoveryClaimed.Revision);
        var recovered = await recoveryHost.RecoverContinuationAsync(
            token.TokenId,
            token.Secret,
            recoveryClaim,
            recoveryTime,
            CancellationToken.None);
        Assert.Equal(expectedState, recovered!.State);

        switch (stage)
        {
            case ContinuationExecutionStage.BeforeTerminal:
                Assert.Null(recovered.RecoveryReference);
                Assert.Equal(ActionOutcomeCertainty.Certain, recovered.OutcomeCertainty);
                break;
            case ContinuationExecutionStage.TerminalStarted:
                Assert.Equal(recoveryReference, recovered.RecoveryReference);
                Assert.Equal(ActionOutcomeCertainty.Uncertain, recovered.OutcomeCertainty);
                Assert.Null(recovered.CompletedOutcome);
                Assert.Equal("protected-input", recovered.ProtectedInput);
                break;
            case ContinuationExecutionStage.TerminalReceipted:
                Assert.Equal("receipt-outcome", recovered.CompletedOutcome);
                Assert.Equal("receipt-1", recovered.ReceiptReference);
                Assert.Equal(ActionOutcomeCertainty.Certain, recovered.OutcomeCertainty);
                break;
            case ContinuationExecutionStage.OutcomePersisted:
                Assert.Equal("persisted-outcome", recovered.CompletedOutcome);
                Assert.Equal(ActionOutcomeCertainty.Certain, recovered.OutcomeCertainty);
                break;
            case ContinuationExecutionStage.DeliveryStarted:
                await AssertDestinationOnlyDeliveryAsync(
                    recoveryHost,
                    token,
                    WithRevision(recoveryClaim, recovered.Revision),
                    recoveryTime);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(stage), stage, null);
        }
    }

    private static async ValueTask AssertDestinationOnlyDeliveryAsync(
        StoreBackedContinuationHost host,
        ContinuationToken token,
        KernelContinuationClaim claim,
        DateTimeOffset now)
    {
        var deliveryStarted = await host.BeginDeliveryAsync(
            token.TokenId,
            token.Secret,
            claim,
            now,
            CancellationToken.None);
        Assert.Equal(ContinuationState.Claimed, deliveryStarted!.State);
        Assert.Equal(ContinuationExecutionStage.DeliveryStarted, deliveryStarted.ExecutionStage);
        claim = WithRevision(claim, deliveryStarted.Revision);
        var delivered = await host.DeliverAsync(
            token.TokenId,
            token.Secret,
            claim,
            now,
            CancellationToken.None);
        Assert.Equal(ContinuationState.Delivered, delivered!.State);
        var deliveredClaim = WithRevision(claim, delivered.Revision);
        Assert.False(await host.DeleteAsync(
            token.TokenId,
            token.Secret,
            deliveredClaim,
            now.AddSeconds(2),
            CancellationToken.None));
        var acknowledged = await host.AcknowledgeAsync(
            token.TokenId,
            token.Secret,
            deliveredClaim,
            now.AddSeconds(2),
            CancellationToken.None);
        Assert.NotNull(acknowledged!.DeliveryAcknowledgedAt);
        var acknowledgedClaim = WithRevision(deliveredClaim, acknowledged.Revision);
        Assert.True(await host.DeleteAsync(
            token.TokenId,
            token.Secret,
            acknowledgedClaim,
            now.AddSeconds(2),
            CancellationToken.None));
        var deleted = await host.GetAsync(token.TokenId, CancellationToken.None);
        Assert.Equal(ContinuationState.Deleted, deleted!.State);
        Assert.Null(deleted.ProtectedInput);
        Assert.Null(deleted.Request.ProtectedInput);
        Assert.Null(deleted.CompletedOutcome);
    }

    private static KernelContinuationRequest Request(
        DateTimeOffset now,
        ContinuationExecutionStage stage) =>
        new(
            Guid.NewGuid(),
            new SharpClawActionKey($"continuation.{stage}"),
            1,
            Guid.NewGuid(),
            new ActionDeferRequest(
                stage == ContinuationExecutionStage.DeliveryStarted
                    ? now.AddSeconds(1)
                    : now.AddMinutes(10),
                "wait"),
            new ActionContinuationPolicy(TimeSpan.FromHours(1), true, true),
            "contract-hash",
            new ContinuationDestination("test", "result"),
            "protected-input");

    private static KernelContinuationClaim Claim(
        string owner,
        DateTimeOffset leaseExpiresAt,
        int generation,
        long revision,
        string contractHash) =>
        new(new ContinuationClaim(owner, leaseExpiresAt, generation, revision), contractHash);

    private static KernelContinuationClaim WithRevision(
        KernelContinuationClaim claim,
        long revision) =>
        claim with { Claim = claim.Claim with { ExpectedRevision = revision } };

    private sealed class BoundReceiptResolver : IKernelContinuationReceiptResolver
    {
        public ValueTask<KernelContinuationReceipt?> FindAsync(
            KernelContinuationReceiptRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(request.ReceiptReference, "receipt-1", StringComparison.Ordinal))
                return ValueTask.FromResult<KernelContinuationReceipt?>(null);
            return ValueTask.FromResult<KernelContinuationReceipt?>(new(
                request.TokenId,
                request.RecoveryReference.RecoveryId,
                request.ActionKey,
                request.ActionVersion,
                request.IdempotencyKey,
                request.ContractHash,
                "receipt-1",
                "receipt-outcome",
                DateTimeOffset.MinValue));
        }
    }
}
