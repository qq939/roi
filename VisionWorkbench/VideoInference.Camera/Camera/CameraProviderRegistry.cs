using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoInferenceDemo;

public sealed record CameraProviderRegistration(string ProviderId, string DisplayName, Func<ICameraProvider> Factory);

public sealed class CameraProviderRegistry
{
    private readonly IReadOnlyDictionary<string, Lazy<ICameraProvider>> _providers;
    private readonly IReadOnlyDictionary<string, string> _displayNames;
    private readonly IReadOnlyList<CameraProviderDescriptor> _descriptors;

    public CameraProviderRegistry(IEnumerable<ICameraProvider> providers)
        : this(providers.Select(provider => new CameraProviderRegistration(
            provider.ProviderId,
            provider.DisplayName,
            () => provider)))
    {
    }

    public CameraProviderRegistry(IEnumerable<CameraProviderRegistration> providers)
    {
        var providerMap = new Dictionary<string, Lazy<ICameraProvider>>(StringComparer.OrdinalIgnoreCase);
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var descriptors = new List<CameraProviderDescriptor>();

        foreach (var provider in providers)
        {
            providerMap[provider.ProviderId] = new Lazy<ICameraProvider>(provider.Factory);
            displayNames[provider.ProviderId] = provider.DisplayName;
            descriptors.Add(new CameraProviderDescriptor(provider.ProviderId, provider.DisplayName));
        }

        _providers = providerMap;
        _displayNames = displayNames;
        _descriptors = descriptors;
    }

    public IReadOnlyList<CameraProviderDescriptor> DescribeProviders() => _descriptors;

    public string GetDisplayName(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            providerId = CameraProviderIds.OpenCv;
        }

        return _displayNames.TryGetValue(providerId, out var displayName)
            ? displayName
            : providerId.Trim();
    }

    public IReadOnlyList<CameraDeviceInfo> EnumerateDevices(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            providerId = CameraProviderIds.OpenCv;
        }

        return GetRequired(providerId).EnumerateDevices();
    }

    public ICameraSession Open(CameraOpenOptions options)
    {
        var normalized = options.Normalize();
        return GetRequired(normalized.ProviderId).Open(normalized);
    }

    private ICameraProvider GetRequired(string providerId)
    {
        if (_providers.TryGetValue(providerId, out var provider))
        {
            return provider.Value;
        }

        var supported = string.Join(", ", _descriptors.Select(item => item.ProviderId));
        throw new InvalidOperationException($"Unknown camera provider '{providerId}'. Supported providers: {supported}.");
    }
}
