using SharpClaw.Core.State;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SharpClaw.Contracts.Attributes;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Modules;

namespace SharpClaw.Core.Chat;

/// <summary>
/// Expands SharpClaw chat header templates without depending on a host store.
/// </summary>
public sealed partial class ChatHeaderTemplateEngine(
    ModuleRegistry moduleRegistry,
    ProviderApiClientFactory clientFactory)
{
    private static readonly ConcurrentDictionary<Type, HashSet<string>> SensitiveFieldCache = new();
    private readonly ChatHeaderGrantFormatter _grantFormatter = new(moduleRegistry);

    /// <summary>
    /// Extracts unique tag names from a header template in encounter order.
    /// </summary>
    public static IReadOnlyList<string> ExtractTagNames(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var matches = TagPattern().Matches(template);
        if (matches.Count == 0)
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            var name = match.Groups["name"].Value;
            if (seen.Add(name))
                names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// Expands all recognized header tags in <paramref name="template"/>.
    /// </summary>
    public async Task<string> ExpandAsync(
        string template,
        ChatHeaderExpansionContext context,
        ChatHeaderExpansionOptions options,
        IChatHeaderResourceTagResolver? resourceTags,
        IServiceProvider serviceProvider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        if (options.DisableHeaderTagExpansion)
            return template;

        var matches = TagPattern().Matches(template);
        if (matches.Count == 0)
            return template;

        var sb = new StringBuilder(template.Length * 2);
        var lastIdx = 0;

        foreach (Match match in matches)
        {
            sb.Append(template, lastIdx, match.Index - lastIdx);
            lastIdx = match.Index + match.Length;

            var tagName = match.Groups["name"].Value;
            var itemTemplate = match.Groups["tpl"].Success
                ? match.Groups["tpl"].Value
                : null;

            var expanded = await ExpandTagAsync(
                tagName,
                itemTemplate,
                context,
                options,
                resourceTags,
                serviceProvider,
                ct);
            sb.Append(expanded);
        }

        sb.Append(template, lastIdx, template.Length - lastIdx);
        return sb.ToString();
    }

    private async Task<string> ExpandTagAsync(
        string tagName,
        string? itemTemplate,
        ChatHeaderExpansionContext ctx,
        ChatHeaderExpansionOptions options,
        IChatHeaderResourceTagResolver? resourceTags,
        IServiceProvider serviceProvider,
        CancellationToken ct)
    {
        return tagName.ToLowerInvariant() switch
        {
            "time" => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"),
            "user" => ctx.User?.Username ?? "(unknown)",
            "via" => ctx.ClientType,
            "role" => FormatUserRole(ctx),
            "bio" => ctx.User?.Bio ?? "",
            "agent-name" => ctx.Agent.Name,
            "agent-role" => await FormatAgentRoleAsync(ctx, serviceProvider, ct),
            "clearance" => "(per-action; see grants)",
            "grants" => FormatGrants(ctx.UserPs),
            "agent-grants" => await FormatAgentGrantsAsync(ctx, serviceProvider, ct),
            "reasoning-effort" => FormatReasoningEffortNotice(ctx),
            _ => await TryExpandModuleTagAsync(tagName, ctx, options, serviceProvider, ct)
                 ?? await TryExpandResourceTagAsync(tagName, itemTemplate, resourceTags, ct)
                 ?? $"{{{{unknown:{tagName}}}}}"
        };
    }

    private string FormatUserRole(ChatHeaderExpansionContext ctx)
    {
        if (ctx.User?.Role is null || ctx.UserPs is null)
            return "(none)";

        var grants = _grantFormatter.FormatGrantNames(ctx.UserPs);
        return grants.Count > 0
            ? $"{ctx.User.Role.Name} ({string.Join(", ", grants)})"
            : ctx.User.Role.Name;
    }

    private async Task<string> FormatAgentRoleAsync(
        ChatHeaderExpansionContext ctx,
        IServiceProvider serviceProvider,
        CancellationToken ct)
    {
        if (ctx.AgentRole is null)
            return "(none)";

        var sb = new StringBuilder();
        sb.Append(ctx.AgentRole.Name);
        if (ctx.AgentPs is not null)
        {
            var grants = await _grantFormatter.FormatGrantNamesWithResourcesAsync(
                ctx.AgentPs,
                serviceProvider,
                ct);
            if (grants.Count > 0)
                sb.Append(" (").Append(string.Join(", ", grants)).Append(')');
        }

        return sb.ToString();
    }

    private async Task<string> FormatAgentGrantsAsync(
        ChatHeaderExpansionContext ctx,
        IServiceProvider serviceProvider,
        CancellationToken ct)
    {
        if (ctx.AgentPs is null)
            return "(none)";

        var grants = await _grantFormatter.FormatGrantNamesWithResourcesAsync(
            ctx.AgentPs,
            serviceProvider,
            ct);
        return grants.Count > 0 ? string.Join(", ", grants) : "(none)";
    }

    private string FormatGrants(PermissionSetState? ps)
    {
        if (ps is null)
            return "(none)";

        var grants = _grantFormatter.FormatGrantNames(ps);
        return grants.Count > 0 ? string.Join(", ", grants) : "(none)";
    }

    private async Task<string?> TryExpandModuleTagAsync(
        string tagName,
        ChatHeaderExpansionContext ctx,
        ChatHeaderExpansionOptions options,
        IServiceProvider serviceProvider,
        CancellationToken ct)
    {
        var tag = moduleRegistry.GetHeaderTag(tagName);
        if (tag is null)
            return null;

        if (options.DisableModuleHeaderTags)
            return "";

        if (tag.ResolveWithContext is not null)
        {
            var moduleContext = new ModuleHeaderTagContext(
                ctx.Channel.Id,
                ctx.Channel.Title,
                ctx.Agent.Id,
                ctx.Agent.Name,
                ctx.ClientType,
                ctx.User?.Id,
                ctx.CompletionParameters,
                ctx.ProviderKey);
            return await tag.ResolveWithContext(serviceProvider, moduleContext, ct);
        }

        return await tag.Resolve(serviceProvider, ct);
    }

    private string FormatReasoningEffortNotice(ChatHeaderExpansionContext ctx)
    {
        if (ctx.CompletionParameters?.ReasoningEffort is not { } effort)
            return "";

        var spec = clientFactory.GetParameterSpec(ctx.ProviderKey);
        if (!spec.ReasoningEffortInformationalOnly)
            return "";

        return ChatHeaderNotices.FormatReasoningEffortNotice(effort);
    }

    private async Task<string?> TryExpandResourceTagAsync(
        string tagName,
        string? itemTemplate,
        IChatHeaderResourceTagResolver? resourceTags,
        CancellationToken ct)
    {
        if (resourceTags is null)
            return null;

        var entities = await resourceTags.LoadEntitiesAsync(tagName, ct);
        if (entities is null)
            return null;

        if (entities.Count == 0)
            return "(none)";

        if (itemTemplate is null)
            return string.Join(", ", entities.Select(e => e.Id.ToString("D")));

        var entityType = entities[0].GetType();
        var sensitiveFields = GetSensitiveFields(entityType);

        var items = new List<string>(entities.Count);
        foreach (var entity in entities)
        {
            var formatted = FieldPattern().Replace(itemTemplate, m =>
            {
                var fieldName = m.Groups["field"].Value;

                if (sensitiveFields.Contains(fieldName))
                    return "[redacted]";

                var prop = entityType.GetProperty(
                    fieldName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop is null)
                    return $"[{fieldName}?]";

                var value = prop.GetValue(entity);
                return value?.ToString() ?? "";
            });
            items.Add(formatted);
        }

        return string.Join(", ", items);
    }

    private static HashSet<string> GetSensitiveFields(Type type)
    {
        return SensitiveFieldCache.GetOrAdd(type, static t =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetCustomAttribute<HeaderSensitiveAttribute>() is not null)
                    set.Add(prop.Name);
            }

            return set;
        });
    }

    [GeneratedRegex(@"\{\{(?<name>[A-Za-z][A-Za-z0-9\-_]*?)(?::(?<tpl>\{[^}]+\}(?:[^{}]*\{[^}]+\})*[^{}]*))?\}\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\{(?<field>[A-Za-z][A-Za-z0-9_]*)\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex FieldPattern();
}

/// <summary>
/// Store-neutral facts used while expanding a chat header template.
/// </summary>
public sealed record ChatHeaderExpansionContext(
    ChannelState Channel,
    AgentState Agent,
    string ClientType,
    UserState? User,
    PermissionSetState? UserPs,
    RoleState? AgentRole,
    PermissionSetState? AgentPs,
    CompletionParameters? CompletionParameters = null,
    string ProviderKey = "");

/// <summary>
/// Runtime switches for chat header expansion.
/// </summary>
public sealed record ChatHeaderExpansionOptions(
    bool DisableHeaderTagExpansion = false,
    bool DisableModuleHeaderTags = false);

/// <summary>
/// Host adapter used by Core to load resource-tag entities.
/// </summary>
public interface IChatHeaderResourceTagResolver
{
    /// <summary>
    /// Loads entities for a resource tag, or returns null when the tag is unknown.
    /// </summary>
    Task<IReadOnlyList<DomainState>?> LoadEntitiesAsync(
        string tagName,
        CancellationToken ct);
}
