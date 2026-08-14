using SharpClaw.Contracts.Modules;

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
    public const string OwnerModuleId = KernelJobsActionModule.ModuleId;
    public const string Jobs = "jobs";

    public static IReadOnlyList<ModuleStorageContractDescriptor> Contracts { get; } =
    [
        Contract(Jobs)
    ];

    private static ModuleStorageContractDescriptor Contract(string name) =>
        new(
            OwnerModuleId,
            name,
            [
                new(ModuleStorageOperations.Get),
                new(ModuleStorageOperations.List),
                new(ModuleStorageOperations.Query),
                new(ModuleStorageOperations.Upsert),
                new(ModuleStorageOperations.Delete),
                new(ModuleStorageOperations.Claim),
                new(ModuleStorageOperations.RenewClaim),
                new(ModuleStorageOperations.RecoverClaim),
                new(ModuleStorageOperations.MutateAndOutbox)
            ],
            "Canonical Jobs aggregate owned by the host kernel.",
            [
                new("recordType", ModuleStorageIndexValueKind.String),
                new("jobId", ModuleStorageIndexValueKind.String),
                new("actionKey", ModuleStorageIndexValueKind.String),
                new("status", ModuleStorageIndexValueKind.String),
                new("callerSubject", ModuleStorageIndexValueKind.String),
                new("idempotencyKey", ModuleStorageIndexValueKind.String),
                new("createdAt", ModuleStorageIndexValueKind.DateTime, AllowsRange: true),
                new("attemptId", ModuleStorageIndexValueKind.String)
            ]);
}

/// <summary>Registers the complete typed Jobs action catalog and storage declarations.</summary>
public sealed class KernelJobsActionModule : ISharpClawModule
{
    public const string ModuleId = "sharpclaw.core.jobs";

    private readonly Dictionary<string, ActionInterceptionCapabilities> _grants =
        new(StringComparer.Ordinal);
    private readonly List<KernelSensitiveActionApproval> _approvals = [];

    public ModuleIdentity Identity { get; } =
        new(ModuleId, "SharpClaw Core Jobs", "jobs");

    public IReadOnlyDictionary<string, ActionInterceptionCapabilities> Grants => _grants;

    public IReadOnlyList<KernelSensitiveActionApproval> Approvals => _approvals;

    public void Configure(ISharpClawModuleBuilder module)
    {
        ArgumentNullException.ThrowIfNull(module);
        foreach (var contract in KernelJobsStorage.Contracts)
            module.Storage.Add(contract);

        AddFamily<KernelJobsOperationFamilies.Submit>(module, "jobs.submit");
        AddFamily<KernelJobsOperationFamilies.Validate>(module, "jobs.validate");
        AddFamily<KernelJobsOperationFamilies.IdentityCreate>(module, "jobs.identity.create");
        AddFamily<KernelJobsOperationFamilies.QueuePersist>(module, "jobs.queue.persist");
        AddFamily<KernelJobsOperationFamilies.HoldEvaluate>(module, "jobs.hold.evaluate");
        AddFamily<KernelJobsOperationFamilies.HoldResolve>(module, "jobs.hold.resolve");
        AddFamily<KernelJobsOperationFamilies.Dispatch>(module, "jobs.dispatch");
        AddFamily<KernelJobsOperationFamilies.Start>(module, "jobs.start");
        AddFamily<KernelJobsOperationFamilies.HandlerInvoke>(module, "jobs.handler.invoke");
        AddFamily<KernelJobsOperationFamilies.ProgressReport>(module, "jobs.progress.report");
        AddFamily<KernelJobsOperationFamilies.ArtifactSeal>(module, "jobs.artifact.seal");
        AddFamily<KernelJobsOperationFamilies.Complete>(module, "jobs.complete");
        AddFamily<KernelJobsOperationFamilies.Fail>(module, "jobs.fail");
        AddFamily<KernelJobsOperationFamilies.Cancel>(module, "jobs.cancel");
        AddFamily<KernelJobsOperationFamilies.CancelRequest>(module, "jobs.cancel.request");
        AddFamily<KernelJobsOperationFamilies.CancelApply>(module, "jobs.cancel.apply");
        AddFamily<KernelJobsOperationFamilies.Pause>(module, "jobs.pause");
        AddFamily<KernelJobsOperationFamilies.Stop>(module, "jobs.stop");
        AddFamily<KernelJobsOperationFamilies.Recovery>(module, "jobs.recovery");
        AddFamily<KernelJobsOperationFamilies.RecoveryScan>(module, "jobs.recovery.scan");
        AddFamily<KernelJobsOperationFamilies.RecoveryClassify>(module, "jobs.recovery.classify");
        AddFamily<KernelJobsOperationFamilies.Retry>(module, "jobs.retry");
        AddFamily<KernelJobsOperationFamilies.RetryEvaluate>(module, "jobs.retry.evaluate");
        AddFamily<KernelJobsOperationFamilies.RetrySchedule>(module, "jobs.retry.schedule");
        AddFamily<KernelJobsOperationFamilies.Resume>(module, "jobs.resume");
        AddFamily<KernelJobsOperationFamilies.Delete>(module, "jobs.delete");
        AddFamily<KernelJobsOperationFamilies.Read>(module, "jobs.read");
        AddFamily<KernelJobsOperationFamilies.List>(module, "jobs.list");
        AddFamily<KernelJobsOperationFamilies.LogsRead>(module, "jobs.logs.read");
        AddFamily<KernelJobsOperationFamilies.AuditRead>(module, "jobs.audit.read");
        AddFamily<KernelJobsOperationFamilies.ArtifactRead>(module, "jobs.artifact.read");
        AddFamily<KernelJobsOperationFamilies.EventDeliver>(module, "jobs.event.deliver");
        AddFamily<KernelJobsOperationFamilies.StateTransition>(module, "jobs.state.transition");
        AddFamily<KernelJobsOperationFamilies.StateTransitionPrepare>(module, "jobs.state.transition.prepare");
        AddFamily<KernelJobsOperationFamilies.StateTransitionCommit>(module, "jobs.state.transition.commit");
        AddFamily<KernelJobsOperationFamilies.StateTransitionRollback>(module, "jobs.state.transition.rollback");
        AddFamily<KernelJobsOperationFamilies.Persistence>(module, "jobs.persistence");
        AddFamily<KernelJobsOperationFamilies.PersistencePrepare>(module, "jobs.persistence.prepare");
        AddFamily<KernelJobsOperationFamilies.PersistenceCommit>(module, "jobs.persistence.commit");
        AddFamily<KernelJobsOperationFamilies.PersistenceRollback>(module, "jobs.persistence.rollback");
        AddFamily<KernelJobsOperationFamilies.InterruptionCheck>(module, "jobs.interruption.check");
        AddFamily<KernelJobsOperationFamilies.ExternalCall>(module, "jobs.external_call");
        AddFamily<KernelJobsOperationFamilies.IrreversibleEffect>(module, "jobs.irreversible_effect");
        AddFamily<KernelJobsOperationFamilies.ExternalEffectPrepare>(module, "jobs.external_effect.prepare");
        AddFamily<KernelJobsOperationFamilies.ExternalEffectReceipt>(module, "jobs.external_effect.receipt");
        AddFamily<KernelJobsOperationFamilies.ExternalEffectUncertain>(module, "jobs.external_effect.uncertain");
    }

    private void AddFamily<TFamily>(ISharpClawModuleBuilder module, string family)
    {
        if (!SharpClawActionCatalog.JobsFamilies.Contains(family, StringComparer.Ordinal))
            throw new KernelGraphCompilationException(
                $"The Contracts Jobs catalog does not define '{family}'.");

        var contract = KernelJobsActionCatalog.For<TFamily>(new SharpClawActionKey(family));
        AddDescriptor(module, contract.Before);
        AddDescriptor(module, contract.Action);
        AddDescriptor(module, contract.After);
    }

    private void AddDescriptor<TAction, TResult>(
        ISharpClawModuleBuilder module,
        ActionDescriptor<TAction, TResult> descriptor)
    {
        module.Actions.Add(descriptor);
        _grants[descriptor.Key.Value] = descriptor.Capabilities;
        _approvals.Add(new KernelSensitiveActionApproval(
            Identity.Id,
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
