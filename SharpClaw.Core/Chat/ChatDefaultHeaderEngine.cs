using SharpClaw.Core.State;
using System.Globalization;
using System.Text;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Clients;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Builds SharpClaw's default chat metadata headers from host-loaded facts.
/// </summary>
public sealed class ChatDefaultHeaderEngine(ProviderApiClientFactory clientFactory)
{
    /// <summary>
    /// Returns whether channel or context configuration suppresses all headers.
    /// </summary>
    public bool IsHeaderDisabled(ChannelState channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.DisableChatHeader
            || (channel.AgentContext?.DisableChatHeader ?? false);
    }

    /// <summary>
    /// Resolves the custom header template using channel-over-agent precedence.
    /// </summary>
    public string? ResolveCustomTemplate(ChannelState channel, AgentState agent)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(agent);
        return channel.CustomChatHeader ?? agent.CustomChatHeader;
    }

    /// <summary>
    /// Returns whether a generated default header should be built.
    /// </summary>
    public bool ShouldBuildDefaultHeader(bool disableDefaultHeaders) =>
        !disableDefaultHeaders;

    /// <summary>Builds the default header for automated task messages.</summary>
    public string BuildTaskHeader(
        ChatTaskHeaderFacts facts,
        string? agentSuffix,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var sb = Begin(now);
        sb.Append(" | source: automated task");
        sb.Append(" | task: ").Append(facts.TaskName);

        if (facts.SharedData is not null)
            sb.Append(" | shared-data: ").Append(facts.SharedData);

        if (facts.BigDataReferences.Count > 0)
        {
            sb.Append(" | big-data-ids: [");
            sb.Append(string.Join(
                ", ",
                facts.BigDataReferences.Select(e => $"{e.Id}:\"{e.Title}\"")));
            sb.Append(']');
        }

        AppendSuffix(sb, agentSuffix);
        return sb.ToString();
    }

    /// <summary>Builds the default header for externally forwarded users.</summary>
    public string BuildExternalUserHeader(
        ChatExternalUserHeaderFacts facts,
        string? agentSuffix,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var sb = Begin(now);
        sb.Append(" | user: ").Append(facts.DisplayName ?? facts.Username);
        if (facts.DisplayName is not null && facts.Username != facts.DisplayName)
            sb.Append(" (@").Append(facts.Username).Append(')');
        sb.Append(" | via: ").Append(facts.ClientType);

        AppendSuffix(sb, agentSuffix);
        return sb.ToString();
    }

    /// <summary>Builds the default header for authenticated users.</summary>
    public string BuildAuthenticatedUserHeader(
        ChatAuthenticatedUserHeaderFacts facts,
        string? agentSuffix,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var sb = Begin(now);
        sb.Append(" | user: ").Append(facts.Username);
        sb.Append(" | via: ").Append(facts.ClientType);

        if (facts.RoleName is not null)
        {
            if (facts.Grants.Count > 0)
            {
                sb.Append(" | role: ").Append(facts.RoleName)
                    .Append(" (").Append(string.Join(", ", facts.Grants)).Append(')');
            }
            else
            {
                sb.Append(" | role: ").Append(facts.RoleName);
            }
        }

        if (!string.IsNullOrWhiteSpace(facts.Bio))
            sb.Append(" | bio: ").Append(facts.Bio);

        AppendSuffix(sb, agentSuffix);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the shared agent role, policy, provider notice, and terminator.
    /// </summary>
    public string BuildAgentSuffix(
        ChatAgentHeaderSuffixFacts facts,
        CompletionParameters? completionParameters,
        string providerKey)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var sb = new StringBuilder();
        if (facts.RoleName is not null)
        {
            sb.Append(" | agent-role: ").Append(facts.RoleName);
            if (facts.Grants.Count > 0)
                sb.Append(" (").Append(string.Join(", ", facts.Grants)).Append(')');
        }
        else
        {
            sb.Append(" | agent-role: (none)");
        }

        sb.Append(" | policy: unlisted-resource/GUID=denied; disclose gaps to user");

        if (completionParameters?.ReasoningEffort is { } effort)
        {
            var spec = clientFactory.GetParameterSpec(providerKey);
            if (spec.ReasoningEffortInformationalOnly)
            {
                var notice = ChatHeaderNotices.FormatReasoningEffortNotice(effort);
                if (notice.Length > 0)
                    sb.Append(" | ").Append(notice);
            }
        }

        sb.AppendLine("]");
        return sb.ToString();
    }

    private static StringBuilder Begin(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        return new StringBuilder()
            .Append("[time: ")
            .Append(utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
    }

    private static void AppendSuffix(StringBuilder sb, string? agentSuffix)
    {
        sb.Append(agentSuffix ?? "]");
    }
}

/// <summary>Facts used to build a task-sourced chat header.</summary>
public sealed record ChatTaskHeaderFacts(
    string TaskName,
    string? SharedData,
    IReadOnlyList<ChatTaskBigDataReference> BigDataReferences);

/// <summary>Task big-data metadata exposed in the default chat header.</summary>
public sealed record ChatTaskBigDataReference(string Id, string Title);

/// <summary>Facts used to build an external forwarded-user chat header.</summary>
public sealed record ChatExternalUserHeaderFacts(
    string Username,
    string? DisplayName,
    string ClientType);

/// <summary>Facts used to build an authenticated-user chat header.</summary>
public sealed record ChatAuthenticatedUserHeaderFacts(
    string Username,
    string ClientType,
    string? RoleName,
    IReadOnlyList<string> Grants,
    string? Bio);

/// <summary>Facts used to build the shared agent header suffix.</summary>
public sealed record ChatAgentHeaderSuffixFacts(
    string? RoleName,
    IReadOnlyList<string> Grants);
