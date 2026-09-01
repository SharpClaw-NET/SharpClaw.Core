using System.Security.Cryptography;
using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

public sealed class KernelExternalActionDispatchTests
{
    [Fact]
    public async Task External_serialized_action_uses_discovery_identity_and_rejects_replay()
    {
        var graph = new KernelGraphBuilder(false).Compile();
        var descriptor = ExternalDescriptor(sensitive: true);
        var snapshot = ExternalSnapshot(graph, descriptor, sensitiveApproved: true);
        var fixture = CreateFixture(graph, descriptor, snapshot);
        using var registry = CreateRegistry(fixture);
        IActionDispatcher dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityRegistry: registry);
        var definition = ExternalDefinition(descriptor);
        var action = fixture.Authority.Action.Value.Clone();
        var terminalCalls = 0;

        var accepted = await dispatcher.RunExternalSerializedAsync(
            definition,
            fixture.Authority.Descriptor,
            action,
            (context, _) =>
            {
                terminalCalls++;
                Assert.Equal("payload-a", context.Action.GetProperty("value").GetString());
                Assert.Equal(fixture.Authority.ModuleId, context.OwnerModuleId);
                Assert.Equal(snapshot, context.Snapshot);
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(new ExternalResult("serialized")));
            },
            snapshot,
            SessionProof(fixture.Authority),
            CancellationToken.None);
        var replay = await dispatcher.RunExternalSerializedAsync(
            definition,
            fixture.Authority.Descriptor,
            action,
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(new ExternalResult("replay")));
            },
            snapshot,
            SessionProof(fixture.Authority),
            CancellationToken.None);

        Assert.True(
            accepted.Kind == ActionOutcomeKind.Completed,
            $"Serialized dispatch failed: {accepted.Error?.Code} {accepted.Error?.Message}");
        Assert.Equal("serialized", accepted.Result.GetProperty("Value").GetString());
        Assert.Equal(ActionOutcomeKind.Failed, replay.Kind);
        Assert.Equal(SidecarCapabilityErrors.Replay, replay.Error?.Code);
        Assert.Equal(1, terminalCalls);
    }

    [Fact]
    public async Task External_serialized_action_rejects_changed_semantics_before_consuming_authority()
    {
        var graph = new KernelGraphBuilder(false).Compile();
        var descriptor = ExternalDescriptor();
        var snapshot = ExternalSnapshot(graph, descriptor);
        var fixture = CreateFixture(graph, descriptor, snapshot);
        using var registry = CreateRegistry(fixture);
        IActionDispatcher dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityRegistry: registry);
        var definition = ExternalDefinition(descriptor);
        var action = fixture.Authority.Action.Value.Clone();
        var terminalCalls = 0;

        var rejected = await dispatcher.RunExternalSerializedAsync(
            definition with { HasIrreversibleEffects = true },
            fixture.Authority.Descriptor,
            action,
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(new ExternalResult("invalid")));
            },
            snapshot,
            SessionProof(fixture.Authority),
            CancellationToken.None);
        var accepted = await dispatcher.RunExternalSerializedAsync(
            definition,
            fixture.Authority.Descriptor,
            action,
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(new ExternalResult("accepted")));
            },
            snapshot,
            SessionProof(fixture.Authority),
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, rejected.Kind);
        Assert.Equal("sidecar_external_invalid_descriptor", rejected.Error?.Code);
        Assert.True(
            accepted.Kind == ActionOutcomeKind.Completed,
            $"Serialized dispatch failed: {accepted.Error?.Code} {accepted.Error?.Message}");
        Assert.Equal(1, terminalCalls);
    }

    [Fact]
    public async Task External_serialized_action_adds_its_exact_global_grant_to_an_existing_host_policy()
    {
        var localKey = new SharpClawActionKey("local.host.action");
        var builder = new KernelGraphBuilder(false);
        builder.Add(LocalDescriptor(localKey));
        var graph = builder.Compile(options: new KernelGraphCompileOptions
        {
            ActionCapabilityGrants = new Dictionary<string, ActionInterceptionCapabilities>
            {
                [localKey.Value] = ActionInterceptionCapabilities.Inspect,
            },
        });
        var descriptor = ExternalDescriptor();
        var snapshot = ExternalSnapshot(graph, descriptor);
        var fixture = CreateFixture(graph, descriptor, snapshot);
        using var registry = CreateRegistry(fixture);
        IActionDispatcher dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityRegistry: registry);
        var terminalCalls = 0;

        var outcome = await dispatcher.RunExternalSerializedAsync(
            ExternalDefinition(descriptor),
            fixture.Authority.Descriptor,
            fixture.Authority.Action.Value.Clone(),
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(
                    new ExternalResult("accepted")));
            },
            snapshot,
            SessionProof(fixture.Authority),
            CancellationToken.None);

        Assert.True(
            outcome.Kind == ActionOutcomeKind.Completed,
            $"Serialized dispatch failed: {outcome.Error?.Code} {outcome.Error?.Message}");
        Assert.Equal(1, terminalCalls);
    }

    [Fact]
    public async Task External_action_uses_the_singleton_dispatcher_without_a_local_descriptor()
    {
        var builder = new KernelGraphBuilder(false);
        var localKey = new SharpClawActionKey("local.host.action");
        builder.Add(LocalDescriptor(localKey));
        var graph = builder.Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        using var registry = CreateRegistry(fixture);
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityRegistry: registry);
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
            SessionProof(fixture.Authority),
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
    public async Task External_action_uses_dispatcher_owned_session_registration_and_rejects_replay()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        using var registry = CreateRegistry(fixture);
        IActionDispatcher dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityRegistry: registry);
        var terminalCalls = 0;

        var forged = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (_, _) =>
            {
                Interlocked.Increment(ref terminalCalls);
                return ValueTask.FromResult(new ExternalResult("forged"));
            },
            graph.ActionSnapshot,
            WithProof(fixture.Authority, "forged-proof", recomputeHash: true),
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, forged.Kind);
        Assert.Equal(SidecarCapabilityErrors.Unauthenticated, forged.Error?.Code);
        Assert.Equal(0, terminalCalls);

        var accepted = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (_, _) =>
            {
                Interlocked.Increment(ref terminalCalls);
                return ValueTask.FromResult(new ExternalResult("accepted"));
            },
            graph.ActionSnapshot,
            SessionProof(fixture.Authority),
            CancellationToken.None);

        var replay = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (_, _) =>
            {
                Interlocked.Increment(ref terminalCalls);
                return ValueTask.FromResult(new ExternalResult("replayed"));
            },
            graph.ActionSnapshot,
            SessionProof(fixture.Authority),
            CancellationToken.None);

        Assert.True(
            accepted.Kind == ActionOutcomeKind.Completed,
            $"Accepted dispatch failed: {accepted.Error?.Code} {accepted.Error?.Message}");
        Assert.Equal(ActionOutcomeKind.Failed, replay.Kind);
        Assert.Equal(SidecarCapabilityErrors.Replay, replay.Error?.Code);
        Assert.Equal(1, terminalCalls);
    }

    [Fact]
    public async Task External_action_requires_registered_session_after_registration_disposal()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        var session = CreateSessionVerifier(fixture);
        using var registry = new KernelExternalAuthoritySessionRegistry();
        var registration = registry.Register(session);
        IActionDispatcher dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityRegistry: registry);
        registration.Dispose();
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
            SessionProof(fixture.Authority),
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("ACTION_EXTERNAL_AUTHORITY_UNAVAILABLE", outcome.Error?.Code);
        Assert.Equal(0, terminalCalls);
    }

    [Fact]
    public async Task External_action_rejects_fabricated_authority_with_a_real_registered_session()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        using var registry = CreateRegistry(fixture);
        IActionDispatcher dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityRegistry: registry);
        var terminalCalls = 0;

        var fabricated = WithProof(fixture.Authority, "always-accept-proof", recomputeHash: true);
        var outcome = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(new ExternalResult("unexpected"));
            },
            graph.ActionSnapshot,
            fabricated,
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(SidecarCapabilityErrors.Unauthenticated, outcome.Error?.Code);
        Assert.Equal(0, terminalCalls);
    }

    [Fact]
    public async Task External_action_rejects_missing_or_changed_authority_before_terminal()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        using var registry = CreateRegistry(fixture);
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityRegistry: registry);
        var terminalCalls = 0;

        var cases = new[]
        {
            ("missing", (SidecarExternalActionDispatchAuthority)null!),
            ("module", SessionProof(fixture.Authority) with { ModuleId = "other.module" }),
            ("graph", SessionProof(fixture.Authority) with { GraphId = "other.graph" }),
            ("descriptor", SessionProof(fixture.Authority) with
            {
                Descriptor = fixture.Authority.Descriptor with { DescriptorHash = "changed" }
            }),
            ("terminal", SessionProof(fixture.Authority) with
            {
                Terminal = fixture.Authority.Terminal with { TerminalId = Guid.NewGuid() }
            }),
            ("host-context", SessionProof(fixture.Authority) with
            {
                InitiatingHostContext = fixture.Authority.InitiatingHostContext with
                {
                    Caller = new RequestPrincipal("other.caller")
                }
            }),
            ("snapshot", SessionProof(fixture.Authority)),
            ("stale", SessionProof(fixture.Authority) with
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
                SessionProof(fixture.Authority),
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
            SessionProof(fixture.Authority),
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, changedPayload.Kind);
        Assert.Equal(0, terminalCalls);
    }

    [Fact]
    public async Task External_authority_is_consumed_once_and_requires_trusted_verification()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        using var registry = CreateRegistry(fixture);
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityRegistry: registry);
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
            SessionProof(fixture.Authority),
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
            SessionProof(fixture.Authority),
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Completed, first.Kind);
        Assert.Equal(ActionOutcomeKind.Failed, second.Kind);
        Assert.Equal(SidecarCapabilityErrors.Replay, second.Error?.Code);
        Assert.Equal(1, terminalCalls);
    }

    [Fact]
    public async Task External_authority_allows_one_dispatch_under_concurrent_replay()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        using var registry = CreateRegistry(fixture);
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityRegistry: registry);
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
                SessionProof(fixture.Authority),
                CancellationToken.None);

        var outcomes = await Task.WhenAll(DispatchAsync(), DispatchAsync());

        Assert.Equal(1, outcomes.Count(outcome => outcome.Kind == ActionOutcomeKind.Completed));
        Assert.Equal(1, outcomes.Count(outcome => outcome.Kind == ActionOutcomeKind.Failed));
        Assert.Equal(1, terminalCalls);
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
            SessionProof(fixture.Authority),
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("ACTION_EXTERNAL_AUTHORITY_UNAVAILABLE", outcome.Error?.Code);
        Assert.Equal(0, terminalCalls);
    }

    [Fact]
    public async Task Caller_created_permissive_session_cannot_register_through_dispatcher_contract()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        var permissiveSession = CreateSessionVerifier(fixture, permissive: true);
        IActionDispatcher dispatcher = KernelTestExecution.CreateDispatcher(graph);
        var dispatcherMethods = typeof(IActionDispatcher).GetMethods();
        var terminalCalls = 0;

        Assert.DoesNotContain(
            dispatcherMethods,
            method => method.Name == "RegisterExternalAuthoritySession");

        var outcome = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(new ExternalResult("unexpected"));
            },
            graph.ActionSnapshot,
            SessionProof(fixture.Authority),
            CancellationToken.None);

        Assert.NotNull(permissiveSession);
        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("ACTION_EXTERNAL_AUTHORITY_UNAVAILABLE", outcome.Error?.Code);
        Assert.Equal(0, terminalCalls);
    }

    [Fact]
    public async Task External_action_removes_disconnected_session_registration()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        var session = CreateSessionVerifier(fixture);
        using var registry = new KernelExternalAuthoritySessionRegistry();
        registry.Register(session);
        session.Disconnect();
        IActionDispatcher dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityRegistry: registry);
        var terminalCalls = 0;

        var first = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(new ExternalResult("unexpected"));
            },
            graph.ActionSnapshot,
            SessionProof(fixture.Authority),
            CancellationToken.None);
        var second = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(new ExternalResult("unexpected"));
            },
            graph.ActionSnapshot,
            SessionProof(fixture.Authority),
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, first.Kind);
        Assert.Equal(SidecarCapabilityErrors.Disconnected, first.Error?.Code);
        Assert.Equal(ActionOutcomeKind.Failed, second.Kind);
        Assert.Equal("ACTION_EXTERNAL_AUTHORITY_UNAVAILABLE", second.Error?.Code);
        Assert.Equal(0, terminalCalls);
    }

    [Fact]
    public void External_registry_removes_expired_session_registration()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        var session = CreateSessionVerifier(fixture);
        using var registry = new KernelExternalAuthoritySessionRegistry();
        registry.Register(session);
        var expired = registry.ValidateAndConsume(
            SessionProof(fixture.Authority),
            session.Binding.ExpiresAt.AddSeconds(1));
        var removed = registry.ValidateAndConsume(
            SessionProof(fixture.Authority),
            DateTimeOffset.UtcNow);

        Assert.Equal(SidecarCapabilityErrors.Expired, expired.Code);
        Assert.Equal("ACTION_EXTERNAL_AUTHORITY_UNAVAILABLE", removed.Code);
    }

    [Fact]
    public void External_registry_removes_registration_after_binding_rotation()
    {
        var graph = new KernelGraphBuilder(false).Compile(options: ExternalPolicyOptions());
        var fixture = CreateFixture(graph);
        var session = CreateSessionVerifier(fixture, permissive: true);
        using var registry = new KernelExternalAuthoritySessionRegistry();
        registry.Register(session);
        Assert.True(session.CompleteCall(fixture.Authority.Call.CallId, 0).Accepted);

        var now = DateTimeOffset.UtcNow;
        var replacementExpiry = now.AddMinutes(5);
        var replacement = session.Binding with
        {
            SessionId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            CancellationId = Guid.NewGuid(),
            ExpiresAt = replacementExpiry,
            Grant = session.Binding.Grant with { ExpiresAt = replacementExpiry },
            Authentication = session.Binding.Authentication with
            {
                Nonce = Guid.NewGuid().ToString("N"),
                IssuedAt = now,
                ExpiresAt = replacementExpiry,
                BindingHash = string.Empty,
            },
        };
        replacement = replacement with
        {
            Authentication = replacement.Authentication with
            {
                BindingHash = SidecarCapabilitySessionValidator.ComputeBindingHash(replacement),
            },
        };

        Assert.True(session.RotateBinding(replacement, now).Accepted);
        var result = registry.ValidateAndConsume(
            SessionProof(fixture.Authority),
            now);

        Assert.Equal(SidecarCapabilityErrors.InvalidBinding, result.Code);
        Assert.Equal(
            "ACTION_EXTERNAL_AUTHORITY_UNAVAILABLE",
            registry.ValidateAndConsume(SessionProof(fixture.Authority), now).Code);
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
            externalAuthorityRegistry: CreateRegistry(fixture));
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
            SessionProof(fixture.Authority),
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
            externalAuthorityRegistry: CreateRegistry(fixture));
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
            SessionProof(fixture.Authority),
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
            externalAuthorityRegistry: CreateRegistry(fixture));
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
            SessionProof(fixture.Authority),
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
            externalAuthorityRegistry: CreateRegistry(fixture));
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
            SessionProof(fixture.Authority),
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("ACTION_EXTERNAL_POLICY_REJECTED", outcome.Error?.Code);
        Assert.Equal(0, terminalCalls);
    }

    [Fact]
    public async Task External_policy_freezes_mutable_grants_and_sensitive_approvals()
    {
        var moduleGrants = new Dictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
        {
            ["sidecar.module"] = new Dictionary<string, ActionInterceptionCapabilities>
            {
                ["sidecar.external.action"] = ActionInterceptionCapabilities.Inspect
            }
        };
        var approvals = new List<KernelSensitiveActionApproval>();
        var options = new KernelGraphCompileOptions
        {
            ActionModuleCapabilityGrants = moduleGrants,
            SensitiveActionApprovals = approvals
        };
        var graph = new KernelGraphBuilder(false).Compile(options: options);
        var snapshotHash = graph.ActionSnapshot.ContractHash;
        var descriptor = ExternalDescriptor(
            ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel,
            sensitive: true);
        var fixture = CreateFixture(graph, descriptor);
        using var registry = CreateRegistry(fixture);
        var dispatcher = KernelTestExecution.CreateDispatcher(
            graph,
            externalAuthorityRegistry: registry);
        var terminalCalls = 0;

        moduleGrants["sidecar.module"] = new Dictionary<string, ActionInterceptionCapabilities>
        {
            ["sidecar.external.action"] =
                ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Cancel
        };
        approvals.Add(new KernelSensitiveActionApproval(
            "sidecar.module",
            descriptor.Key,
            descriptor.Version,
            typeof(ExternalInput).AssemblyQualifiedName!,
            typeof(ExternalResult).AssemblyQualifiedName!,
            KernelSchemaIdentity.Action(descriptor)));

        var outcome = await dispatcher.RunExternalAsync(
            fixture.Descriptor,
            fixture.Action,
            (_, _) =>
            {
                terminalCalls++;
                return ValueTask.FromResult(new ExternalResult("unexpected"));
            },
            graph.ActionSnapshot,
            SessionProof(fixture.Authority),
            CancellationToken.None);

        Assert.Equal(ActionOutcomeKind.Failed, outcome.Kind);
        Assert.Equal("ACTION_EXTERNAL_POLICY_REJECTED", outcome.Error?.Code);
        Assert.Equal(0, terminalCalls);
        Assert.Equal(snapshotHash, graph.ActionSnapshot.ContractHash);
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
        ActionDescriptor<ExternalInput, ExternalResult>? descriptorOverride = null,
        ActionPipelineSnapshot? snapshotOverride = null)
    {
        var now = DateTimeOffset.UtcNow;
        var descriptor = descriptorOverride ?? ExternalDescriptor();
        var snapshot = snapshotOverride ?? graph.ActionSnapshot;
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
            snapshot,
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
            SnapshotContentHash = SidecarCapabilityTransportValidation.ComputeSnapshotHash(snapshot),
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
            snapshot,
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
        ActionPipelineSnapshot Snapshot,
        SidecarExternalActionDispatchAuthority Authority);

    private static SidecarActionDefinition ExternalDefinition(
        ActionDescriptor<ExternalInput, ExternalResult> descriptor) =>
        new(
            descriptor.Key,
            descriptor.Version,
            descriptor.Category,
            descriptor.InputSchema!,
            descriptor.ResultSchema!,
            descriptor.Capabilities,
            descriptor.ContainsSensitiveData,
            descriptor.HasIrreversibleEffects,
            descriptor.RepeatPolicy,
            descriptor.ContinuationPolicy,
            descriptor.DefaultTimeout,
            descriptor.SafePoints,
            descriptor.ProtocolVersionRange);

    private static ActionPipelineSnapshot ExternalSnapshot(
        KernelGraph graph,
        ActionDescriptor<ExternalInput, ExternalResult> descriptor,
        bool sensitiveApproved = false) =>
        new(
            graph.ActionSnapshot.ContractHash,
            [new ActionCapabilityGrant(
                descriptor.Key,
                descriptor.Version,
                descriptor.Capabilities,
                sensitiveApproved || !descriptor.ContainsSensitiveData)],
            graph.ActionSnapshot.EventGrants,
            graph.ActionSnapshot.MaximumActionDepth);

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

    private static SidecarExternalActionDispatchAuthority SessionProof(
        SidecarExternalActionDispatchAuthority authority)
    {
        var hostAuthority = authority.EffectiveHostEntry.Authority with
        {
            Proof = authority.EffectiveHostEntry.Authority.CanonicalBindingHash,
        };
        return authority with
        {
            EffectiveHostEntry = authority.EffectiveHostEntry with
            {
                Authority = hostAuthority,
            },
        };
    }

    private static SidecarCapabilitySession CreateSessionVerifier(
        ExternalFixture fixture,
        bool permissive = false)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(5);
        var proof = new SidecarAuthenticationProof(
            "hmac-sha256",
            "core-test-host",
            "session-nonce",
            "signature",
            string.Empty,
            now,
            expiresAt);
        var binding = new SidecarCapabilitySessionBinding(
            fixture.Authority.ModuleId,
            fixture.Authority.GraphId,
            1,
            new SidecarCapabilityGrant(
                "core-test-grant",
                fixture.Authority.ModuleId,
                fixture.Authority.GraphId,
                [SidecarCapabilityKind.Action],
                "authorization-hash",
                now.AddMinutes(-1),
                expiresAt),
            fixture.Authority.Call.SessionId,
            fixture.Authority.Call.RequestId,
            fixture.Authority.Call.CancellationId,
            expiresAt,
            new SidecarPayloadLimits(4096, 4096, 4096, 8192, 1024),
            new SidecarConcurrencyLimits(2, 4),
            new SidecarSafeFailureIdentity(
                Guid.NewGuid(),
                "core.test.failure",
                "The test failure is safe."),
            "core-test-host",
            proof);
        binding = binding with
        {
            Authentication = proof with
            {
                BindingHash = SidecarCapabilitySessionValidator.ComputeBindingHash(binding),
            },
        };
        var bindingHash = binding.Authentication.BindingHash;
        var nonces = new HashSet<string>(StringComparer.Ordinal);
        var session = new SidecarCapabilitySession(
            binding,
            permissive
                ? static _ => true
                : authority => string.Equals(authority.BindingHash, bindingHash, StringComparison.Ordinal),
            nonces.Add,
            now,
            permissive
                ? static (_, _) => true
                : static (authority, hash) => string.Equals(authority.Proof, hash, StringComparison.Ordinal));
        var begin = session.BeginCall(
            fixture.Authority.Call,
            SidecarCapabilityKind.Action,
            fixture.Authority.Action,
            fixture.Authority.Action.ByteLength,
            now);
        Assert.True(begin.Accepted, begin.Message);
        return session;
    }

    private static KernelExternalAuthoritySessionRegistry CreateRegistry(ExternalFixture fixture)
    {
        var registry = new KernelExternalAuthoritySessionRegistry();
        registry.Register(CreateSessionVerifier(fixture));
        return registry;
    }
}
