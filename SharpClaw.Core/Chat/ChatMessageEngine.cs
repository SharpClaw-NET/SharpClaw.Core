using SharpClaw.Core.State;
using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Store-neutral chat message construction and projection rules used by
/// SharpClaw runtimes.
/// </summary>
public sealed class ChatMessageEngine
{
    /// <summary>Creates the user message persisted before provider inference.</summary>
    public ChatMessageState CreateUserMessage(
        Guid channelId,
        Guid? threadId,
        ChatRequest request,
        Guid? senderUserId,
        string? senderUsername,
        Guid? permissionRoleId,
        string? permissionRoleName)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ChatMessageState
        {
            Role = ChatRoles.User,
            Origin = MessageOrigin.User,
            Content = request.Message,
            ChannelId = channelId,
            ThreadId = threadId,
            SenderUserId = senderUserId,
            SenderUsername = senderUsername,
            PermissionRoleId = permissionRoleId,
            PermissionRoleName = permissionRoleName,
            ClientType = request.ClientType
        };
    }

    /// <summary>Creates an assistant message from a completed provider result.</summary>
    public ChatMessageState CreateAssistantMessage(
        Guid channelId,
        Guid? threadId,
        ChatRequest request,
        AgentState agent,
        string content,
        int? promptTokens,
        int? completionTokens,
        string? providerMetadataJson)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(agent);

        return new ChatMessageState
        {
            Role = ChatRoles.Assistant,
            Origin = MessageOrigin.Assistant,
            Content = content,
            ChannelId = channelId,
            ThreadId = threadId,
            SenderAgentId = agent.Id,
            SenderAgentName = agent.Name,
            PermissionRoleId = agent.RoleId,
            PermissionRoleName = agent.Role?.Name,
            ClientType = request.ClientType,
            PromptTokens = PositiveOrNull(promptTokens),
            CompletionTokens = PositiveOrNull(completionTokens),
            ProviderMetadataJson = providerMetadataJson
        };
    }

    /// <summary>Creates the visible system error message for failed chat sends.</summary>
    public ChatMessageState CreateSystemErrorMessage(
        Guid channelId,
        Guid? threadId,
        ChatRequest request,
        string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ChatMessageState
        {
            Role = ChatRoles.System,
            Origin = MessageOrigin.System,
            Content = $"⚠ Error: {errorMessage}",
            ChannelId = channelId,
            ThreadId = threadId,
            ClientType = request.ClientType
        };
    }

    /// <summary>Projects a persisted chat message into the public response shape.</summary>
    public ChatMessageResponse ToResponse(ChatMessageState message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new ChatMessageResponse(
            message.Role,
            message.Content,
            message.CreatedAt,
            message.SenderUserId,
            message.SenderUsername,
            message.SenderAgentId,
            message.SenderAgentName,
            message.ClientType);
    }

    /// <summary>Applies the stable history ordering used by SharpClaw clients.</summary>
    public IReadOnlyList<ChatMessageResponse> ToOrderedHistoryResponses(
        IEnumerable<ChatMessageState> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return messages
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .Select(ToResponse)
            .ToList();
    }

    private static int? PositiveOrNull(int? value) => value is > 0 ? value : null;
}
