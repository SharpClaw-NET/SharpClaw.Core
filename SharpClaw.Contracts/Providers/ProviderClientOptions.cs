namespace SharpClaw.Contracts.Providers;

/// <summary>
/// Non-secret provider construction facts supplied by a host or provider
/// adapter. This record intentionally carries no transport handle and no
/// credentials; adapters own HTTP client lifetime, credential binding, and
/// provider-specific transport policy.
/// </summary>
public sealed record ProviderClientOptions(string? Endpoint)
{
    public static ProviderClientOptions Empty { get; } = new((string?)null);
}
