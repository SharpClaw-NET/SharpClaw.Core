using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Core.Kernel;

public sealed class KernelExternalAuthoritySessionRegistry : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, RegisteredSession> _sessions = [];
    private bool _disposed;

    private sealed record RegisteredSession(
        SidecarCapabilitySession Session,
        string BindingHash,
        long BindingGeneration);

    public IDisposable Register(SidecarCapabilitySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var binding = session.Binding;
        if (binding is null || string.IsNullOrWhiteSpace(binding.Authentication.BindingHash))
            throw new ArgumentException("The sidecar capability session has no valid binding.", nameof(session));

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.ContainsKey(binding.SessionId))
                throw new InvalidOperationException(
                    $"An external authority session is already registered for '{binding.SessionId}'.");

            _sessions.Add(
                binding.SessionId,
                new RegisteredSession(
                    session,
                    binding.Authentication.BindingHash,
                    session.BindingGeneration));
        }

        return new Registration(this, binding.SessionId, session);
    }

    public SidecarCapabilityValidationResult ValidateAndConsume(
        SidecarExternalActionDispatchAuthority authority,
        DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_disposed)
                return RejectUnavailable();

            if (!_sessions.TryGetValue(authority.Call.SessionId, out var registered))
                return RejectUnavailable();

            var binding = registered.Session.Binding;
            if (registered.Session.BindingGeneration != registered.BindingGeneration ||
                !string.Equals(
                    binding.Authentication.BindingHash,
                    registered.BindingHash,
                    StringComparison.Ordinal))
            {
                _sessions.Remove(authority.Call.SessionId);
                return SidecarCapabilityValidationResult.Reject(
                    SidecarCapabilityErrors.InvalidBinding,
                    "The registered sidecar capability session binding changed.");
            }

            var result = registered.Session.ValidateAndConsume(authority, now);
            if (result.Code is SidecarCapabilityErrors.Disconnected or SidecarCapabilityErrors.Expired)
                _sessions.Remove(authority.Call.SessionId);
            return result;
        }
    }

    private void Remove(Guid sessionId, SidecarCapabilitySession session)
    {
        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out var registered) &&
                ReferenceEquals(registered.Session, session))
                _sessions.Remove(sessionId);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _sessions.Clear();
        }
    }

    private static SidecarCapabilityValidationResult RejectUnavailable() =>
        SidecarCapabilityValidationResult.Reject(
            "ACTION_EXTERNAL_AUTHORITY_UNAVAILABLE",
            "A host-owned external authority session registry is required.");

    private sealed class Registration(
        KernelExternalAuthoritySessionRegistry owner,
        Guid sessionId,
        SidecarCapabilitySession session) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Remove(sessionId, session);
        }
    }
}
