using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Kernel;

public enum KernelStandardActionProfile
{
    Pure,
    Deferrable,
    Effect,
    IdempotentEffect,
    ConflictEffect,
    ReceiptedEffect,
    Stream,
    StreamEffect,
    ReceiptedStreamEffect,
    Signal,
    Progress,
    Observe
}

public sealed record KernelStandardActionManifestEntry(
    SharpClawActionKey Key,
    int Version,
    string Category,
    Type InputPayloadType,
    Type ResultPayloadType,
    JsonSchemaReference InputSchema,
    JsonSchemaReference ResultSchema,
    ActionInterceptionCapabilities Capabilities,
    bool ContainsSensitiveData,
    bool HasIrreversibleEffects,
    ActionRepeatPolicy RepeatPolicy,
    ActionContinuationPolicy? ContinuationPolicy,
    TimeSpan DefaultTimeout,
    IReadOnlyList<ActionSafePoint> SafePoints,
    KernelStandardActionProfile Profile)
{
    public bool IsJobsAction => Key.Value.StartsWith("jobs.", StringComparison.Ordinal);

    public bool IsJobsBeforeAction =>
        IsJobsAction && Key.Value.EndsWith(".before", StringComparison.Ordinal);

    public bool IsJobsAfterAction =>
        IsJobsAction && Key.Value.EndsWith(".after", StringComparison.Ordinal);

    public ActionDescriptor<KernelActionEnvelope, object> ToDescriptor()
    {
        if (IsJobsAction)
            throw new KernelGraphCompilationException(
                $"Jobs action '{Key.Value}' requires its typed standard descriptor.");

        return new(
            Key,
            Version,
            Category,
            Capabilities,
            ContainsSensitiveData,
            HasIrreversibleEffects,
            RepeatPolicy,
            ContinuationPolicy,
            DefaultTimeout)
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            SafePoints = SafePoints
        };
    }

    public ActionDescriptor<
        JobActionInput<JsonElement>,
        JobActionResult<JsonElement>> ToJobsActionDescriptor()
    {
        if (!IsJobsAction || IsJobsBeforeAction || IsJobsAfterAction)
            throw new KernelGraphCompilationException(
                $"Action '{Key.Value}' is not a Jobs root action.");

        return CreateDescriptor<
            JobActionInput<JsonElement>,
            JobActionResult<JsonElement>>();
    }

    public ActionDescriptor<
        JobCheckpoint<JobActionInput<JsonElement>>,
        JobCheckpoint<JobActionInput<JsonElement>>> ToJobsBeforeDescriptor()
    {
        if (!IsJobsBeforeAction)
            throw new KernelGraphCompilationException(
                $"Action '{Key.Value}' is not a Jobs before checkpoint.");

        return CreateDescriptor<
            JobCheckpoint<JobActionInput<JsonElement>>,
            JobCheckpoint<JobActionInput<JsonElement>>>();
    }

    public ActionDescriptor<
        JobCheckpoint<JobActionResult<JsonElement>>,
        JobCheckpoint<JobActionResult<JsonElement>>> ToJobsAfterDescriptor()
    {
        if (!IsJobsAfterAction)
            throw new KernelGraphCompilationException(
                $"Action '{Key.Value}' is not a Jobs after checkpoint.");

        return CreateDescriptor<
            JobCheckpoint<JobActionResult<JsonElement>>,
            JobCheckpoint<JobActionResult<JsonElement>>>();
    }

    private ActionDescriptor<TAction, TResult> CreateDescriptor<TAction, TResult>() =>
        new(
            Key,
            Version,
            Category,
            Capabilities,
            ContainsSensitiveData,
            HasIrreversibleEffects,
            RepeatPolicy,
            ContinuationPolicy,
            DefaultTimeout)
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            SafePoints = SafePoints
        };

    public bool MatchesDescriptor<TAction, TResult>(ActionDescriptor<TAction, TResult> descriptor) =>
        descriptor.Key == Key &&
        descriptor.Version == Version &&
        descriptor.Category == Category &&
        ((typeof(TAction) == InputPayloadType && typeof(TResult) == ResultPayloadType) ||
         (!IsJobsAction &&
          typeof(TAction) == typeof(KernelActionEnvelope) &&
          typeof(TResult) == typeof(object))) &&
        descriptor.Capabilities == Capabilities &&
        descriptor.ContainsSensitiveData == ContainsSensitiveData &&
        descriptor.HasIrreversibleEffects == HasIrreversibleEffects &&
        descriptor.RepeatPolicy == RepeatPolicy &&
        descriptor.ContinuationPolicy == ContinuationPolicy &&
        descriptor.DefaultTimeout == DefaultTimeout &&
        descriptor.ProtocolVersionRange == ContractVersionRange.Exact(1) &&
        descriptor.SafePoints.SequenceEqual(SafePoints);
}

internal static class KernelStandardActionManifest
{
    private sealed record JobsProfileSet(
        KernelStandardActionProfile Root,
        KernelStandardActionProfile Before,
        KernelStandardActionProfile After);

    private static readonly IReadOnlyDictionary<string, JobsProfileSet> JobsFamilyProfiles =
        new ReadOnlyDictionary<string, JobsProfileSet>(
            new Dictionary<string, JobsProfileSet>(StringComparer.Ordinal)
            {
                ["jobs.submit"] = new(KernelStandardActionProfile.IdempotentEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.validate"] = new(KernelStandardActionProfile.Pure, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.identity.create"] = new(KernelStandardActionProfile.Pure, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.queue.persist"] = new(KernelStandardActionProfile.IdempotentEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.hold.evaluate"] = new(KernelStandardActionProfile.Pure, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.hold.resolve"] = new(KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.dispatch"] = new(KernelStandardActionProfile.ConflictEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.start"] = new(KernelStandardActionProfile.ConflictEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.handler.invoke"] = new(KernelStandardActionProfile.ReceiptedEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.progress.report"] = new(KernelStandardActionProfile.Progress, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.artifact.seal"] = new(KernelStandardActionProfile.IdempotentEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.complete"] = new(KernelStandardActionProfile.ConflictEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.fail"] = new(KernelStandardActionProfile.ConflictEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.cancel"] = new(KernelStandardActionProfile.ConflictEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.cancel.request"] = new(KernelStandardActionProfile.Signal, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.cancel.apply"] = new(KernelStandardActionProfile.ConflictEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.pause"] = new(KernelStandardActionProfile.Signal, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.stop"] = new(KernelStandardActionProfile.Signal, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.recovery"] = new(KernelStandardActionProfile.ConflictEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.recovery.scan"] = new(KernelStandardActionProfile.Pure, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.recovery.classify"] = new(KernelStandardActionProfile.Pure, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.retry"] = new(KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.retry.evaluate"] = new(KernelStandardActionProfile.Pure, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.retry.schedule"] = new(KernelStandardActionProfile.ConflictEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.resume"] = new(KernelStandardActionProfile.ConflictEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.delete"] = new(KernelStandardActionProfile.ConflictEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.read"] = new(KernelStandardActionProfile.Pure, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.list"] = new(KernelStandardActionProfile.Pure, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.logs.read"] = new(KernelStandardActionProfile.Pure, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.audit.read"] = new(KernelStandardActionProfile.Pure, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.artifact.read"] = new(KernelStandardActionProfile.Pure, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.event.deliver"] = new(KernelStandardActionProfile.ReceiptedEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.state.transition"] = new(KernelStandardActionProfile.ConflictEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.state.transition.prepare"] = new(KernelStandardActionProfile.Pure, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.state.transition.commit"] = new(KernelStandardActionProfile.ConflictEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.state.transition.rollback"] = new(KernelStandardActionProfile.IdempotentEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.persistence"] = new(KernelStandardActionProfile.ConflictEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.persistence.prepare"] = new(KernelStandardActionProfile.Pure, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.persistence.commit"] = new(KernelStandardActionProfile.ConflictEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.persistence.rollback"] = new(KernelStandardActionProfile.IdempotentEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.interruption.check"] = new(KernelStandardActionProfile.Pure, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.external_call"] = new(KernelStandardActionProfile.ReceiptedEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.irreversible_effect"] = new(KernelStandardActionProfile.ReceiptedEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.external_effect.prepare"] = new(KernelStandardActionProfile.Pure, KernelStandardActionProfile.Pure, KernelStandardActionProfile.Observe),
                ["jobs.external_effect.receipt"] = new(KernelStandardActionProfile.ReceiptedEffect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe),
                ["jobs.external_effect.uncertain"] = new(KernelStandardActionProfile.Effect, KernelStandardActionProfile.Deferrable, KernelStandardActionProfile.Observe)
            });

    private static readonly IReadOnlyDictionary<string, KernelStandardActionProfile> Profiles =
        BuildProfiles();

    private static IReadOnlyDictionary<string, KernelStandardActionProfile> BuildProfiles()
    {
        var profiles = new Dictionary<string, KernelStandardActionProfile>(StringComparer.Ordinal)
            {
                ["runtime.start.prepare"] = KernelStandardActionProfile.Pure,
                ["runtime.start.configure"] = KernelStandardActionProfile.Pure,
                ["runtime.start.bind"] = KernelStandardActionProfile.Pure,
                ["runtime.stop.prepare"] = KernelStandardActionProfile.Pure,
                ["runtime.stop.complete"] = KernelStandardActionProfile.IdempotentEffect,
                ["runtime.request.receive"] = KernelStandardActionProfile.Pure,
                ["runtime.request.authenticate"] = KernelStandardActionProfile.Pure,
                ["runtime.request.authorize"] = KernelStandardActionProfile.Pure,
                ["runtime.request.route"] = KernelStandardActionProfile.Pure,
                ["runtime.request.handler.invoke"] = KernelStandardActionProfile.Effect,
                ["runtime.request.response.prepare"] = KernelStandardActionProfile.Pure,
                ["runtime.request.response.write"] = KernelStandardActionProfile.ReceiptedEffect,
                ["runtime.request.complete"] = KernelStandardActionProfile.IdempotentEffect,
                ["runtime.request.fail"] = KernelStandardActionProfile.Signal,
                ["runtime.request.cancel"] = KernelStandardActionProfile.Signal,
                ["runtime.cli.parse"] = KernelStandardActionProfile.Pure,
                ["runtime.cli.command.select"] = KernelStandardActionProfile.Pure,
                ["runtime.cli.execute"] = KernelStandardActionProfile.Effect,
                ["runtime.cli.output.write"] = KernelStandardActionProfile.ReceiptedEffect,
                ["runtime.cli.complete"] = KernelStandardActionProfile.IdempotentEffect,
                ["runtime.cli.fail"] = KernelStandardActionProfile.Signal,
                ["runtime.cli.cancel"] = KernelStandardActionProfile.Signal,
                ["security.api_key.resolve"] = KernelStandardActionProfile.Pure,
                ["security.session.validate"] = KernelStandardActionProfile.Pure,
                ["security.administrator.authorize"] = KernelStandardActionProfile.Pure,
                ["security.secret.read"] = KernelStandardActionProfile.Pure,
                ["security.secret.write"] = KernelStandardActionProfile.ConflictEffect,
                ["security.secret.delete"] = KernelStandardActionProfile.ConflictEffect,
                ["security.remote_pairing.validate"] = KernelStandardActionProfile.Pure,
                ["security.decision.fail"] = KernelStandardActionProfile.Signal,
                ["security.decision.cancel"] = KernelStandardActionProfile.Signal,
                ["client.command.receive"] = KernelStandardActionProfile.Pure,
                ["client.command.validate"] = KernelStandardActionProfile.Pure,
                ["client.command.dispatch"] = KernelStandardActionProfile.Effect,
                ["client.command.complete"] = KernelStandardActionProfile.IdempotentEffect,
                ["client.command.fail"] = KernelStandardActionProfile.Signal,
                ["client.command.cancel"] = KernelStandardActionProfile.Signal,
                ["client.navigation.prepare"] = KernelStandardActionProfile.Pure,
                ["client.navigation.commit"] = KernelStandardActionProfile.ConflictEffect,
                ["client.state.prepare"] = KernelStandardActionProfile.Pure,
                ["client.state.commit"] = KernelStandardActionProfile.ConflictEffect,
                ["gateway.request.receive"] = KernelStandardActionProfile.Pure,
                ["gateway.request.authenticate"] = KernelStandardActionProfile.Pure,
                ["gateway.request.authorize"] = KernelStandardActionProfile.Pure,
                ["gateway.request.route"] = KernelStandardActionProfile.Pure,
                ["gateway.request.forward"] = KernelStandardActionProfile.ReceiptedEffect,
                ["gateway.request.response"] = KernelStandardActionProfile.Pure,
                ["gateway.request.fail"] = KernelStandardActionProfile.Signal,
                ["gateway.request.cancel"] = KernelStandardActionProfile.Signal,
                ["gateway.stream.open"] = KernelStandardActionProfile.StreamEffect,
                ["gateway.stream.chunk.receive"] = KernelStandardActionProfile.Stream,
                ["gateway.stream.chunk.forward"] = KernelStandardActionProfile.ReceiptedStreamEffect,
                ["gateway.stream.close"] = KernelStandardActionProfile.StreamEffect,
                ["gateway.stream.fail"] = KernelStandardActionProfile.Signal,
                ["gateway.stream.cancel"] = KernelStandardActionProfile.Signal,
                ["gateway.module.endpoint.dispatch"] = KernelStandardActionProfile.Effect,
                ["gateway.bridge.session.validate"] = KernelStandardActionProfile.Pure,
                ["gateway.bridge.forward"] = KernelStandardActionProfile.ReceiptedEffect,
                ["chat.turn.start"] = KernelStandardActionProfile.Effect,
                ["chat.conversation.resolve"] = KernelStandardActionProfile.Pure,
                ["chat.profile.resolve"] = KernelStandardActionProfile.Pure,
                ["chat.history.load"] = KernelStandardActionProfile.Pure,
                ["chat.user_message.prepare"] = KernelStandardActionProfile.Pure,
                ["chat.user_message.commit"] = KernelStandardActionProfile.IdempotentEffect,
                ["chat.context.assemble.start"] = KernelStandardActionProfile.Effect,
                ["chat.context.contributor.invoke"] = KernelStandardActionProfile.Effect,
                ["chat.context.assemble.complete"] = KernelStandardActionProfile.Deferrable,
                ["chat.tools.collect"] = KernelStandardActionProfile.Pure,
                ["chat.tools.select"] = KernelStandardActionProfile.Pure,
                ["chat.provider_round.start"] = KernelStandardActionProfile.Effect,
                ["chat.provider_round.complete"] = KernelStandardActionProfile.Deferrable,
                ["chat.assistant_message.prepare"] = KernelStandardActionProfile.Pure,
                ["chat.assistant_message.commit"] = KernelStandardActionProfile.IdempotentEffect,
                ["chat.turn.complete"] = KernelStandardActionProfile.Deferrable,
                ["chat.turn.fail"] = KernelStandardActionProfile.Signal,
                ["chat.turn.cancel"] = KernelStandardActionProfile.Signal,
                ["provider.resolve"] = KernelStandardActionProfile.Pure,
                ["provider.client.create"] = KernelStandardActionProfile.IdempotentEffect,
                ["provider.request.prepare"] = KernelStandardActionProfile.Pure,
                ["provider.request.serialize"] = KernelStandardActionProfile.Pure,
                ["provider.request.serialize.after"] = KernelStandardActionProfile.Pure,
                ["provider.request.send"] = KernelStandardActionProfile.ReceiptedEffect,
                ["provider.stream.open"] = KernelStandardActionProfile.StreamEffect,
                ["provider.stream.chunk.receive"] = KernelStandardActionProfile.Stream,
                ["provider.stream.chunk.transform"] = KernelStandardActionProfile.Stream,
                ["provider.stream.chunk.send"] = KernelStandardActionProfile.ReceiptedStreamEffect,
                ["provider.stream.close"] = KernelStandardActionProfile.StreamEffect,
                ["provider.response.deserialize"] = KernelStandardActionProfile.Pure,
                ["provider.response.complete"] = KernelStandardActionProfile.Deferrable,
                ["provider.request.fail"] = KernelStandardActionProfile.Signal,
                ["provider.request.cancel"] = KernelStandardActionProfile.Signal,
                ["tool.definition.register"] = KernelStandardActionProfile.IdempotentEffect,
                ["tool.definition.select"] = KernelStandardActionProfile.Pure,
                ["tool.call.parse"] = KernelStandardActionProfile.Pure,
                ["tool.call.propose"] = KernelStandardActionProfile.Deferrable,
                ["tool.call.input.transform"] = KernelStandardActionProfile.Pure,
                ["tool.call.check"] = KernelStandardActionProfile.Pure,
                ["tool.call.defer"] = KernelStandardActionProfile.Deferrable,
                ["tool.call.coordinate"] = KernelStandardActionProfile.Effect,
                ["tool.handler.invoke"] = KernelStandardActionProfile.ReceiptedEffect,
                ["tool.result.transform"] = KernelStandardActionProfile.Pure,
                ["tool.result.return"] = KernelStandardActionProfile.Deferrable,
                ["tool.call.fail"] = KernelStandardActionProfile.Signal,
                ["tool.call.cancel"] = KernelStandardActionProfile.Signal,
                ["conversation.create"] = KernelStandardActionProfile.IdempotentEffect,
                ["conversation.history.query"] = KernelStandardActionProfile.Pure,
                ["conversation.message.prepare"] = KernelStandardActionProfile.Pure,
                ["conversation.message.commit"] = KernelStandardActionProfile.ConflictEffect,
                ["conversation.message.delete"] = KernelStandardActionProfile.ConflictEffect,
                ["conversation.clear.prepare"] = KernelStandardActionProfile.Pure,
                ["conversation.clear.commit"] = KernelStandardActionProfile.ConflictEffect,
                ["module.discover"] = KernelStandardActionProfile.Pure,
                ["module.validate"] = KernelStandardActionProfile.Pure,
                ["module.configure"] = KernelStandardActionProfile.Pure,
                ["module.graph.compile"] = KernelStandardActionProfile.Pure,
                ["module.start"] = KernelStandardActionProfile.Effect,
                ["module.enable.prepare"] = KernelStandardActionProfile.Pure,
                ["module.enable.commit"] = KernelStandardActionProfile.IdempotentEffect,
                ["module.disable.prepare"] = KernelStandardActionProfile.Pure,
                ["module.disable.commit"] = KernelStandardActionProfile.IdempotentEffect,
                ["module.stop"] = KernelStandardActionProfile.Effect,
                ["module.unload"] = KernelStandardActionProfile.Effect,
                ["module.health.check"] = KernelStandardActionProfile.Pure,
                ["module.lifecycle.fail"] = KernelStandardActionProfile.Signal,
                ["module.lifecycle.cancel"] = KernelStandardActionProfile.Signal,
                ["module.lease.drain"] = KernelStandardActionProfile.Effect,
                ["storage.get"] = KernelStandardActionProfile.Pure,
                ["storage.list"] = KernelStandardActionProfile.Pure,
                ["storage.query"] = KernelStandardActionProfile.Pure,
                ["storage.claim"] = KernelStandardActionProfile.ConflictEffect,
                ["storage.upsert.prepare"] = KernelStandardActionProfile.Pure,
                ["storage.upsert.commit"] = KernelStandardActionProfile.ConflictEffect,
                ["storage.delete.prepare"] = KernelStandardActionProfile.Pure,
                ["storage.delete.commit"] = KernelStandardActionProfile.ConflictEffect,
                ["storage.transaction.prepare"] = KernelStandardActionProfile.Pure,
                ["storage.transaction.begin"] = KernelStandardActionProfile.ConflictEffect,
                ["storage.transaction.commit"] = KernelStandardActionProfile.ConflictEffect,
                ["storage.transaction.rollback"] = KernelStandardActionProfile.IdempotentEffect,
                ["storage.operation.fail"] = KernelStandardActionProfile.Signal,
                ["storage.operation.cancel"] = KernelStandardActionProfile.Signal,
                ["event.define"] = KernelStandardActionProfile.Pure,
                ["event.publish.preview"] = KernelStandardActionProfile.Pure,
                ["event.publish.commit"] = KernelStandardActionProfile.IdempotentEffect,
                ["event.enqueue"] = KernelStandardActionProfile.ReceiptedEffect,
                ["event.deliver"] = KernelStandardActionProfile.ReceiptedEffect,
                ["event.acknowledge"] = KernelStandardActionProfile.ConflictEffect,
                ["event.delivery.fail"] = KernelStandardActionProfile.Signal,
                ["continuation.create"] = KernelStandardActionProfile.IdempotentEffect,
                ["continuation.claim"] = KernelStandardActionProfile.ConflictEffect,
                ["continuation.lease.renew"] = KernelStandardActionProfile.ConflictEffect,
                ["continuation.resume"] = KernelStandardActionProfile.ConflictEffect,
                ["continuation.cancel"] = KernelStandardActionProfile.ConflictEffect,
                ["continuation.recover"] = KernelStandardActionProfile.Pure,
                ["continuation.complete"] = KernelStandardActionProfile.ConflictEffect,
                ["continuation.deliver"] = KernelStandardActionProfile.ReceiptedEffect,
                ["continuation.acknowledge"] = KernelStandardActionProfile.ConflictEffect,
                ["continuation.expire"] = KernelStandardActionProfile.ConflictEffect,
                ["continuation.delete"] = KernelStandardActionProfile.ConflictEffect,
                ["action_recovery.create"] = KernelStandardActionProfile.IdempotentEffect,
                ["action_recovery.query"] = KernelStandardActionProfile.Pure,
                ["action_recovery.evaluate"] = KernelStandardActionProfile.Pure,
                ["action_recovery.resolve"] = KernelStandardActionProfile.ConflictEffect,
                ["action_recovery.deliver"] = KernelStandardActionProfile.ReceiptedEffect,
                ["action_recovery.acknowledge"] = KernelStandardActionProfile.ConflictEffect,
                ["action_recovery.delete"] = KernelStandardActionProfile.ConflictEffect,
                ["background.service.start"] = KernelStandardActionProfile.Effect,
                ["background.tick.prepare"] = KernelStandardActionProfile.Pure,
                ["background.tick.execute"] = KernelStandardActionProfile.Effect,
                ["background.tick.complete"] = KernelStandardActionProfile.IdempotentEffect,
                ["background.tick.fail"] = KernelStandardActionProfile.Signal,
                ["background.tick.cancel"] = KernelStandardActionProfile.Signal,
                ["background.service.stop"] = KernelStandardActionProfile.Effect
            };

        foreach (var (family, profile) in JobsFamilyProfiles)
        {
            profiles.Add(family, profile.Root);
            profiles.Add($"{family}.before", profile.Before);
            profiles.Add($"{family}.after", profile.After);
        }

        return new ReadOnlyDictionary<string, KernelStandardActionProfile>(profiles);
    }

    public static IReadOnlyList<KernelStandardActionManifestEntry> Entries { get; } =
        BuildEntries();

    public static KernelStandardActionManifestEntry Get(SharpClawActionKey key) =>
        Entries.FirstOrDefault(entry => entry.Key == key)
        ?? throw new KernelGraphCompilationException(
            $"Canonical action '{key.Value}' has no descriptor manifest entry.");

    private static IReadOnlyList<KernelStandardActionManifestEntry> BuildEntries()
    {
        var canonical = SharpClawActionCatalog.All
            .DistinctBy(key => key.Value, StringComparer.Ordinal)
            .ToArray();
        var canonicalNames = canonical.Select(key => key.Value).ToHashSet(StringComparer.Ordinal);
        var missing = canonicalNames.Except(Profiles.Keys, StringComparer.Ordinal).ToArray();
        var extra = Profiles.Keys.Except(canonicalNames, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || extra.Length > 0)
        {
            throw new KernelGraphCompilationException(
                $"The standard action manifest is incomplete. Missing={string.Join(',', missing)}. " +
                $"Extra={string.Join(',', extra)}.");
        }

        return new ReadOnlyCollection<KernelStandardActionManifestEntry>(
            canonical.Select(key => Create(key, Profiles[key.Value])).ToArray());
    }

    private static KernelStandardActionManifestEntry Create(
        SharpClawActionKey key,
        KernelStandardActionProfile profile)
    {
        var profileData = ProfileData(profile, key);
        var contract = ContractTypes(key);
        return new KernelStandardActionManifestEntry(
            key,
            1,
            KernelActionCatalog.CategoryFor(key),
            contract.Input,
            contract.Result,
            Schema(key, "input", contract.Input, profileData),
            Schema(key, "result", contract.Result, profileData),
            profileData.Capabilities,
            KernelActionCatalog.StandardSensitive(key),
            profileData.HasIrreversibleEffects,
            profileData.RepeatPolicy,
            profileData.ContinuationPolicy,
            profileData.DefaultTimeout,
            profileData.SafePoints,
            profile);
    }

    private static KernelStandardActionProfileData ProfileData(
        KernelStandardActionProfile profile,
        SharpClawActionKey key) => profile switch
        {
            KernelStandardActionProfile.Pure => new(
                KernelCapabilities.ObservableActions |
                ActionInterceptionCapabilities.ReplaceInput |
                ActionInterceptionCapabilities.ReplaceResult |
                ActionInterceptionCapabilities.Cancel |
                ActionInterceptionCapabilities.Repeat,
                false,
                new ActionRepeatPolicy(
                    ActionRepeatKind.Idempotent,
                    3,
                    TimeSpan.FromMilliseconds(10),
                    $"action:{key.Value}"),
                null,
                TimeSpan.FromSeconds(30),
                [
                    ActionSafePoint.BeforeContinuation,
                    ActionSafePoint.BeforeTerminal,
                    ActionSafePoint.AfterTerminal
                ]),
            KernelStandardActionProfile.Deferrable => new(
                KernelCapabilities.ObservableActions |
                ActionInterceptionCapabilities.ReplaceInput |
                ActionInterceptionCapabilities.ReplaceResult |
                ActionInterceptionCapabilities.Cancel |
                ActionInterceptionCapabilities.Defer,
                false,
                KernelCapabilities.NoRepeat,
                KernelCapabilities.DurableContinuation,
                TimeSpan.FromMinutes(1),
                [
                    ActionSafePoint.BeforeContinuation,
                    ActionSafePoint.BeforeTerminal,
                    ActionSafePoint.AfterTerminal
                ]),
            KernelStandardActionProfile.Effect => new(
                KernelCapabilities.ObservableActions |
                ActionInterceptionCapabilities.ReplaceInput |
                ActionInterceptionCapabilities.ReplaceResult |
                ActionInterceptionCapabilities.Cancel |
                ActionInterceptionCapabilities.Defer,
                true,
                KernelCapabilities.NoRepeat,
                KernelCapabilities.DurableContinuation,
                TimeSpan.FromMinutes(2),
                [
                    ActionSafePoint.BeforeContinuation,
                    ActionSafePoint.BeforeTerminal,
                    ActionSafePoint.AfterTerminal,
                    ActionSafePoint.BeforeCommit,
                    ActionSafePoint.AfterCommit
                ]),
            KernelStandardActionProfile.IdempotentEffect => RepeatableEffect(
                key,
                ActionRepeatKind.Idempotent,
                3,
                TimeSpan.FromMilliseconds(50)),
            KernelStandardActionProfile.ConflictEffect => RepeatableEffect(
                key,
                ActionRepeatKind.ConflictOnly,
                3,
                TimeSpan.FromMilliseconds(25)),
            KernelStandardActionProfile.ReceiptedEffect => RepeatableEffect(
                key,
                ActionRepeatKind.Receipted,
                2,
                TimeSpan.FromMilliseconds(100)),
            KernelStandardActionProfile.Stream => new(
                KernelCapabilities.ObservableActions |
                ActionInterceptionCapabilities.ReplaceInput |
                ActionInterceptionCapabilities.ReplaceResult |
                ActionInterceptionCapabilities.Cancel,
                false,
                KernelCapabilities.NoRepeat,
                null,
                TimeSpan.FromSeconds(15),
                [ActionSafePoint.BeforeTerminal, ActionSafePoint.AfterTerminal]),
            KernelStandardActionProfile.StreamEffect => new(
                KernelCapabilities.ObservableActions |
                ActionInterceptionCapabilities.ReplaceInput |
                ActionInterceptionCapabilities.ReplaceResult |
                ActionInterceptionCapabilities.Cancel |
                ActionInterceptionCapabilities.Defer,
                true,
                KernelCapabilities.NoRepeat,
                KernelCapabilities.DurableContinuation,
                TimeSpan.FromSeconds(30),
                [
                    ActionSafePoint.BeforeTerminal,
                    ActionSafePoint.AfterTerminal,
                    ActionSafePoint.BeforeCommit,
                    ActionSafePoint.AfterCommit
                ]),
            KernelStandardActionProfile.ReceiptedStreamEffect => new(
                KernelCapabilities.ObservableActions |
                ActionInterceptionCapabilities.ReplaceInput |
                ActionInterceptionCapabilities.ReplaceResult |
                ActionInterceptionCapabilities.Cancel |
                ActionInterceptionCapabilities.Defer |
                ActionInterceptionCapabilities.Repeat,
                true,
                new ActionRepeatPolicy(
                    ActionRepeatKind.Receipted,
                    2,
                    TimeSpan.FromMilliseconds(25),
                    $"action:{key.Value}"),
                KernelCapabilities.DurableContinuation,
                TimeSpan.FromSeconds(30),
                [
                    ActionSafePoint.BeforeTerminal,
                    ActionSafePoint.AfterTerminal,
                    ActionSafePoint.BeforeCommit,
                    ActionSafePoint.AfterCommit
                ]),
            KernelStandardActionProfile.Signal => new(
                KernelCapabilities.ObservableActions |
                ActionInterceptionCapabilities.ReplaceInput |
                ActionInterceptionCapabilities.ReplaceResult,
                false,
                KernelCapabilities.NoRepeat,
                null,
                TimeSpan.FromSeconds(15),
                [ActionSafePoint.BeforeTerminal, ActionSafePoint.AfterTerminal]),
            KernelStandardActionProfile.Progress => new(
                KernelCapabilities.ObservableActions |
                ActionInterceptionCapabilities.ReplaceInput |
                ActionInterceptionCapabilities.ReplaceResult |
                ActionInterceptionCapabilities.Cancel |
                ActionInterceptionCapabilities.Wrap,
                false,
                KernelCapabilities.NoRepeat,
                null,
                TimeSpan.FromSeconds(15),
                [ActionSafePoint.BeforeTerminal, ActionSafePoint.AfterTerminal]),
            KernelStandardActionProfile.Observe => new(
                ActionInterceptionCapabilities.Inspect |
                ActionInterceptionCapabilities.Observe |
                ActionInterceptionCapabilities.PublishEvents,
                false,
                KernelCapabilities.NoRepeat,
                null,
                TimeSpan.FromSeconds(15),
                [ActionSafePoint.BeforeTerminal, ActionSafePoint.AfterTerminal]),
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };

    private static KernelStandardActionProfileData RepeatableEffect(
        SharpClawActionKey key,
        ActionRepeatKind repeatKind,
        int maximumAttempts,
        TimeSpan minimumBackoff) => new(
            KernelCapabilities.ObservableActions |
            ActionInterceptionCapabilities.ReplaceInput |
            ActionInterceptionCapabilities.ReplaceResult |
            ActionInterceptionCapabilities.Cancel |
            ActionInterceptionCapabilities.Defer |
            ActionInterceptionCapabilities.Repeat,
            true,
            new ActionRepeatPolicy(
                repeatKind,
                maximumAttempts,
                minimumBackoff,
                $"action:{key.Value}"),
            KernelCapabilities.DurableContinuation,
            TimeSpan.FromMinutes(2),
            [
                ActionSafePoint.BeforeContinuation,
                ActionSafePoint.BeforeTerminal,
                ActionSafePoint.AfterTerminal,
                ActionSafePoint.BeforeCommit,
                ActionSafePoint.AfterCommit
            ]);

    private static (Type Input, Type Result) ContractTypes(SharpClawActionKey key)
    {
        if (key.Value.StartsWith("jobs.", StringComparison.Ordinal))
        {
            if (key.Value.EndsWith(".before", StringComparison.Ordinal))
                return (
                    typeof(JobCheckpoint<JobActionInput<JsonElement>>),
                    typeof(JobCheckpoint<JobActionInput<JsonElement>>));
            if (key.Value.EndsWith(".after", StringComparison.Ordinal))
                return (
                    typeof(JobCheckpoint<JobActionResult<JsonElement>>),
                    typeof(JobCheckpoint<JobActionResult<JsonElement>>));
            return (typeof(JobActionInput<JsonElement>), typeof(JobActionResult<JsonElement>));
        }

        return key.Value switch
        {
        "chat.turn.start" => (typeof(ChatTurnInput), typeof(ChatTurnResult)),
        "chat.conversation.resolve" => (typeof(ChatTurnInput), typeof(ConversationSelection)),
        "chat.profile.resolve" => (typeof(ChatTurnContext), typeof(ChatProfile)),
        "chat.history.load" => (typeof(ChatTurnContext), typeof(IReadOnlyList<ChatCompletionMessage>)),
        "chat.user_message.prepare" or "chat.user_message.commit" =>
            (typeof(KernelChatUserMessage), typeof(KernelChatUserMessage)),
        "chat.context.assemble.start" => (typeof(ChatContextRequest), typeof(ChatContextContribution)),
        "chat.context.contributor.invoke" =>
            (typeof(KernelChatContributorInvocation), typeof(ChatContextContribution)),
        "chat.context.assemble.complete" =>
            (typeof(ChatContextContribution), typeof(ChatContextContribution)),
        "chat.tools.collect" or "chat.tools.select" =>
            (typeof(IReadOnlyList<ToolDescriptor>), typeof(IReadOnlyList<ToolDescriptor>)),
        "chat.provider_round.start" => (typeof(ProviderTurnRequest), typeof(ChatCompletionResult)),
        "chat.provider_round.complete" => (typeof(ChatCompletionResult), typeof(ChatCompletionResult)),
        "chat.assistant_message.prepare" or "chat.assistant_message.commit" or
            "conversation.message.prepare" or "conversation.message.commit" =>
            (typeof(ChatExchange), typeof(ChatExchange)),
        "chat.turn.complete" => (typeof(ChatTurnResult), typeof(ChatTurnResult)),
        "chat.turn.fail" => (typeof(KernelChatFailure), typeof(bool)),
        "chat.turn.cancel" => (typeof(ChatTurnInput), typeof(bool)),
        "conversation.history.query" => (typeof(Guid), typeof(IReadOnlyList<ChatCompletionMessage>)),
        "provider.resolve" or "provider.client.create" or "provider.request.prepare" or
            "provider.request.serialize" or "provider.request.serialize.after" or
            "provider.stream.open" =>
            (typeof(KernelProviderRequestEnvelope), typeof(KernelProviderRequestEnvelope)),
        "provider.request.send" => (typeof(KernelProviderRequestEnvelope), typeof(KernelProviderTransportResult)),
        "provider.stream.close" => (typeof(KernelProviderRequestEnvelope), typeof(bool)),
        "provider.stream.chunk.receive" or "provider.stream.chunk.transform" or
            "provider.stream.chunk.send" => (typeof(ChatStreamChunk), typeof(KernelProviderChunkResult)),
        "provider.response.deserialize" =>
            (typeof(KernelProviderCompletionEnvelope), typeof(ChatCompletionResult)),
        "provider.response.complete" => (typeof(ChatCompletionResult), typeof(ChatCompletionResult)),
        "provider.request.fail" or "provider.request.cancel" =>
            (typeof(KernelProviderFailure), typeof(bool)),
        "tool.definition.register" => (typeof(ToolDescriptor), typeof(ToolDescriptor)),
        "tool.call.propose" => (typeof(ToolInvocation), typeof(ToolInvocationOutcome)),
        "tool.call.parse" or "tool.call.input.transform" or "tool.call.defer" =>
            (typeof(ToolInvocation), typeof(ToolInvocation)),
        "tool.definition.select" => (typeof(ToolInvocation), typeof(KernelToolResolution)),
        "tool.call.check" => (typeof(ToolInvocation), typeof(KernelToolCheckResult)),
        "tool.call.coordinate" => (typeof(ToolInvocation), typeof(ToolInvocationOutcome)),
        "tool.handler.invoke" => (typeof(ToolInvocation), typeof(ToolResult)),
        "tool.result.transform" or "tool.result.return" =>
            (typeof(KernelToolResultStage), typeof(ToolResult)),
        "tool.call.fail" or "tool.call.cancel" => (typeof(ToolInvocationOutcome), typeof(bool)),
        "module.start" => (typeof(ModuleStartContext), typeof(bool)),
        "module.stop" => (typeof(ModuleIdentity), typeof(bool)),
            _ => (typeof(JsonElement), typeof(JsonElement))
        };
    }

    private static JsonSchemaReference Schema(
        SharpClawActionKey key,
        string role,
        Type payloadType,
        KernelStandardActionProfileData profile)
    {
        var contractName = $"sharpclaw.kernel.action.{key.Value}.{role}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{contractName}|{KernelGraphHasher.StableScalar(1)}|{payloadType.AssemblyQualifiedName}|" +
            $"{KernelGraphHasher.StableScalar((int)profile.Capabilities)}|" +
            $"{KernelGraphHasher.StableScalar(profile.HasIrreversibleEffects)}|" +
            $"{KernelGraphHasher.StableScalar(profile.RepeatPolicy.Kind)}|" +
            $"{KernelGraphHasher.StableScalar(profile.RepeatPolicy.MaximumAttempts)}|" +
            $"{KernelGraphHasher.StableScalar(profile.RepeatPolicy.MinimumBackoff)}|" +
            $"{profile.RepeatPolicy.IdempotencyScope}|" +
            $"{KernelGraphHasher.StableScalar(profile.ContinuationPolicy?.MaximumLifetime)}|" +
            $"{KernelGraphHasher.StableScalar(profile.ContinuationPolicy?.Durable)}|" +
            $"{KernelGraphHasher.StableScalar(profile.ContinuationPolicy?.SingleClaim)}|" +
            $"{KernelGraphHasher.StableScalar(profile.DefaultTimeout)}|" +
            string.Join(',', profile.SafePoints.Select(value => KernelGraphHasher.StableScalar(value))))));
        return new JsonSchemaReference(contractName, 1, hash);
    }

    private sealed record KernelStandardActionProfileData(
        ActionInterceptionCapabilities Capabilities,
        bool HasIrreversibleEffects,
        ActionRepeatPolicy RepeatPolicy,
        ActionContinuationPolicy? ContinuationPolicy,
        TimeSpan DefaultTimeout,
        IReadOnlyList<ActionSafePoint> SafePoints);
}

public sealed record KernelProviderTransportResult(
    ChatCompletionResult? Completion,
    bool IsStreaming,
    [property: JsonIgnore] IAsyncEnumerable<ChatStreamChunk>? Stream = null)
{
    public static KernelProviderTransportResult Buffered(ChatCompletionResult completion) =>
        new(completion, false);

    public static KernelProviderTransportResult Streaming(IAsyncEnumerable<ChatStreamChunk> stream) =>
        new(null, true, stream);
}

public sealed record KernelProviderChunkResult(
    IReadOnlyList<ChatStreamChunk> Chunks,
    bool Suppressed);
