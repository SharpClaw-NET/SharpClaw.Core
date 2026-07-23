using System.Text.Json;
using SharpClaw.Contracts.Attributes;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Core.State;

/// <summary>
/// Host-owned identity and timestamps shared by neutral Core state objects.
/// These objects contain no persistence annotations or provider concepts.
/// </summary>
public abstract class DomainState
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? CustomId { get; set; }
}

public sealed class ProviderState : DomainState
{
    public required string Name { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public string? ApiEndpoint { get; set; }
    [HeaderSensitive]
    public string? EncryptedApiKey { get; set; }
    public ICollection<ModelState> Models { get; set; } = [];
}

public sealed class ModelState : DomainState
{
    public required string Name { get; set; }
    public string? CapabilityTagsRaw { get; set; }
    public IReadOnlySet<string> CapabilityTags =>
        string.IsNullOrEmpty(CapabilityTagsRaw)
            ? new HashSet<string>()
            : new HashSet<string>(
                CapabilityTagsRaw.Split(','),
                StringComparer.OrdinalIgnoreCase);
    public Guid ProviderId { get; set; }
    public ProviderState Provider { get; set; } = null!;
    public ICollection<AgentState> Agents { get; set; } = [];
}

public sealed class AgentState : DomainState
{
    public required string Name { get; set; }
    public string? SystemPrompt { get; set; }
    public int? MaxCompletionTokens { get; set; }
    public float? Temperature { get; set; }
    public float? TopP { get; set; }
    public int? TopK { get; set; }
    public float? FrequencyPenalty { get; set; }
    public float? PresencePenalty { get; set; }
    public string[]? Stop { get; set; }
    public int? Seed { get; set; }
    public JsonElement? ResponseFormat { get; set; }
    public string? ReasoningEffort { get; set; }
    public Dictionary<string, JsonElement>? ProviderParameters { get; set; }
    public string? CustomChatHeader { get; set; }
    public bool DisableToolSchemas { get; set; }
    public Guid? ToolAwarenessSetId { get; set; }
    public ToolAwarenessSetState? ToolAwarenessSet { get; set; }
    public Guid ModelId { get; set; }
    public ModelState Model { get; set; } = null!;
    public Guid? RoleId { get; set; }
    public RoleState? Role { get; set; }
    public ICollection<ChannelContextState> Contexts { get; set; } = [];
    public ICollection<ChannelState> Channels { get; set; } = [];
    public ICollection<ChannelState> AllowedChannels { get; set; } = [];
    public ICollection<ChannelContextState> AllowedContexts { get; set; } = [];
}

public sealed class ToolAwarenessSetState : DomainState
{
    public required string Name { get; set; }
    public Dictionary<string, bool> Tools { get; set; } = [];
}

public sealed class UserState : DomainState
{
    public required string Username { get; set; }
    [HeaderSensitive]
    public required byte[] PasswordHash { get; set; }
    [HeaderSensitive]
    public required byte[] PasswordSalt { get; set; }
    public bool IsUserAdmin { get; set; }
    public string? Bio { get; set; }
    public DateTimeOffset AccessTokensInvalidatedAt { get; set; }
    public Guid? RoleId { get; set; }
    public RoleState? Role { get; set; }
}

public sealed class RoleState : DomainState
{
    public required string Name { get; set; }
    public Guid? PermissionSetId { get; set; }
    public PermissionSetState? PermissionSet { get; set; }
    public ICollection<UserState> Users { get; set; } = [];
}

public sealed class PermissionSetState : DomainState
{
    public ICollection<GlobalFlagState> GlobalFlags { get; set; } = [];
    public ICollection<ResourceAccessState> ResourceAccesses { get; set; } = [];
    public ISet<Guid> ClearanceUserWhitelist { get; set; } = new HashSet<Guid>();
    public ISet<Guid> ClearanceAgentWhitelist { get; set; } = new HashSet<Guid>();
}

public sealed class GlobalFlagState : DomainState
{
    public required string FlagKey { get; set; }
    public PermissionClearance Clearance { get; set; } = PermissionClearance.Unset;
    public Guid PermissionSetId { get; set; }
}

public sealed class ResourceAccessState : DomainState
{
    public required string ResourceType { get; set; }
    public Guid ResourceId { get; set; }
    public PermissionClearance Clearance { get; set; } = PermissionClearance.Unset;
    public Guid PermissionSetId { get; set; }
    public string SubType { get; set; } = string.Empty;
    public string? AccessLevel { get; set; }
    public bool IsDefault { get; set; }
}

public sealed class ChannelContextState : DomainState
{
    public required string Name { get; set; }
    public Guid AgentId { get; set; }
    public AgentState Agent { get; set; } = null!;
    public Guid? PermissionSetId { get; set; }
    public PermissionSetState? PermissionSet { get; set; }
    public Guid? DefaultResourceSetId { get; set; }
    public DefaultResourceSetState? DefaultResourceSet { get; set; }
    public bool DisableChatHeader { get; set; }
    public ICollection<AgentState> AllowedAgents { get; set; } = [];
    public ICollection<ChannelState> Channels { get; set; } = [];
}

public sealed class ChannelState : DomainState
{
    public required string Title { get; set; }
    public Guid? AgentId { get; set; }
    public AgentState? Agent { get; set; }
    public Guid? AgentContextId { get; set; }
    public ChannelContextState? AgentContext { get; set; }
    public Guid? PermissionSetId { get; set; }
    public PermissionSetState? PermissionSet { get; set; }
    public Guid? DefaultResourceSetId { get; set; }
    public DefaultResourceSetState? DefaultResourceSet { get; set; }
    public bool DisableChatHeader { get; set; }
    public string? CustomChatHeader { get; set; }
    public bool DisableToolSchemas { get; set; }
    public Guid? ToolAwarenessSetId { get; set; }
    public ToolAwarenessSetState? ToolAwarenessSet { get; set; }
    public ICollection<AgentState> AllowedAgents { get; set; } = [];
    public ICollection<ChatMessageState> ChatMessages { get; set; } = [];
    public ICollection<ChatThreadState> Threads { get; set; } = [];
}

public sealed class ChatThreadState : DomainState
{
    public required string Name { get; set; }
    public int? MaxMessages { get; set; }
    public int? MaxCharacters { get; set; }
    public Guid ChannelId { get; set; }
    public ChannelState Channel { get; set; } = null!;
    public ICollection<ChatMessageState> ChatMessages { get; set; } = [];
}

public sealed class ChatMessageState : DomainState
{
    public required string Role { get; set; }
    public MessageOrigin? Origin { get; set; }
    public required string Content { get; set; }
    public string? ProviderMetadataJson { get; set; }
    public Guid ChannelId { get; set; }
    public ChannelState Channel { get; set; } = null!;
    public Guid? ThreadId { get; set; }
    public ChatThreadState? Thread { get; set; }
    public Guid? SenderUserId { get; set; }
    public string? SenderUsername { get; set; }
    public Guid? SenderAgentId { get; set; }
    public string? SenderAgentName { get; set; }
    public Guid? PermissionRoleId { get; set; }
    public string? PermissionRoleName { get; set; }
    public string? ClientType { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
}

public sealed class DefaultResourceSetState : DomainState
{
    public List<DefaultResourceEntryState> Entries { get; set; } = [];
}

public sealed class DefaultResourceEntryState : DomainState
{
    public Guid DefaultResourceSetId { get; set; }
    public string ResourceKey { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
}
