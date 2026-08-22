using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

internal static class KernelTestExecution
{
    public static HostActionEntryRequestContext CreateToolContext(
        RequestPrincipal? caller = null,
        ExtensionFeatureSet? features = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new HostActionEntryRequestContext(
            Guid.NewGuid(),
            "tests.in-process",
            HostActionEntryIngress.Tool,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            caller ?? RequestPrincipal.Anonymous,
            features ?? ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            now.AddMinutes(1),
            now.AddMinutes(1));
    }

    public static KernelActionExecutionContext CreateContext() =>
        new(
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid(),
            CreateToolContext());

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
