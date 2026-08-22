using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Core.Kernel;

public sealed record KernelToolContextIssueRequest(
    Guid InvocationId,
    Guid? ConversationId,
    string ToolCallId,
    string ToolName,
    JsonElement Arguments,
    ActionContext<KernelActionEnvelope>? ParentActionContext)
{
    public bool IsWellFormed =>
        InvocationId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(ToolCallId) &&
        !string.IsNullOrWhiteSpace(ToolName) &&
        Arguments.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
}

public interface IKernelToolContextIssuer
{
    ValueTask<HostActionEntryRequestContext?> IssueAsync(
        KernelToolContextIssueRequest request,
        CancellationToken cancellationToken);
}
