using System.Security.Cryptography;
using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

internal static class KernelTestExecution
{
    public static ToolInvocation CreateToolInvocation(
        string toolName,
        JsonElement? arguments = null,
        Guid? invocationId = null)
    {
        var id = invocationId ?? Guid.NewGuid();
        var value = arguments ?? JsonSerializer.SerializeToElement(new { });
        return new ToolInvocation(
            id,
            Guid.NewGuid(),
            "call",
            toolName,
            value,
            CreateToolContext(id, toolName, value));
    }

    public static HostActionEntryRequestContext CreateToolContext(
        Guid invocationId,
        string toolName,
        JsonElement arguments,
        ActionContext<KernelActionEnvelope>? parent = null)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.SerializeToUtf8Bytes(arguments);
        return new HostActionEntryRequestContext(
            Guid.NewGuid(),
            "test-capability",
            HostActionEntryIngress.Tool,
            invocationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            parent?.Caller ?? RequestPrincipal.Anonymous,
            parent?.Features ?? ExtensionFeatureSet.Empty,
            parent?.TraceId ?? Guid.NewGuid(),
            parent?.IdempotencyKey ?? Guid.NewGuid(),
            parent?.Deadline ?? now.AddMinutes(1),
            parent?.Deadline ?? now.AddMinutes(1))
        {
            Contribution = new HostActionEntryContribution(
                new HostActionEntryIngressBinding(
                    HostActionEntryIngress.Tool,
                    toolName,
                    null!),
                new HostActionEntryLineage(
                    SharpClawActions.Tools.Invoke,
                    1,
                    "test-descriptor",
                    "SharpClaw.Contracts.Modules.ToolInvocation",
                    1,
                    "test-schema",
                    Convert.ToHexString(SHA256.HashData(payload)),
                    payload.Length)),
            ParentInvocationId = parent?.InvocationId,
            Depth = parent is null ? 0 : parent.Depth + 1,
            Attempt = parent?.Attempt > 0 ? parent.Attempt : 1
        };
    }

    public static KernelActionExecutionContext CreateContext() =>
        new(
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());

    public static IKernelToolContextIssuer CreateToolContextIssuer() =>
        new TestToolContextIssuer();

    public static KernelActionDispatcher CreateDispatcher(
        KernelGraph graph,
        IActionContinuationHost? continuationHost = null,
        ICommittedEventWriter? eventWriter = null,
        IKernelActionResultSnapshotter? resultSnapshotter = null,
        IKernelActionRepeatEvidenceAuthority? repeatEvidenceAuthority = null) =>
        new(
            graph,
            CreateContext(),
            continuationHost,
            eventWriter,
            resultSnapshotter,
            repeatEvidenceAuthority);
}

internal sealed class TestToolContextIssuer : IKernelToolContextIssuer
{
    public List<KernelToolContextIssueRequest> Requests { get; } = [];

    public ValueTask<HostActionEntryRequestContext?> IssueAsync(
        KernelToolContextIssueRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return ValueTask.FromResult<HostActionEntryRequestContext?>(
            KernelTestExecution.CreateToolContext(
                request.InvocationId,
                request.ToolName,
                request.Arguments,
                request.ParentActionContext));
    }
}

internal sealed class MatchingRepeatEvidenceAuthority : IKernelActionRepeatEvidenceAuthority
{
    public ValueTask<KernelActionRepeatEvidence?> AuthorizeAsync(
        KernelActionRepeatEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<KernelActionRepeatEvidence?>(new(
            Guid.NewGuid().ToString("N"),
            request.RequiredKind,
            request.ActionKey,
            request.ActionVersion,
            request.IdempotencyScope,
            request.IdempotencyKey,
            request.PriorInvocationId,
            request.PriorAttempt,
            request.NextInvocationId,
            request.NextAttempt,
            request.RequestedAt,
            request.RequestedAt.AddMinutes(1)));
    }
}
