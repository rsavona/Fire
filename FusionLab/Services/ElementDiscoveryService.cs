using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Fusion.Common.Contracts;
using Fusion.Common.BaseClasses;

namespace FusionLab.Services;

/// <summary>
/// Service responsible for providing lists of discovered element managers and reactions
/// to the UI for selection and configuration.
/// </summary>
public class ElementDiscoveryService
{
    private readonly IEnumerable<Type> _discoveredElements;
    private readonly IEnumerable<Type> _discoveredReactions;

    public ElementDiscoveryService(
        [FromKeyedServices("ElementManagerTypes")] IEnumerable<Type> discoveredElements,
        [FromKeyedServices("ReactionTypes")] IEnumerable<Type> discoveredReactions)
    {
        _discoveredElements = discoveredElements
            .Where(t => typeof(IElementManager).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .ToList();

        _discoveredReactions = discoveredReactions
            .Where(t => typeof(ReactionBase).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();
    }

    /// <summary>
    /// Gets the list of types that implement IElementManager.
    /// </summary>
    public IEnumerable<Type> GetDiscoveredElements() => _discoveredElements;

    /// <summary>
    /// Gets the list of types that inherit from ReactionBase.
    /// </summary>
    public IEnumerable<Type> GetDiscoveredReactions() => _discoveredReactions;
}
