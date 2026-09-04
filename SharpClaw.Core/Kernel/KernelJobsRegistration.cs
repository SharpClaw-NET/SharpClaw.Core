using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Core.Kernel;

/// <summary>Typed action payload for one Core-owned Jobs family.</summary>
public sealed record KernelJobOperationInput<TFamily>(
    JobDocument Job,
    JobProgress? Progress = null);

/// <summary>Typed action result for one Core-owned Jobs family.</summary>
public sealed record KernelJobOperationResult<TFamily>(
    JobDocument Job,
    JobPayloadEnvelope? Output = null,
    JobProgress? Progress = null);

/// <summary>Marker types that give each canonical Jobs family a distinct CLR type.</summary>
public static class KernelJobsOperationFamilies
{
    public sealed record Submit;
    public sealed record Validate;
    public sealed record IdentityCreate;
    public sealed record QueuePersist;
    public sealed record HoldEvaluate;
    public sealed record HoldResolve;
    public sealed record Dispatch;
    public sealed record Start;
    public sealed record HandlerInvoke;
    public sealed record ProgressReport;
    public sealed record ArtifactSeal;
    public sealed record Complete;
    public sealed record Fail;
    public sealed record Cancel;
    public sealed record CancelRequest;
    public sealed record CancelApply;
    public sealed record Pause;
    public sealed record Stop;
    public sealed record Recovery;
    public sealed record RecoveryScan;
    public sealed record RecoveryClassify;
    public sealed record Retry;
    public sealed record RetryEvaluate;
    public sealed record RetrySchedule;
    public sealed record Resume;
    public sealed record Delete;
    public sealed record Read;
    public sealed record List;
    public sealed record LogsRead;
    public sealed record AuditRead;
    public sealed record ArtifactRead;
    public sealed record EventDeliver;
    public sealed record StateTransition;
    public sealed record StateTransitionPrepare;
    public sealed record StateTransitionCommit;
    public sealed record StateTransitionRollback;
    public sealed record Persistence;
    public sealed record PersistencePrepare;
    public sealed record PersistenceCommit;
    public sealed record PersistenceRollback;
    public sealed record InterruptionCheck;
    public sealed record ExternalCall;
    public sealed record IrreversibleEffect;
    public sealed record ExternalEffectPrepare;
    public sealed record ExternalEffectReceipt;
    public sealed record ExternalEffectUncertain;
}

/// <summary>Canonical storage declarations for Core-owned Jobs data.</summary>
public static class KernelJobsStorage
{
    public const string OwnerId = KernelJobsBindings.SourceId;
    public const string Jobs = "jobs";

    public static IReadOnlyList<ScopedStorageContractDescriptor> Contracts { get; } =
    [
        Contract(Jobs)
    ];

    private static ScopedStorageContractDescriptor Contract(string name) =>
        new(
            OwnerId,
            name,
            [
                new(ScopedStorageOperations.Get),
                new(ScopedStorageOperations.List),
                new(ScopedStorageOperations.Query),
                new(ScopedStorageOperations.Upsert),
                new(ScopedStorageOperations.Delete),
                new(ScopedStorageOperations.Claim),
                new(ScopedStorageOperations.RenewClaim),
                new(ScopedStorageOperations.RecoverClaim),
                new(ScopedStorageOperations.MutateAndOutbox)
            ],
            "Canonical Jobs aggregate owned by the host kernel.",
            [
                new("recordType", ScopedStorageIndexValueKind.String),
                new("jobId", ScopedStorageIndexValueKind.String),
                new("actionKey", ScopedStorageIndexValueKind.String),
                new("status", ScopedStorageIndexValueKind.String),
                new("callerSubject", ScopedStorageIndexValueKind.String),
                new("idempotencyKey", ScopedStorageIndexValueKind.String),
                new("createdAt", ScopedStorageIndexValueKind.DateTime, AllowsRange: true),
                new("attemptId", ScopedStorageIndexValueKind.String)
            ]);
}

/// <summary>Adds the complete typed Jobs action catalog to the kernel.</summary>
public sealed class KernelJobsBindings
{
    public const string SourceId = "sharpclaw.core.jobs";

    private readonly Dictionary<string, ActionInterceptionCapabilities> _grants =
        new(StringComparer.Ordinal);
    private readonly List<KernelSensitiveActionApproval> _approvals = [];

    public IReadOnlyDictionary<string, ActionInterceptionCapabilities> Grants => _grants;

    public IReadOnlyList<KernelSensitiveActionApproval> Approvals => _approvals;

    public void AddTo(KernelGraphBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AddFamily<KernelJobsOperationFamilies.Submit>(builder, "jobs.submit");
        AddFamily<KernelJobsOperationFamilies.Validate>(builder, "jobs.validate");
        AddFamily<KernelJobsOperationFamilies.IdentityCreate>(builder, "jobs.identity.create");
        AddFamily<KernelJobsOperationFamilies.QueuePersist>(builder, "jobs.queue.persist");
        AddFamily<KernelJobsOperationFamilies.HoldEvaluate>(builder, "jobs.hold.evaluate");
        AddFamily<KernelJobsOperationFamilies.HoldResolve>(builder, "jobs.hold.resolve");
        AddFamily<KernelJobsOperationFamilies.Dispatch>(builder, "jobs.dispatch");
        AddFamily<KernelJobsOperationFamilies.Start>(builder, "jobs.start");
        AddFamily<KernelJobsOperationFamilies.HandlerInvoke>(builder, "jobs.handler.invoke");
        AddFamily<KernelJobsOperationFamilies.ProgressReport>(builder, "jobs.progress.report");
        AddFamily<KernelJobsOperationFamilies.ArtifactSeal>(builder, "jobs.artifact.seal");
        AddFamily<KernelJobsOperationFamilies.Complete>(builder, "jobs.complete");
        AddFamily<KernelJobsOperationFamilies.Fail>(builder, "jobs.fail");
        AddFamily<KernelJobsOperationFamilies.Cancel>(builder, "jobs.cancel");
        AddFamily<KernelJobsOperationFamilies.CancelRequest>(builder, "jobs.cancel.request");
        AddFamily<KernelJobsOperationFamilies.CancelApply>(builder, "jobs.cancel.apply");
        AddFamily<KernelJobsOperationFamilies.Pause>(builder, "jobs.pause");
        AddFamily<KernelJobsOperationFamilies.Stop>(builder, "jobs.stop");
        AddFamily<KernelJobsOperationFamilies.Recovery>(builder, "jobs.recovery");
        AddFamily<KernelJobsOperationFamilies.RecoveryScan>(builder, "jobs.recovery.scan");
        AddFamily<KernelJobsOperationFamilies.RecoveryClassify>(builder, "jobs.recovery.classify");
        AddFamily<KernelJobsOperationFamilies.Retry>(builder, "jobs.retry");
        AddFamily<KernelJobsOperationFamilies.RetryEvaluate>(builder, "jobs.retry.evaluate");
        AddFamily<KernelJobsOperationFamilies.RetrySchedule>(builder, "jobs.retry.schedule");
        AddFamily<KernelJobsOperationFamilies.Resume>(builder, "jobs.resume");
        AddFamily<KernelJobsOperationFamilies.Delete>(builder, "jobs.delete");
        AddFamily<KernelJobsOperationFamilies.Read>(builder, "jobs.read");
        AddFamily<KernelJobsOperationFamilies.List>(builder, "jobs.list");
        AddFamily<KernelJobsOperationFamilies.LogsRead>(builder, "jobs.logs.read");
        AddFamily<KernelJobsOperationFamilies.AuditRead>(builder, "jobs.audit.read");
        AddFamily<KernelJobsOperationFamilies.ArtifactRead>(builder, "jobs.artifact.read");
        AddFamily<KernelJobsOperationFamilies.EventDeliver>(builder, "jobs.event.deliver");
        AddFamily<KernelJobsOperationFamilies.StateTransition>(builder, "jobs.state.transition");
        AddFamily<KernelJobsOperationFamilies.StateTransitionPrepare>(builder, "jobs.state.transition.prepare");
        AddFamily<KernelJobsOperationFamilies.StateTransitionCommit>(builder, "jobs.state.transition.commit");
        AddFamily<KernelJobsOperationFamilies.StateTransitionRollback>(builder, "jobs.state.transition.rollback");
        AddFamily<KernelJobsOperationFamilies.Persistence>(builder, "jobs.persistence");
        AddFamily<KernelJobsOperationFamilies.PersistencePrepare>(builder, "jobs.persistence.prepare");
        AddFamily<KernelJobsOperationFamilies.PersistenceCommit>(builder, "jobs.persistence.commit");
        AddFamily<KernelJobsOperationFamilies.PersistenceRollback>(builder, "jobs.persistence.rollback");
        AddFamily<KernelJobsOperationFamilies.InterruptionCheck>(builder, "jobs.interruption.check");
        AddFamily<KernelJobsOperationFamilies.ExternalCall>(builder, "jobs.external_call");
        AddFamily<KernelJobsOperationFamilies.IrreversibleEffect>(builder, "jobs.irreversible_effect");
        AddFamily<KernelJobsOperationFamilies.ExternalEffectPrepare>(builder, "jobs.external_effect.prepare");
        AddFamily<KernelJobsOperationFamilies.ExternalEffectReceipt>(builder, "jobs.external_effect.receipt");
        AddFamily<KernelJobsOperationFamilies.ExternalEffectUncertain>(builder, "jobs.external_effect.uncertain");
    }

    private void AddFamily<TFamily>(KernelGraphBuilder builder, string family)
    {
        if (!SharpClawActionCatalog.JobsFamilies.Contains(family, StringComparer.Ordinal))
            throw new KernelGraphCompilationException(
                $"The Contracts Jobs catalog does not define '{family}'.");

        var contract = KernelJobsActionCatalog.For<TFamily>(new SharpClawActionKey(family));
        AddDescriptor(builder, contract.Before);
        AddDescriptor(builder, contract.Action);
        AddDescriptor(builder, contract.After);
    }

    private void AddDescriptor<TAction, TResult>(
        KernelGraphBuilder builder,
        ActionDescriptor<TAction, TResult> descriptor)
    {
        builder.Add(descriptor, SourceId);
        _grants[descriptor.Key.Value] = descriptor.Capabilities;
        _approvals.Add(new KernelSensitiveActionApproval(
            SourceId,
            descriptor.Key,
            descriptor.Version,
            typeof(TAction).AssemblyQualifiedName!,
            typeof(TResult).AssemblyQualifiedName!,
            KernelSchemaIdentity.Action(descriptor)));
    }
}

/// <summary>Creates typed before, root, and after descriptors for one Jobs family.</summary>
public static class KernelJobsActionCatalog
{
    public static JobActionContract<
        KernelJobOperationInput<TFamily>,
        KernelJobOperationResult<TFamily>> For<TFamily>(SharpClawActionKey key)
    {
        if (!SharpClawActionCatalog.JobsFamilies.Contains(key.Value, StringComparer.Ordinal))
            throw new KernelGraphCompilationException(
                $"Action '{key.Value}' is not a canonical Jobs family root.");

        return new(
            Descriptor<
                JobCheckpoint<KernelJobOperationInput<TFamily>>,
                JobCheckpoint<KernelJobOperationInput<TFamily>>>($"{key.Value}.before"),
            Descriptor<KernelJobOperationInput<TFamily>, KernelJobOperationResult<TFamily>>(key.Value),
            Descriptor<
                JobCheckpoint<KernelJobOperationResult<TFamily>>,
                JobCheckpoint<KernelJobOperationResult<TFamily>>>($"{key.Value}.after"));
    }

    private static ActionDescriptor<TAction, TResult> Descriptor<TAction, TResult>(string key)
    {
        var entry = KernelActionCatalog.DescriptorFor(new SharpClawActionKey(key));
        return new(
            entry.Key,
            entry.Version,
            entry.Category,
            entry.Capabilities,
            entry.ContainsSensitiveData,
            entry.HasIrreversibleEffects,
            entry.RepeatPolicy,
            entry.ContinuationPolicy,
            entry.DefaultTimeout)
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            SafePoints = entry.SafePoints
        };
    }
}
