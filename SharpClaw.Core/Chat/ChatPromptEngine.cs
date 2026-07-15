using SharpClaw.Core.State;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Store-neutral chat prompt and completion-parameter shaping rules.
/// Hosts own provider execution; Core owns the request-shaping semantics.
/// </summary>
public sealed class ChatPromptEngine
{
    private const string NativeToolSystemSuffixResourceName =
        "SharpClaw.Core.tool-instructions-native-suffix.md";

    private static readonly Lazy<string> NativeToolSystemSuffixValue =
        new(() => LoadEmbeddedResource(NativeToolSystemSuffixResourceName));

    /// <summary>
    /// The system-prompt suffix that teaches providers SharpClaw tool-call
    /// notation and result handling.
    /// </summary>
    public string NativeToolSystemSuffix => NativeToolSystemSuffixValue.Value;

    /// <summary>
    /// Builds the effective system prompt for a chat request.
    /// </summary>
    public string BuildEffectiveSystemPrompt(
        string? agentPrompt,
        bool includeCorePrompt,
        bool disableDefaultSystemPrompt)
    {
        if (!includeCorePrompt || disableDefaultSystemPrompt)
            return agentPrompt ?? "";

        return BuildSystemPrompt(agentPrompt);
    }

    /// <summary>
    /// Appends the native-tool suffix to an agent prompt.
    /// </summary>
    public string BuildSystemPrompt(string? agentPrompt)
    {
        if (string.IsNullOrEmpty(agentPrompt))
            return NativeToolSystemSuffix;

        return agentPrompt + "\n\n" + NativeToolSystemSuffix;
    }

    /// <summary>
    /// Maps an agent's provider-tuning fields into completion parameters.
    /// </summary>
    public CompletionParameters BuildCompletionParameters(
        AgentState agent,
        Guid modelId,
        Guid? threadId)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return new CompletionParameters
        {
            Temperature = agent.Temperature,
            TopP = agent.TopP,
            TopK = agent.TopK,
            FrequencyPenalty = agent.FrequencyPenalty,
            PresencePenalty = agent.PresencePenalty,
            Stop = agent.Stop,
            Seed = agent.Seed,
            ResponseFormat = agent.ResponseFormat,
            ReasoningEffort = agent.ReasoningEffort,
            ModelId = modelId,
            ThreadId = threadId,
        };
    }

    private static string LoadEmbeddedResource(string name)
    {
        using var stream = typeof(ChatPromptEngine).Assembly
            .GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{name}' not found in {typeof(ChatPromptEngine).Assembly.FullName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
