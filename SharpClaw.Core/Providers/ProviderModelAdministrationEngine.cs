using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Providers;

/// <summary>
/// Store-neutral provider and model catalog administration workflow.
/// Hosts provide persistence, encryption, HTTP, and plugin lookup; Core owns
/// validation order, entity mutation sequence, sync semantics, and response
/// projection.
/// </summary>
public sealed class ProviderModelAdministrationEngine(
    ProviderCatalogEngine providers,
    ModelCatalogEngine models)
{
    public ProviderModelAdministrationEngine()
        : this(new ProviderCatalogEngine(), new ModelCatalogEngine())
    {
    }

    public IReadOnlyList<ProviderTypeResponse> ListAvailableTypes(
        IProviderModelAdministrationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return providers.ListAvailableTypes(host.ProviderPlugins);
    }

    /// <summary>
    /// Lists configured providers using the host store and Core projection
    /// semantics.
    /// </summary>
    public async Task<IReadOnlyList<ProviderResponse>> ListProvidersAsync(
        IProviderModelAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var providerRows = await host.ListProvidersAsync(ct);
        return providerRows
            .Select(ToProviderResponse)
            .ToList();
    }

    /// <summary>
    /// Loads one configured provider and projects it to the public response.
    /// </summary>
    public async Task<ProviderResponse?> GetProviderAsync(
        Guid providerId,
        IProviderModelAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var provider = await host.LoadProviderAsync(providerId, ct);
        return provider is null ? null : ToProviderResponse(provider);
    }

    public async Task<ProviderResponse> CreateProviderAsync(
        CreateProviderRequest request,
        IProviderModelAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var plugin = host.GetProviderPlugin(request.ProviderKey);
        var plan = providers.PlanCreate(
            request,
            plugin,
            host.UniqueProviderNamesEnforced,
            await host.ListProviderNamesAsync(null, ct));

        var provider = new ProviderDB
        {
            Name = plan.Name,
            ProviderKey = plan.ProviderKey,
            ApiEndpoint = plan.ApiEndpointToStore,
            EncryptedApiKey = plan.ApiKey is not null
                ? host.ProtectProviderSecret(plan.ApiKey)
                : null
        };

        host.TrackProvider(provider);
        await host.SaveAsync(ct);
        return ToProviderResponse(provider);
    }

    public async Task<ProviderResponse?> UpdateProviderAsync(
        Guid providerId,
        UpdateProviderRequest request,
        IProviderModelAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var provider = await host.LoadProviderAsync(providerId, ct);
        if (provider is null)
            return null;

        var plan = providers.PlanUpdate(
            provider.Name,
            request,
            host.UniqueProviderNamesEnforced,
            await host.ListProviderNamesAsync(providerId, ct));

        provider.Name = plan.Name;
        if (plan.UpdateApiEndpoint)
            provider.ApiEndpoint = plan.ApiEndpoint;

        await host.SaveAsync(ct);
        return ToProviderResponse(provider);
    }

    public async Task<bool> DeleteProviderAsync(
        Guid providerId,
        IProviderModelAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var provider = await host.LoadProviderAsync(providerId, ct);
        if (provider is null)
            return false;

        host.RemoveProvider(provider);
        await host.SaveAsync(ct);
        return true;
    }

    public async Task SetProviderApiKeyAsync(
        Guid providerId,
        string apiKey,
        IProviderModelAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(host);

        var provider = await host.LoadProviderAsync(providerId, ct)
            ?? throw new ArgumentException($"Provider {providerId} not found.");

        provider.EncryptedApiKey = host.ProtectProviderSecret(apiKey);
        await host.SaveAsync(ct);
    }

    public async Task<DeviceCodeSession> StartDeviceCodeFlowAsync(
        Guid providerId,
        IProviderModelAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var provider = await host.LoadProviderAsync(providerId, ct)
            ?? throw new ArgumentException($"Provider {providerId} not found.");

        var deviceCodeFlow = host.GetProviderPlugin(provider.ProviderKey)
            ?.DeviceCodeFlow
            ?? throw new InvalidOperationException(
                $"Provider key '{provider.ProviderKey}' does not support device code authentication.");

        return await host.StartDeviceCodeFlowAsync(deviceCodeFlow, ct);
    }

    public async Task CompleteDeviceCodeFlowAsync(
        Guid providerId,
        DeviceCodeSession session,
        IProviderModelAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(host);

        var provider = await host.LoadProviderAsync(providerId, ct)
            ?? throw new ArgumentException($"Provider {providerId} not found.");

        var deviceCodeFlow = host.GetProviderPlugin(provider.ProviderKey)
            ?.DeviceCodeFlow
            ?? throw new InvalidOperationException(
                $"Provider key '{provider.ProviderKey}' does not support device code authentication.");

        var accessToken = await host.PollDeviceCodeFlowAsync(
            deviceCodeFlow,
            session,
            ct)
            ?? throw new InvalidOperationException(
                $"Device code flow for provider '{provider.ProviderKey}' did not return an access token.");

        provider.EncryptedApiKey = host.ProtectProviderSecret(accessToken);
        await host.SaveAsync(ct);
    }

    public bool SupportsDeviceCodeAuth(
        string providerKey,
        IProviderModelAdministrationHost host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentNullException.ThrowIfNull(host);
        return host.GetProviderPlugin(providerKey)?.DeviceCodeFlow is not null;
    }

    public async Task<int> RefreshCapabilitiesAsync(
        Guid providerId,
        IProviderModelAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var provider = await host.LoadProviderAsync(providerId, ct)
            ?? throw new ArgumentException($"Provider {providerId} not found.");

        var resolver = host.GetProviderPlugin(provider.ProviderKey)
            ?.Capabilities
            ?? throw new ProviderUnavailableException(provider.ProviderKey);

        var providerModels = await host.ListModelsForProviderAsync(
            providerId,
            ct);

        var updated = 0;
        foreach (var model in providerModels)
        {
            if (models.RefreshCapabilityTags(model, resolver))
                updated++;
        }

        if (updated > 0)
            await host.SaveAsync(ct);

        return updated;
    }

    public async Task<IReadOnlyList<ModelResponse>> SyncModelsAsync(
        Guid providerId,
        IProviderModelAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var provider = await host.LoadProviderWithModelsAsync(providerId, ct)
            ?? throw new ArgumentException($"Provider {providerId} not found.");

        var plugin = host.GetProviderPlugin(provider.ProviderKey);
        providers.EnsureCanSyncModels(
            provider.ProviderKey,
            !string.IsNullOrEmpty(provider.EncryptedApiKey),
            plugin);

        if (plugin is null)
            throw new ProviderUnavailableException(provider.ProviderKey);

        var modelIds = await host.ListProviderModelIdsAsync(
            provider,
            plugin,
            ct);

        var existingNames = provider.Models
            .Select(model => model.Name)
            .ToHashSet();

        var newModels = models.BuildMissingModels(
            provider.Id,
            modelIds,
            existingNames,
            plugin.Capabilities);

        if (newModels.Count > 0)
        {
            host.TrackModels(newModels);
            await host.SaveAsync(ct);
        }

        var providerModels = await host.ListModelsForProviderAsync(
            providerId,
            ct);

        return providerModels
            .Select(model => models.ToResponse(model, provider))
            .ToList();
    }

    public async Task<ModelResponse> CreateModelAsync(
        CreateModelRequest request,
        IProviderModelAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var provider = await host.LoadProviderAsync(request.ProviderId, ct)
            ?? throw new ArgumentException($"Provider {request.ProviderId} not found.");

        if (host.UniqueModelNamesEnforced)
        {
            models.EnsureModelNameAvailable(
                request.Name,
                await host.ModelNameExistsAsync(request.Name, null, ct));
        }

        var model = models.Create(request, provider);
        host.TrackModel(model);
        await host.SaveAsync(ct);
        return models.ToResponse(model, provider);
    }

    /// <summary>
    /// Lists configured models, optionally restricted to one provider.
    /// </summary>
    public async Task<IReadOnlyList<ModelResponse>> ListModelsAsync(
        Guid? providerId,
        IProviderModelAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var modelRows = await host.ListModelsAsync(providerId, ct);
        return modelRows
            .Select(model => models.ToResponse(model))
            .ToList();
    }

    /// <summary>
    /// Loads one configured model and projects it to the public response.
    /// </summary>
    public async Task<ModelResponse?> GetModelAsync(
        Guid modelId,
        IProviderModelAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var model = await host.LoadModelAsync(modelId, ct);
        return model is null ? null : models.ToResponse(model);
    }

    public async Task<ModelResponse?> UpdateModelAsync(
        Guid modelId,
        UpdateModelRequest request,
        IProviderModelAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var model = await host.LoadModelAsync(modelId, ct);
        if (model is null)
            return null;

        var nameExists = request.Name is not null && host.UniqueModelNamesEnforced
            && !request.Name.Trim().Equals(
                model.Name.Trim(),
                StringComparison.OrdinalIgnoreCase)
            && await host.ModelNameExistsAsync(request.Name, modelId, ct);

        models.ApplyUpdate(
            model,
            request,
            host.UniqueModelNamesEnforced,
            nameExists);

        await host.SaveAsync(ct);
        return models.ToResponse(model);
    }

    public async Task<bool> DeleteModelAsync(
        Guid modelId,
        IProviderModelAdministrationHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var model = await host.LoadModelAsync(modelId, ct);
        if (model is null)
            return false;

        host.RemoveModel(model);
        await host.SaveAsync(ct);
        return true;
    }

    private static ProviderResponse ToProviderResponse(ProviderDB provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new ProviderResponse(
            provider.Id,
            provider.Name,
            provider.ProviderKey,
            provider.ApiEndpoint,
            provider.EncryptedApiKey is not null);
    }
}

/// <summary>
/// Host boundary for provider/model catalog administration.
/// </summary>
public interface IProviderModelAdministrationHost
{
    bool UniqueProviderNamesEnforced { get; }

    bool UniqueModelNamesEnforced { get; }

    IEnumerable<IProviderPlugin> ProviderPlugins { get; }

    IProviderPlugin? GetProviderPlugin(string providerKey);

    string ProtectProviderSecret(string secret);

    string UnprotectProviderSecret(string protectedSecret);

    Task<ProviderDB?> LoadProviderAsync(Guid providerId, CancellationToken ct);

    Task<ProviderDB?> LoadProviderWithModelsAsync(
        Guid providerId,
        CancellationToken ct);

    Task<ModelDB?> LoadModelAsync(Guid modelId, CancellationToken ct);

    /// <summary>Loads all configured providers for catalog projection.</summary>
    Task<IReadOnlyList<ProviderDB>> ListProvidersAsync(CancellationToken ct);

    /// <summary>Loads configured models for catalog projection.</summary>
    Task<IReadOnlyList<ModelDB>> ListModelsAsync(
        Guid? providerId,
        CancellationToken ct);

    Task<IReadOnlyList<ModelDB>> ListModelsForProviderAsync(
        Guid providerId,
        CancellationToken ct);

    Task<IReadOnlyList<string>> ListProviderNamesAsync(
        Guid? excludeId,
        CancellationToken ct);

    Task<bool> ModelNameExistsAsync(
        string name,
        Guid? excludeId,
        CancellationToken ct);

    Task<IReadOnlyList<string>> ListProviderModelIdsAsync(
        ProviderDB provider,
        IProviderPlugin plugin,
        CancellationToken ct);

    Task<DeviceCodeSession> StartDeviceCodeFlowAsync(
        IDeviceCodeFlow deviceCodeFlow,
        CancellationToken ct);

    Task<string?> PollDeviceCodeFlowAsync(
        IDeviceCodeFlow deviceCodeFlow,
        DeviceCodeSession session,
        CancellationToken ct);

    void TrackProvider(ProviderDB provider);

    void TrackModel(ModelDB model);

    void TrackModels(IReadOnlyList<ModelDB> models);

    void RemoveProvider(ProviderDB provider);

    void RemoveModel(ModelDB model);

    Task SaveAsync(CancellationToken ct);
}
