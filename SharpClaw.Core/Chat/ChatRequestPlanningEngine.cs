using SharpClaw.Core.State;
using System.Text.Json;
using SharpClaw.Contracts.Models;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Clients;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Plans the provider-facing shape of a chat request from host-loaded facts.
/// </summary>
public sealed class ChatRequestPlanningEngine(
    ChatPromptEngine prompts)
{
    /// <summary>
    /// Builds the provider-call plan for a non-streaming chat request.
    /// </summary>
    public ChatRequestPlan BuildBufferedPlan(
        ChannelState channel,
        AgentState agent,
        Guid? threadId,
        bool disableDefaultSystemPrompt,
        bool disableCustomProviderParameters,
        ChatProviderPlanningFacts? providerFacts)
    {
        var facts = ResolveFacts(agent);
        ArgumentNullException.ThrowIfNull(providerFacts);

        EnsureProviderAccessSatisfied(providerFacts.ProviderAccessSatisfied);

        var disableTools = channel.DisableToolSchemas || agent.DisableToolSchemas;
        var useNativeTools = providerFacts.SupportsNativeToolCalling;
        var enableTools = !disableTools && useNativeTools;
        var completionParameters = BuildAndValidateCompletionParameters(
            agent,
            facts.Model,
            facts.Provider,
            threadId,
            providerFacts.CompletionParameterSpec);

        return new ChatRequestPlan(
            UseNativeTools: useNativeTools,
            DisableTools: disableTools,
            EnableTools: enableTools,
            SupportsVision: facts.Model.CapabilityTags.Contains(
                WellKnownCapabilityKeys.Vision),
            SystemPrompt: prompts.BuildEffectiveSystemPrompt(
                agent.SystemPrompt,
                enableTools,
                disableDefaultSystemPrompt),
            CompletionParameters: completionParameters,
            MaxCompletionTokens: agent.MaxCompletionTokens,
            ProviderParameters: disableCustomProviderParameters
                ? null
                : agent.ProviderParameters,
            ToolAwareness: enableTools
                ? channel.ToolAwarenessSet?.Tools ?? agent.ToolAwarenessSet?.Tools
                : null,
            ModelCapabilityTags: facts.Model.CapabilityTags,
            ModelId: facts.Model.Id,
            ModelName: facts.Model.Name,
            ProviderKey: facts.Provider.ProviderKey,
            ProviderName: facts.Provider.Name,
            ProviderEndpoint: facts.Provider.ApiEndpoint);
    }

    /// <summary>
    /// Builds the provider-call plan for a streaming chat request.
    /// </summary>
    public ChatRequestPlan BuildStreamingPlan(
        ChannelState channel,
        AgentState agent,
        Guid? threadId,
        bool disableDefaultSystemPrompt,
        bool disableCustomProviderParameters,
        ChatProviderPlanningFacts? providerFacts)
    {
        var facts = ResolveFacts(agent);
        ArgumentNullException.ThrowIfNull(providerFacts);

        EnsureProviderAccessSatisfied(providerFacts.ProviderAccessSatisfied);

        var disableTools = channel.DisableToolSchemas || agent.DisableToolSchemas;
        var enableTools = !disableTools;
        var completionParameters = BuildAndValidateCompletionParameters(
            agent,
            facts.Model,
            facts.Provider,
            threadId,
            providerFacts.CompletionParameterSpec);

        return new ChatRequestPlan(
            UseNativeTools: providerFacts.SupportsNativeToolCalling,
            DisableTools: disableTools,
            EnableTools: enableTools,
            SupportsVision: facts.Model.CapabilityTags.Contains(
                WellKnownCapabilityKeys.Vision),
            SystemPrompt: prompts.BuildEffectiveSystemPrompt(
                agent.SystemPrompt,
                enableTools,
                disableDefaultSystemPrompt),
            CompletionParameters: completionParameters,
            MaxCompletionTokens: agent.MaxCompletionTokens,
            ProviderParameters: disableCustomProviderParameters
                ? null
                : agent.ProviderParameters,
            ToolAwareness: enableTools
                ? channel.ToolAwarenessSet?.Tools ?? agent.ToolAwarenessSet?.Tools
                : null,
            ModelCapabilityTags: facts.Model.CapabilityTags,
            ModelId: facts.Model.Id,
            ModelName: facts.Model.Name,
            ProviderKey: facts.Provider.ProviderKey,
            ProviderName: facts.Provider.Name,
            ProviderEndpoint: facts.Provider.ApiEndpoint);
    }

    private CompletionParameters BuildAndValidateCompletionParameters(
        AgentState agent,
        ModelState model,
        ProviderState provider,
        Guid? threadId,
        ICompletionParameterSpec completionParameterSpec)
    {
        ArgumentNullException.ThrowIfNull(completionParameterSpec);

        var completionParameters = prompts.BuildCompletionParameters(
            agent,
            model.Id,
            threadId);
        CompletionParameterValidator.ValidateOrThrow(
            completionParameters,
            completionParameterSpec,
            provider.ProviderKey);
        return completionParameters;
    }

    private static void EnsureProviderAccessSatisfied(
        bool providerAccessSatisfied)
    {
        if (!providerAccessSatisfied)
            throw new InvalidOperationException(
                "Provider does not have an API key configured.");
    }

    private static ChatRequestFacts ResolveFacts(AgentState agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var model = agent.Model
            ?? throw new InvalidOperationException(
                $"Agent '{agent.Name}' ({agent.Id}) has no model assigned. " +
                "Assign a valid model before using this agent for chat.");
        var provider = model.Provider
            ?? throw new InvalidOperationException(
                $"Model '{model.Name}' ({model.Id}) has no provider assigned.");

        return new ChatRequestFacts(model, provider);
    }

    private sealed record ChatRequestFacts(ModelState Model, ProviderState Provider);
}

/// <summary>
/// Provider-facing request plan produced from store-loaded chat facts.
/// </summary>
public sealed record ChatRequestPlan(
    bool UseNativeTools,
    bool DisableTools,
    bool EnableTools,
    bool SupportsVision,
    string SystemPrompt,
    CompletionParameters CompletionParameters,
    int? MaxCompletionTokens,
    Dictionary<string, JsonElement>? ProviderParameters,
    Dictionary<string, bool>? ToolAwareness,
    IReadOnlySet<string> ModelCapabilityTags,
    Guid ModelId,
    string ModelName,
    string ProviderKey,
    string ProviderName,
    string? ProviderEndpoint);

public sealed record ChatProviderPlanningFacts(
    bool ProviderAccessSatisfied,
    bool SupportsNativeToolCalling,
    ICompletionParameterSpec CompletionParameterSpec);
