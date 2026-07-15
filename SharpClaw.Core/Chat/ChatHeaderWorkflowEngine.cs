using SharpClaw.Core.State;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Tasks.Runtime;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Store-neutral workflow for SharpClaw's model-facing chat header assembly.
/// </summary>
public sealed class ChatHeaderWorkflowEngine(
    ChatDefaultHeaderEngine headers,
    ChatCache cache)
{
    private readonly ChatDefaultHeaderEngine _headers = headers
        ?? throw new ArgumentNullException(nameof(headers));
    private readonly ChatCache _cache = cache
        ?? throw new ArgumentNullException(nameof(cache));

    /// <summary>
    /// Builds the header text prepended to a user message, or <c>null</c>
    /// when the current channel/request should not expose a header.
    /// </summary>
    public async Task<string?> BuildHeaderAsync(
        ChatHeaderWorkflowRequest request,
        IChatHeaderWorkflowHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        if (_headers.IsHeaderDisabled(request.Channel))
            return null;

        var sessionUserId = host.GetSessionUserId();
        var customTemplate = _headers.ResolveCustomTemplate(
            request.Channel,
            request.Agent);
        if (customTemplate is not null)
        {
            return await host.ExpandCustomHeaderAsync(
                customTemplate,
                request.Channel,
                request.Agent,
                request.ClientType,
                sessionUserId,
                request.CompletionParameters,
                request.ProviderKey,
                ct);
        }

        if (!_headers.ShouldBuildDefaultHeader(request.DisableDefaultHeaders))
            return null;

        if (request.TaskContext is { } taskContext)
        {
            var suffix = await LoadAgentSuffixAsync(request, host, ct);
            return _headers.BuildTaskHeader(
                BuildTaskHeaderFacts(taskContext),
                suffix,
                request.Now);
        }

        if (sessionUserId is null && request.ExternalUsername is not null)
        {
            var suffix = await LoadAgentSuffixAsync(request, host, ct);
            return _headers.BuildExternalUserHeader(
                new ChatExternalUserHeaderFacts(
                    request.ExternalUsername,
                    request.ExternalDisplayName,
                    request.ClientType),
                suffix,
                request.Now);
        }

        if (sessionUserId is null)
            return null;

        var userState = await _cache.GetOrCreateAsync(
            ChatCache.KeyHeaderUser(sessionUserId.Value),
            innerCt => host.LoadUserHeaderStateAsync(
                sessionUserId.Value,
                innerCt),
            EstimateUserHeaderState,
            ct);

        if (userState is null)
            return null;

        var userSuffix = await LoadAgentSuffixAsync(request, host, ct);
        return _headers.BuildAuthenticatedUserHeader(
            new ChatAuthenticatedUserHeaderFacts(
                userState.Username,
                request.ClientType,
                userState.RoleName,
                userState.Grants,
                userState.Bio),
            userSuffix,
            request.Now);
    }

    private async Task<string?> LoadAgentSuffixAsync(
        ChatHeaderWorkflowRequest request,
        IChatHeaderWorkflowHost host,
        CancellationToken ct)
        => await _cache.GetOrCreateAsync(
            ChatCache.KeyHeaderAgentSuffix(
                request.Agent.Id,
                request.Channel.Id,
                request.ProviderKey,
                request.CompletionParameters?.ReasoningEffort),
            async innerCt =>
            {
                var facts = await host.LoadAgentHeaderSuffixFactsAsync(
                    request.Agent.Id,
                    request.Channel.Id,
                    innerCt);
                return _headers.BuildAgentSuffix(
                    facts,
                    request.CompletionParameters,
                    request.ProviderKey);
            },
            ChatCache.EstimateString,
            ct);

    private static ChatTaskHeaderFacts BuildTaskHeaderFacts(
        TaskChatContext taskContext)
    {
        var store = TaskSharedData.Get(taskContext.InstanceId);
        string? lightText = null;
        IReadOnlyList<ChatTaskBigDataReference> bigEntries = [];
        if (store is not null)
        {
            lightText = store.LightData;
            bigEntries = store.ListBig()
                .Select(static entry => new ChatTaskBigDataReference(
                    entry.Id,
                    entry.Title))
                .ToArray();
        }

        return new ChatTaskHeaderFacts(
            taskContext.TaskName,
            lightText,
            bigEntries);
    }

    private static long EstimateUserHeaderState(ChatHeaderUserState state)
        => 128
           + ChatCache.EstimateString(state.Username)
           + ChatCache.EstimateString(state.RoleName)
           + ChatCache.EstimateString(state.Bio)
           + ChatCache.EstimateStringCollection(state.Grants);
}

public interface IChatHeaderWorkflowHost
{
    Guid? GetSessionUserId();

    Task<string> ExpandCustomHeaderAsync(
        string template,
        ChannelState channel,
        AgentState agent,
        string clientType,
        Guid? sessionUserId,
        CompletionParameters? completionParameters,
        string providerKey,
        CancellationToken ct);

    Task<ChatHeaderUserState?> LoadUserHeaderStateAsync(
        Guid userId,
        CancellationToken ct);

    Task<ChatAgentHeaderSuffixFacts> LoadAgentHeaderSuffixFactsAsync(
        Guid agentId,
        Guid channelId,
        CancellationToken ct);
}

public sealed record ChatHeaderWorkflowRequest(
    ChannelState Channel,
    AgentState Agent,
    string ClientType,
    bool DisableDefaultHeaders,
    TaskChatContext? TaskContext,
    string? ExternalUsername,
    string? ExternalDisplayName,
    CompletionParameters? CompletionParameters,
    string ProviderKey,
    DateTimeOffset Now);

public sealed record ChatHeaderUserState(
    string Username,
    string? RoleName,
    IReadOnlyList<string> Grants,
    string? Bio);
