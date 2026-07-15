using SharpClaw.Core.State;
using System.Linq.Expressions;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Core.Providers;

/// <summary>
/// Store-neutral model catalog rules used by SharpClaw runtimes.
/// </summary>
public sealed class ModelCatalogEngine
{
    /// <summary>Returns whether unique-name enforcement should be active.</summary>
    public static bool IsUniqueNameEnforced(string? configurationValue)
    {
        return configurationValue is null
            || !bool.TryParse(configurationValue, out var enforced)
            || enforced;
    }

    /// <summary>Throws when a model name is already present.</summary>
    public void EnsureModelNameAvailable(string name, bool exists)
    {
        if (exists)
            throw new InvalidOperationException($"A model named '{name}' already exists.");
    }

    /// <summary>Serializes model capability tags into the persisted shape.</summary>
    public string? SerializeCapabilityTags(IReadOnlyCollection<string>? capabilityTags)
    {
        return capabilityTags is { Count: > 0 }
            ? string.Join(',', capabilityTags)
            : null;
    }

    /// <summary>Creates a model entity from a request and loaded provider.</summary>
    public ModelState Create(CreateModelRequest request, ProviderState provider)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(provider);

        return new ModelState
        {
            Name = request.Name,
            ProviderId = provider.Id,
            Provider = provider,
            CustomId = request.CustomId,
            CapabilityTagsRaw = SerializeCapabilityTags(request.CapabilityTags)
        };
    }

    /// <summary>Applies an update request to an existing model entity.</summary>
    public void ApplyUpdate(
        ModelState model,
        UpdateModelRequest request,
        bool enforceUniqueNames,
        bool nameExists)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Name is not null)
        {
            if (enforceUniqueNames)
                EnsureModelNameAvailable(request.Name, nameExists);

            model.Name = request.Name;
        }

        if (request.CustomId is not null)
            model.CustomId = request.CustomId;

        if (request.CapabilityTags is not null)
            model.CapabilityTagsRaw = SerializeCapabilityTags(request.CapabilityTags);
    }

    /// <summary>
    /// Refreshes a model's stored capability tags from a resolver. Returns
    /// <see langword="true"/> when the persisted value changed.
    /// </summary>
    public bool RefreshCapabilityTags(
        ModelState model,
        IModelCapabilityResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(resolver);

        var tags = resolver.Resolve(model.Name);
        var tagsRaw = SerializeCapabilityTags(tags);
        if (model.CapabilityTagsRaw == tagsRaw)
            return false;

        model.CapabilityTagsRaw = tagsRaw;
        return true;
    }

    /// <summary>
    /// Builds model entities for provider model ids that do not already exist
    /// in the supplied provider-owned name set.
    /// </summary>
    public IReadOnlyList<ModelState> BuildMissingModels(
        Guid providerId,
        IEnumerable<string> modelIds,
        ISet<string> existingNames,
        IModelCapabilityResolver capabilities)
    {
        ArgumentNullException.ThrowIfNull(modelIds);
        ArgumentNullException.ThrowIfNull(existingNames);
        ArgumentNullException.ThrowIfNull(capabilities);

        return modelIds
            .Where(id => !existingNames.Contains(id))
            .Select(id => new ModelState
            {
                Name = id,
                ProviderId = providerId,
                CapabilityTagsRaw = SerializeCapabilityTags(
                    capabilities.Resolve(id))
            })
            .ToList();
    }

    /// <summary>Projects a loaded model entity into its public response.</summary>
    public ModelResponse ToResponse(ModelState model, ProviderState? provider = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        provider ??= model.Provider;

        return new ModelResponse(
            model.Id,
            model.Name,
            model.ProviderId,
            provider.Name,
            model.CustomId,
            model.CapabilityTags);
    }

    /// <summary>Returns the EF-translatable model response projection.</summary>
    public Expression<Func<ModelState, ModelResponse>> ToResponseProjection() =>
        model => new ModelResponse(
            model.Id,
            model.Name,
            model.ProviderId,
            model.Provider.Name,
            model.CustomId,
            model.CapabilityTags);
}
