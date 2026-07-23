namespace SharpClaw.Core.Modules.Sidecar;

public sealed class SidecarReadinessEvaluator
{
    public ModuleSidecarReadinessReport Evaluate(ModuleSidecarReadinessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var findings = BuildFindings(facts.Contributions, facts.Services);

        return new ModuleSidecarReadinessReport(
            facts.ModuleId,
            facts.DisplayName,
            facts.ToolPrefix,
            facts.ModuleType,
            facts.AssemblyName,
            facts.Contributions,
            facts.Services,
            findings);
    }

    public IReadOnlyList<ModuleSidecarReadinessReport> EvaluateAll(
        IEnumerable<ModuleSidecarReadinessFacts> facts) =>
        [.. facts.Select(Evaluate).OrderBy(report => report.ModuleId, StringComparer.Ordinal)];

    private static IReadOnlyList<SidecarReadinessFinding> BuildFindings(
        ModuleContributionInventory contributions,
        ModuleServiceInventory services)
    {
        var findings = new List<SidecarReadinessFinding>();

        AddCovered(findings, contributions.ToolCount, "tools.job", "Job-pipeline tools are covered by the current foreign protocol.");
        AddCovered(findings, contributions.InlineToolCount, "tools.inline", "Inline tools are covered by the current foreign protocol.");
        AddCovered(findings, contributions.MapsEndpoints ? 1 : 0, "endpoints.http", "HTTP endpoint proxying is covered by the current foreign protocol.");
        AddCovered(findings, contributions.OverridesHealthCheck ? 1 : 0, "health", "Health checks are covered by the current foreign protocol.");
        AddCovered(findings, contributions.OverridesInitialize ? 1 : 0, "lifecycle.initialize", "Initialize is covered by the current foreign protocol.");
        AddCovered(findings, contributions.OverridesShutdown ? 1 : 0, "lifecycle.shutdown", "Shutdown is covered by the current foreign protocol.");
        AddCovered(findings, contributions.ExportedProtocolContractCount, "contracts.protocol.exports", "Protocol contract exports are covered by the current foreign protocol.");
        AddCovered(findings, contributions.RequiredProtocolContractCount, "contracts.protocol.requirements", "Protocol contract requirements are covered by the current foreign protocol.");
        AddCovered(findings, contributions.ResourceTypeDescriptorCount, "module.resource_descriptors", "Resource descriptors, id lookup, and lookup items are covered by the current foreign protocol.");
        AddCovered(findings, contributions.GlobalFlagDescriptorCount, "module.global_flags", "Global flag descriptors are covered by the current foreign protocol.");
        AddCovered(findings, contributions.HeaderTagCount, "module.header_tags", "Header tag discovery and invocation are covered by the current foreign protocol.");
        AddCovered(findings, contributions.UiContributionCount, "module.ui_contributions", "UI contribution descriptors are covered by the current foreign protocol.");
        AddCovered(findings, contributions.FrontendContributionCount, "module.frontend_contributions", "Frontend contribution descriptors are covered by the current foreign protocol.");
        AddCovered(findings, contributions.CliCommandCount, "module.cli_commands", "CLI command discovery and invocation are covered by the current foreign protocol.");

        if (contributions.ExportedClrContractCount > 0)
            findings.Add(new(
                SidecarReadinessFindingKind.RequiresClrContractBridge,
                "contracts.clr.exports",
                $"{contributions.ExportedClrContractCount} CLR contract export(s) need protocol contract equivalents."));

        if (contributions.RequiredNonOptionalClrContractCount > 0)
            findings.Add(new(
                SidecarReadinessFindingKind.RequiresClrContractBridge,
                "contracts.clr.requirements",
                $"{contributions.RequiredNonOptionalClrContractCount} non-optional CLR contract requirement(s) need protocol contract equivalents."));

        AddCovered(
            findings,
            contributions.RequiredOptionalClrContractCount,
            "contracts.clr.optional_requirements",
            "Optional CLR contract requirements do not block sidecar loading.");

        AddCovered(
            findings,
            services.EventSinkRegistrations.Count,
            "events.sinks",
            "Host event sinks are covered by the current foreign protocol: "
            + string.Join(", ", services.EventSinkRegistrations));

        AddCovered(
            findings,
            services.ProviderPluginRegistrations.Count,
            "providers.plugins",
            "Provider plugin discovery and invocation are covered by the current foreign protocol.");

        if (services.ModuleStorageRegistrationTypes.Count > 0)
            findings.Add(new(
                SidecarReadinessFindingKind.RequiresStoragePlan,
                "storage.module_registrations",
                "Module-owned storage registrations need a host-backed storage capability or explicit data migration: "
                + string.Join(", ", services.ModuleStorageRegistrationTypes)));

        AddCovered(findings, contributions.OverridesJobCompletionBehavior ? 1 : 0, "jobs.completion_behavior", "Dynamic job completion behavior is covered by the current foreign protocol.");

        if (contributions.OverridesSeedData)
            findings.Add(new(
                SidecarReadinessFindingKind.RequiresManualReview,
                "lifecycle.seed_data",
                "SeedDataAsync needs a sidecar execution point with data-safety review."));

        if (services.RegistrationCollectionError is not null)
            findings.Add(new(
                SidecarReadinessFindingKind.RequiresManualReview,
                "registrations.collection",
                $"Module registration fact collection failed: {services.RegistrationCollectionError}"));

        return [.. findings.OrderBy(finding => finding.Kind).ThenBy(finding => finding.Key, StringComparer.Ordinal)];
    }

    private static void AddCovered(
        List<SidecarReadinessFinding> findings,
        int count,
        string key,
        string detail)
    {
        if (count <= 0)
            return;

        findings.Add(new(SidecarReadinessFindingKind.CoveredByCurrentProtocol, key, detail));
    }
}
