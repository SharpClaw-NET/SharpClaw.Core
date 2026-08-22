using System.Security.Cryptography;
using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelExternalActionDispatchTests
{
    [Fact]
    public async Task External_action_uses_the_singleton_dispatcher_without_a_local_descriptor()
    {
        var builder = new KernelGraphBuilder(false);
        var localKey = new SharpClawActionKey("local.host.action");
        builder.Add(LocalDescriptor(localKey));
        var graph = builder.Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        var verifier = new ExactExternalAuthorityVerifier(fixture.Authority);
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityVerifier: verifier);
        var terminalCalls = 0;

        Assert.False(graph.ContainsAction(fixture.Descriptor.Key));
        var external = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (context, _) =>
            {
                terminalCalls++;
                Assert.Equal(fixture.Action, context.Action);
                Assert.Equal(fixture.Authority.ModuleId, context.OwnerModuleId);
                Assert.Equal(fixture.Authority.EffectiveHostEntry.EffectiveContext.Snapshot, context.Snapshot);
                return ValueTask.FromResult(new ExternalResult("sidecar-result"));
            },
            graph.ActionSnapshot,
            fixture.Authority,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, external.Kind);
        Assert.Equal("sidecar-result", external.Result?.Value);
        Assert.Equal(1, terminalCalls);

        var localCalls = 0;
        var local = await dispatcher.RunAsync(
            graph.GetStandardAction(localKey),
            new KernelActionEnvelope(localKey, "local"),
            (_, _) =>
            {
                localCalls++;
                return ValueTask.FromResult<object>("local-result");
            },
            graph.ActionSnapshot,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, local.Kind);
        Assert.Equal("local-result", local.Result);
        Assert.Equal(1, localCalls);
    }

    [Fact]
    public async Task External_action_rejects_missing_or_changed_authority_before_terminal()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        var verifier = new ExactExternalAuthorityVerifier(fixture.Authority);
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityVerifier: verifier);
        var terminalCalls = 0;

        var cases = new[]
        {
            ("missing", (SidecarExternalActionDispatchAuthority)null!),
            ("module", fixture.Authority with { ModuleId = "other.module" }),
            ("graph", fixture.Authority with { GraphId = "other.graph" }),
            ("descriptor", fixture.Authority with
            {
                Descriptor = fixture.Authority.Descriptor with { DescriptorHash = "changed" }
            }),
            ("terminal", fixture.Authority with
            {
                Terminal = fixture.Authority.Terminal with { TerminalId = Guid.NewGuid() }
            }),
            ("host-context", fixture.Authority with
            {
                InitiatingHostContext = fixture.Authority.InitiatingHostContext with
                {
                    Caller = new RequestPrincipal("other.caller")
                }
            }),
            ("snapshot", fixture.Authority),
            ("stale", fixture.Authority with
            {
                EffectiveHostEntry = fixture.Authority.EffectiveHostEntry with
                {
                    Authority = fixture.Authority.EffectiveHostEntry.Authority with
                    {
                        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
                    }
                }
            }),
            ("forged-proof", WithProof(fixture.Authority, "forged-proof")),
            ("recomputed-fabricated-proof", WithProof(
                fixture.Authority,
                "fabricated-proof",
                recomputeHash: true))
        };

        foreach (var (name, authority) in cases)
        {
            var snapshot = name == "snapshot"
                ? graph.ActionSnapshot with { MaximumActionDepth = graph.ActionSnapshot.MaximumActionDepth + 1 }
                : graph.ActionSnapshot;
            var outcome = await dispatcher.RunExternalAsync(
                fixture.Descriptor,
                name == "payload" ? fixture.Action with { Value = "changed" } : fixture.Action,
                (_, _) =>
                {
                    terminalCalls++;
                    return ValueTask.FromResult(new ExternalResult("unexpected"));
                },
                snapshot,
                authority,
                CancellationToken.None);

            Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
            Assert.NotNull(outcome.Error);
            Assert.NotEqual("accepted", outcome.Error!.Code);
        }

        var changedPayload = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action with { Value = "changed" },
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(new ExternalResult("unexpected"));
            },
            graph.ActionSnapshot,
            fixture.Authority,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, changedPayload.Kind);
        Assert.Equal(0, terminalCalls);
    }

    [Fact]
    public async Task External_authority_is_consumed_once_and_requires_trusted_verification()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        var verifier = new ExactExternalAuthorityVerifier(fixture.Authority);
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityVerifier: verifier);
        var terminalCalls = 0;

        var first = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (_, _) =>
            {
                Interlocked.Increment(ref terminalCalls);
                return ValueTask.FromResult(new ExternalResult("accepted"));
            },
            graph.ActionSnapshot,
            fixture.Authority,
            CancellationToken.None);
        var second = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (_, _) =>
            {
                Interlocked.Increment(ref terminalCalls);
                return ValueTask.FromResult(new ExternalResult("replayed"));
            },
            graph.ActionSnapshot,
            fixture.Authority,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, first.Kind);
        Assert.Equal(ActionOutcomeKind.Failed, second.Kind);
        Assert.Equal(SidecarCapabilityErrors.Replay, second.Error?.Code);
        Assert.Equal(1, terminalCalls);
        Assert.Equal(1, verifier.SuccessfulConsumptions);
    }

    [Fact]
    public async Task External_authority_allows_one_dispatch_under_concurrent_replay()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        var verifier = new ExactExternalAuthorityVerifier(fixture.Authority);
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityVerifier: verifier);
        var terminalCalls = 0;

        async Task<IActionOutcome<ExternalResult>> DispatchAsync() =>
            await dispatcher.RunExternalAsync(
                fixture.Descriptor,
                fixture.Action,
                (_, _) =>
                {
                    Interlocked.Increment(ref terminalCalls);
                    return ValueTask.FromResult(new ExternalResult("accepted"));
                },
                graph.ActionSnapshot,
                fixture.Authority,
                CancellationToken.None);

        var outcomes = await Task.WhenAll(DispatchAsync(), DispatchAsync());

        Assert.Equal(1, outcomes.Count(outcome => outcome.Kind == ActionOutcomeKind.Completed));
        Assert.Equal(1, outcomes.Count(outcome => outcome.Kind == ActionOutcomeKind.Failed));
        Assert.Equal(1, terminalCalls);
        Assert.Equal(1, verifier.SuccessfulConsumptions);
    }

    [Fact]
    public async Task External_action_requires_a_trusted_verifier()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        var dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var terminalCalls = 0;

        var outcome = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(new ExternalResult("unexpected"));
            },
            graph.ActionSnapshot,
            fixture.Authority,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("ACTION_EXTERNAL_AUTHORITY_UNAVAILABLE", outcome.Error?.Code);
        Assert.Equal(0, terminalCalls);
    }

    [Fact]
    public async Task External_action_uses_the_host_wildcard_policy_once()
    {
        Volatile.Write(ref ExternalWildcardInterceptor.Calls, 0);
        var builder = new KernelGraphBuilder(false);
        builder.Hooks.AnyAction().UseAny<ExternalWildcardInterceptor>(Order("external-wildcard"));
        var policyCapabilities = ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap;
        var graph = builder.Compile(options: new KernelGraphCompileOptions
        {
            ActionModuleCapabilityGrants = ModuleGrant(policyCapabilities)
        });
        var fixture = CreateFixture(graph, ExternalDescriptor(policyCapabilities));
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityVerifier: new ExactExternalAuthorityVerifier(fixture.Authority));
        var terminalCalls = 0;

        var outcome = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (context, _) =>
            {
                terminalCalls++;
                Assert.Equal(fixture.Action, context.Action);
                return ValueTask.FromResult(new ExternalResult("accepted"));
            },
            graph.ActionSnapshot,
            fixture.Authority,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(1, ExternalWildcardInterceptor.Calls);
        Assert.Equal(1, terminalCalls);
    }

    [Fact]
    public async Task External_action_rejects_unsupported_effects_before_terminal()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: new KernelGraphCompileOptions
        {
            SupportedActionCapabilities = ActionInterceptionCapabilities.Inspect,
            ActionModuleCapabilityGrants = ModuleGrant(ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel)
        });
        var descriptor = ExternalDescriptor(ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel);
        var fixture = CreateFixture(graph, descriptor);
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityVerifier: new ExactExternalAuthorityVerifier(fixture.Authority));
        var terminalCalls = 0;

        var outcome = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(new ExternalResult("unexpected"));
            },
            graph.ActionSnapshot,
            fixture.Authority,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("ACTION_EXTERNAL_POLICY_REJECTED", outcome.Error?.Code);
        Assert.Equal(0, terminalCalls);
    }

    [Fact]
    public async Task External_action_rejects_denied_effects_before_terminal()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var descriptor = ExternalDescriptor(ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel);
        var fixture = CreateFixture(graph, descriptor);
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityVerifier: new ExactExternalAuthorityVerifier(fixture.Authority));
        var terminalCalls = 0;

        var outcome = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(new ExternalResult("unexpected"));
            },
            graph.ActionSnapshot,
            fixture.Authority,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("ACTION_EXTERNAL_POLICY_REJECTED", outcome.Error?.Code);
        Assert.Equal(0, terminalCalls);
    }

    [Fact]
    public async Task External_sensitive_action_requires_exact_host_approval()
    {
        var descriptor = ExternalDescriptor(sensitive: true);
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph, descriptor);
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityVerifier: new ExactExternalAuthorityVerifier(fixture.Authority));
        var terminalCalls = 0;

        var outcome = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(new ExternalResult("unexpected"));
            },
            graph.ActionSnapshot,
            fixture.Authority,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("ACTION_EXTERNAL_POLICY_REJECTED", outcome.Error?.Code);
        Assert.Equal(0, terminalCalls);
    }

    private static ActionDescriptor<KernelActionEnvelope, object> LocalDescriptor(SharpClawActionKey key) =>
        new(
            key,
            1,
            "local",
            ActionInterceptionCapabilities.Inspect,
            false,
            false,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, key.Value),
            null,
            TimeSpan.FromSeconds(30))
        {
            InputSchema = new JsonSchemaReference("local.input", 1, "local-input"),
            ResultSchema = new JsonSchemaReference("local.result", 1, "local-result"),
        };

    private static ExternalFixture CreateFixture(
        KernelGraph graph,
        ActionDescriptor<ExternalInput, ExternalResult>? descriptorOverride = null)
    {
        var now = DateTimeOffset.UtcNow;
        var descriptor = descriptorOverride ?? ExternalDescriptor();
        var action = new ExternalInput("payload-a");
        var actionBytes = SidecarCapabilityTransportCodec.Serialize(action);
        var actionPayload = new SidecarSerializedPayload(
            typeof(ExternalInput).AssemblyQualifiedName!,
            descriptor.InputSchema!.Version,
            SidecarCapabilityTransportCodec.ComputeSha256(actionBytes),
            JsonDocument.Parse(actionBytes).RootElement.Clone(),
            actionBytes.Length);
        var descriptorIdentity = new SidecarActionDescriptorIdentity(
            descriptor.Key,
            descriptor.Version,
            descriptor.Category,
            typeof(ExternalInput).AssemblyQualifiedName!,
            descriptor.InputSchema.ContentHash!,
            descriptor.InputSchema.Version,
            typeof(ExternalResult).AssemblyQualifiedName!,
            descriptor.ResultSchema!.ContentHash!,
            descriptor.ResultSchema.Version,
            HostActionEntryAuthorityValidator.ComputeDescriptorHash(descriptor));
        var moduleId = "sidecar.module";
        var graphId = "sidecar.graph";
        var invocationId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var cancellationId = Guid.NewGuid();
        var callId = Guid.NewGuid();
        var deadline = now.AddMinutes(1);
        var caller = new RequestPrincipal("external.caller", Roles: new HashSet<string>(["reader"]));
        var features = ExtensionFeatureSet.Empty;
        var hostContext = new HostActionEntryRequestContext(
            Guid.NewGuid(),
            "external-entry",
            HostActionEntryIngress.CrossModule,
            invocationId,
            requestId,
            cancellationId,
            caller,
            features,
            Guid.NewGuid(),
            Guid.NewGuid(),
            deadline,
            deadline.AddMinutes(1))
        {
            Contribution = new HostActionEntryContribution(
                new HostActionEntryIngressBinding(
                    HostActionEntryIngress.CrossModule,
                    "source.module",
                    moduleId),
                new HostActionEntryLineage(
                    descriptor.Key,
                    descriptor.Version,
                    descriptorIdentity.DescriptorHash,
                    descriptorIdentity.InputTypeIdentity,
                    descriptorIdentity.InputSchemaVersion,
                    descriptorIdentity.InputSchemaHash,
                    null,
                    null)),
            Depth = 0,
            Attempt = 1,
        };
        var call = new SidecarCapabilityCallIdentity(
            Guid.NewGuid(),
            requestId,
            cancellationId,
            callId,
            "external-replay",
            moduleId,
            graphId,
            SidecarCapabilityKind.Action,
            1,
            deadline);
        var cancellation = new SidecarCancellationIdentity(cancellationId, "cancel-authority", deadline.AddMinutes(1));
        var receipt = new SidecarTerminalReceipt(
            "external-receipt",
            descriptor.Key,
            descriptor.Version,
            callId,
            1,
            "external-scope",
            actionPayload.ContentHash);
        var terminal = new SidecarActionTerminalRegistration(
            Guid.NewGuid(),
            descriptorIdentity.InputTypeIdentity,
            descriptorIdentity.InputSchemaVersion,
            descriptorIdentity.ResultTypeIdentity,
            descriptorIdentity.ResultSchemaVersion,
            descriptorIdentity.DescriptorHash);
        var effective = new SidecarActionTerminalExecutionContext(
            call,
            SidecarActionInvocationKind.HostEntry,
            descriptorIdentity,
            actionPayload,
            graph.ActionSnapshot,
            invocationId,
            null,
            0,
            1,
            caller,
            features,
            hostContext.TraceId,
            hostContext.IdempotencyKey,
            cancellation,
            receipt,
            deadline);
        var hostAuthority = new SidecarHostTerminalAuthority(
            Guid.NewGuid(),
            call.SessionId,
            call.RequestId,
            call.CancellationId,
            call.CallId,
            moduleId,
            graphId,
            SidecarActionInvocationKind.HostEntry,
            descriptor.Key,
            descriptor.Version,
            descriptorIdentity.DescriptorHash,
            descriptorIdentity.InputTypeIdentity,
            descriptorIdentity.InputSchemaVersion,
            actionPayload.ContentHash,
            actionPayload.ByteLength,
            receipt.ReceiptId,
            receipt.ActionKey,
            receipt.ActionVersion,
            receipt.CallId,
            receipt.Attempt,
            receipt.IdempotencyScope,
            receipt.ContentHash,
            deadline,
            now,
            deadline.AddMinutes(1),
            "host-proof")
        {
            TerminalId = terminal.TerminalId,
            SnapshotContentHash = SidecarCapabilityTransportValidation.ComputeSnapshotHash(graph.ActionSnapshot),
            Caller = caller,
            Features = features,
            TraceId = hostContext.TraceId,
            IdempotencyKey = hostContext.IdempotencyKey,
            InvocationId = invocationId,
            Depth = 0,
            Attempt = 1,
            HostContextBindingHash = SidecarCapabilityTransportValidation.ComputeHostActionEntryContextBindingHash(hostContext),
        };
        hostAuthority = hostAuthority with
        {
            CanonicalBindingHash = SidecarCapabilityTransportValidation.ComputeTerminalAuthorityBindingHash(hostAuthority),
        };
        var effectiveHostEntry = new SidecarActionEffectiveHostEntryContext(hostContext, effective, hostAuthority);
        return new ExternalFixture(
            descriptor,
            action,
            new SidecarExternalActionDispatchAuthority(
                moduleId,
                graphId,
                call,
                descriptorIdentity,
                actionPayload,
                terminal,
                hostContext,
                effectiveHostEntry));
    }

    private sealed record ExternalInput(string Value);

    private sealed record ExternalResult(string Value);

    private sealed record ExternalFixture(
        ActionDescriptor<ExternalInput, ExternalResult> Descriptor,
        ExternalInput Action,
        SidecarExternalActionDispatchAuthority Authority);

    private static ActionDescriptor<ExternalInput, ExternalResult> ExternalDescriptor(
        ActionInterceptionCapabilities capabilities = ActionInterceptionCapabilities.Inspect,
        bool sensitive = false) =>
        new(
            new SharpClawActionKey("sidecar.external.action"),
            1,
            "sidecar",
            capabilities,
            sensitive,
            false,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "sidecar.external.action"),
            null,
            TimeSpan.FromSeconds(30))
        {
            InputSchema = new JsonSchemaReference("sidecar.external.input", 1, "external-input"),
            ResultSchema = new JsonSchemaReference("sidecar.external.result", 1, "external-result"),
        };

    private static HookOrdering Order(string id) =>
        new(id, HookPriority.Normal, [], [], null, HookFailurePolicy.FailAction);

    private static KernelGraphCompileOptions ExternalPolicyOptions() => new()
    {
        ActionModuleCapabilityGrants = ModuleGrant(ActionInterceptionCapabilities.Inspect)
    };

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
        ModuleGrant(ActionInterceptionCapabilities capabilities) =>
        new Dictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
        {
            ["sidecar.module"] = new Dictionary<string, ActionInterceptionCapabilities>
            {
                ["sidecar.external.action"] = capabilities
            }
        };

    private sealed class ExternalWildcardInterceptor : IAnyActionInterceptor
    {
        public static int Calls;

        public ValueTask<IUntypedActionOutcome> InvokeAsync(
            UntypedActionContext context,
            IUntypedActionControl control,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return control.ProceedAsync(cancellationToken);
        }
    }

    private static SidecarExternalActionDispatchAuthority WithProof(
        SidecarExternalActionDispatchAuthority authority,
        string proof,
        bool recomputeHash = false)
    {
        var hostAuthority = authority.EffectiveHostEntry.Authority with { Proof = proof };
        if (recomputeHash)
            hostAuthority = hostAuthority with
            {
                CanonicalBindingHash = SidecarCapabilityTransportValidation.ComputeTerminalAuthorityBindingHash(hostAuthority)
            };

        return authority with
        {
            EffectiveHostEntry = authority.EffectiveHostEntry with { Authority = hostAuthority }
        };
    }

    private sealed class ExactExternalAuthorityVerifier(
        SidecarExternalActionDispatchAuthority expected)
        : ISidecarExternalActionDispatchAuthorityVerifier
    {
        private int _successfulConsumptions;

        public int SuccessfulConsumptions => Volatile.Read(ref _successfulConsumptions);

        public SidecarCapabilityValidationResult ValidateAndConsume(
            SidecarExternalActionDispatchAuthority authority,
            DateTimeOffset now)
        {
            if (Volatile.Read(ref _successfulConsumptions) != 0)
                return SidecarCapabilityValidationResult.Reject(
                    SidecarCapabilityErrors.Replay,
                    "The external action authority was already consumed.");

            if (authority != expected)
                return SidecarCapabilityValidationResult.Reject(
                    SidecarCapabilityErrors.Unauthenticated,
                    "The external action authority proof is not trusted.");

            if (Interlocked.CompareExchange(ref _successfulConsumptions, 1, 0) != 0)
                return SidecarCapabilityValidationResult.Reject(
                    SidecarCapabilityErrors.Replay,
                    "The external action authority was already consumed.");

            return SidecarCapabilityValidationResult.Accept();
        }
    }
}
