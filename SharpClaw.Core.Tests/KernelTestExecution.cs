using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Core.Tests;

internal static class KernelTestExecution
{
    public static KernelActionExecutionContext CreateContext() =>
        new(
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());

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
