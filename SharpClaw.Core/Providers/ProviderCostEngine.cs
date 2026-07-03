using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Providers;

/// <summary>
/// Store-neutral provider cost reporting rules used by SharpClaw runtimes.
/// </summary>
public sealed class ProviderCostEngine
{
    /// <summary>
    /// Currency reported when no provider cost feed data is available.
    /// </summary>
    public const string DefaultFallbackCurrency = "usd";

    /// <summary>Resolves the requested cost period.</summary>
    public (DateTimeOffset Start, DateTimeOffset End) ResolvePeriod(
        int days,
        DateTimeOffset? startDate,
        DateTimeOffset? endDate,
        DateTimeOffset? now = null)
    {
        var end = endDate ?? now ?? DateTimeOffset.UtcNow;
        var start = startDate ?? end.AddDays(-days);
        return (start, end);
    }

    /// <summary>
    /// Fetches the live cost report for one configured provider.
    /// </summary>
    public async Task<ProviderCostResponse?> GetCostAsync(
        Guid providerId,
        int days,
        DateTimeOffset? startDate,
        DateTimeOffset? endDate,
        IProviderCostHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var provider = await host.LoadProviderAsync(providerId, ct);
        if (provider is null)
            return null;

        var (periodStart, periodEnd) = ResolvePeriod(
            days,
            startDate,
            endDate);

        return await GetCostForProviderAsync(
            provider.Value,
            periodStart,
            periodEnd,
            host,
            ct);
    }

    /// <summary>
    /// Fetches and aggregates live cost reports for configured providers.
    /// </summary>
    public async Task<ProviderCostTotalResponse> GetTotalCostAsync(
        int days,
        DateTimeOffset? startDate,
        DateTimeOffset? endDate,
        bool includeAll,
        IProviderCostHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var (periodStart, periodEnd) = ResolvePeriod(
            days,
            startDate,
            endDate);
        var providers = await host.ListProvidersForCostAsync(includeAll, ct);
        var results = new List<ProviderCostResponse>(providers.Count);

        foreach (var provider in providers)
        {
            var cost = await GetCostForProviderAsync(
                provider,
                periodStart,
                periodEnd,
                host,
                ct);
            if (cost is not null)
                results.Add(cost);
        }

        return CreateTotalResponse(periodStart, periodEnd, results);
    }

    /// <summary>Projects a successful provider cost feed result.</summary>
    public ProviderCostResponse CreateFeedResponse(
        ProviderCostProvider provider,
        bool isLocal,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        ProviderCostResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new ProviderCostResponse(
            provider.Id,
            provider.Name,
            provider.ProviderKey,
            IsLocal: isLocal,
            CostApiSupported: true,
            TotalCost: result.TotalAmount,
            Currency: result.Currency,
            PeriodStart: periodStart,
            PeriodEnd: periodEnd,
            DailyBreakdown: result.DailyBuckets
                .Select(bucket => new CostDailyBucket(
                    bucket.Start,
                    bucket.End,
                    bucket.Amount,
                    result.Currency))
                .ToList(),
            Note: isLocal ? "Local provider - no cloud API costs incurred." : null);
    }

    /// <summary>Creates the response for a supported feed whose key lacks billing permissions.</summary>
    public ProviderCostResponse CreatePermissionDeniedResponse(
        ProviderCostProvider provider,
        bool isLocal,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        string permissionDeniedNote)
    {
        return new ProviderCostResponse(
            provider.Id,
            provider.Name,
            provider.ProviderKey,
            IsLocal: isLocal,
            CostApiSupported: true,
            TotalCost: 0,
            Currency: DefaultFallbackCurrency,
            PeriodStart: periodStart,
            PeriodEnd: periodEnd,
            DailyBreakdown: null,
            Note: permissionDeniedNote);
    }

    /// <summary>Creates the response for a provider that has no cost API surface.</summary>
    public ProviderCostResponse CreateUnsupportedResponse(
        ProviderCostProvider provider,
        bool isLocal,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd)
    {
        return new ProviderCostResponse(
            provider.Id,
            provider.Name,
            provider.ProviderKey,
            IsLocal: isLocal,
            CostApiSupported: false,
            TotalCost: 0,
            Currency: DefaultFallbackCurrency,
            PeriodStart: periodStart,
            PeriodEnd: periodEnd,
            DailyBreakdown: null,
            Note: $"Provider key '{provider.ProviderKey}' does not expose a cost API. "
                + "Check the provider's dashboard for billing information.");
    }

    /// <summary>Aggregates per-provider cost responses into the total response.</summary>
    public ProviderCostTotalResponse CreateTotalResponse(
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        IReadOnlyList<ProviderCostResponse> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var totalCost = providers.Sum(provider => provider.TotalCost);
        var currency = providers
            .FirstOrDefault(provider => provider.CostApiSupported && provider.TotalCost > 0)
            ?.Currency
            ?? DefaultFallbackCurrency;

        return new ProviderCostTotalResponse(
            totalCost,
            currency,
            periodStart,
            periodEnd,
            providers);
    }

    private async Task<ProviderCostResponse> GetCostForProviderAsync(
        ProviderCostProviderConfiguration provider,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        IProviderCostHost host,
        CancellationToken ct)
    {
        var plugin = host.GetProviderPlugin(provider.ProviderKey);
        var isLocal = plugin is { RequiresApiKey: false };
        var costProvider = new ProviderCostProvider(
            provider.Id,
            provider.Name,
            provider.ProviderKey);

        if (plugin is { SupportsCostFeed: true }
            && (!plugin.RequiresApiKey || !string.IsNullOrEmpty(provider.ProtectedApiKey)))
        {
            var result = await host.GetCostsAsync(
                provider,
                plugin,
                periodStart,
                periodEnd,
                ct);
            if (result is not null)
            {
                return CreateFeedResponse(
                    costProvider,
                    isLocal,
                    periodStart,
                    periodEnd,
                    result);
            }

            return CreatePermissionDeniedResponse(
                costProvider,
                isLocal,
                periodStart,
                periodEnd,
                plugin.CostFeedPermissionDeniedNote);
        }

        return CreateUnsupportedResponse(
            costProvider,
            isLocal,
            periodStart,
            periodEnd);
    }
}

/// <summary>
/// Store-neutral provider identity needed to shape provider cost responses.
/// </summary>
public readonly record struct ProviderCostProvider(
    Guid Id,
    string Name,
    string ProviderKey);

/// <summary>
/// Store-neutral provider configuration needed by the cost workflow.
/// </summary>
public readonly record struct ProviderCostProviderConfiguration(
    Guid Id,
    string Name,
    string ProviderKey,
    string? ApiEndpoint,
    string? ProtectedApiKey);

/// <summary>
/// Host boundary for provider cost reporting. Hosts own persistence,
/// secret protection, plugin lookup, and HTTP execution.
/// </summary>
public interface IProviderCostHost
{
    /// <summary>Loads one configured provider for cost reporting.</summary>
    Task<ProviderCostProviderConfiguration?> LoadProviderAsync(
        Guid providerId,
        CancellationToken ct);

    /// <summary>Lists configured providers eligible for cost reporting.</summary>
    Task<IReadOnlyList<ProviderCostProviderConfiguration>> ListProvidersForCostAsync(
        bool includeAll,
        CancellationToken ct);

    /// <summary>Returns the provider plugin for a provider key, if registered.</summary>
    IProviderPlugin? GetProviderPlugin(string providerKey);

    /// <summary>Unprotects a stored provider API key.</summary>
    string UnprotectProviderSecret(string protectedSecret);

    /// <summary>Executes a provider cost feed through host-owned transport.</summary>
    Task<ProviderCostResult?> GetCostsAsync(
        ProviderCostProviderConfiguration provider,
        IProviderPlugin plugin,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        CancellationToken ct);
}
