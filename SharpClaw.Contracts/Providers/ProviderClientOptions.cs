namespace SharpClaw.Contracts.Providers;

/// <summary>
/// Provider construction facts supplied by a host or provider adapter.
/// This record intentionally carries no transport handle; adapters own
/// their HTTP client lifetime and any provider-specific transport policy.
/// </summary>
public sealed record ProviderClientOptions(string? Endpoint, string ApiKey)
{
    public static ProviderClientOptions Empty { get; } = new(null, string.Empty);
}
