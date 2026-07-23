using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Modules;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Store-neutral workflow for chat-visible module tool surfaces.
/// </summary>
public sealed class ChatToolWorkflowEngine(
    ModuleRegistry moduleRegistry,
    ChatCache cache,
    ChatToolSelectionEngine toolSelection)
{
    private readonly ModuleRegistry _moduleRegistry = moduleRegistry
        ?? throw new ArgumentNullException(nameof(moduleRegistry));
    private readonly ChatCache _cache = cache
        ?? throw new ArgumentNullException(nameof(cache));
    private readonly ChatToolSelectionEngine _toolSelection = toolSelection
        ?? throw new ArgumentNullException(nameof(toolSelection));

    /// <summary>
    /// Returns the provider-facing tool definitions for a chat request.
    /// Module tools form the provider-facing surface, with tool-awareness
    /// applied before the definitions are returned.
    /// </summary>
    public async Task<IReadOnlyList<ChatToolDefinition>> GetEffectiveToolsAsync(
        ChatEffectiveToolRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AgentId.HasValue)
        {
            return await _cache.GetOrCreateAsync(
                ChatCache.KeyEffectiveTools(
                    request.AgentId.Value,
                    _toolSelection.BuildAwarenessFingerprint(
                        request.ToolAwareness)),
                _ => Task.FromResult<IReadOnlyList<ChatToolDefinition>?>(
                    BuildEffectiveTools(request.ToolAwareness)),
                _toolSelection.EstimateToolDefinitions,
                ct)
                ?? [];
        }

        return BuildEffectiveTools(request.ToolAwareness);
    }

    private IReadOnlyList<ChatToolDefinition> BuildEffectiveTools(
        IReadOnlyDictionary<string, bool>? toolAwareness)
    {
        var baseTools = new List<ChatToolDefinition>(
            _moduleRegistry.GetAllToolDefinitions());

        return _toolSelection.ApplyAwareness(
            baseTools,
            toolAwareness);
    }
}

public sealed record ChatEffectiveToolRequest(
    IReadOnlyDictionary<string, bool>? ToolAwareness,
    Guid? AgentId);
