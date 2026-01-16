using System;
using System.Collections.Generic;
using DeviceSpace.Common.Contracts;

using Microsoft.Extensions.Logging;

namespace DeviceSpace.Core;

/// <summary>
/// A registry that discovers and holds all available IMessageTransformer plugins.
/// </summary>
public class AdapterRegistry
{
    private readonly ILogger<AdapterRegistry> _logger;
    private readonly Dictionary<(Type, Type), IMessageAdapter> _transformers = new();

    public AdapterRegistry(ILogger<AdapterRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers a new transformer, called by the PluginManager when a DLL is loaded.
    /// </summary>
    public void Register(IMessageAdapter adapter)
    {
        var key = (adapter.SourceType, adapter.TargetType);
        _transformers[key] = adapter;
        _logger.LogInformation("[Registry] Registered Transformer: {TransformerName} for {SourceType} -> {TargetType}", 
            adapter.GetType().Name,
            key.SourceType.Name,
            key.TargetType.Name);
    }

    /// <summary>
    /// Finds a transformer for a specific conversion.
    /// </summary>
    public IMessageAdapter? FindTransformer(Type sourceType, Type targetType)
    {
        _transformers.TryGetValue((sourceType, targetType), out var transformer);
        return transformer; // Returns null if not found
    }
}