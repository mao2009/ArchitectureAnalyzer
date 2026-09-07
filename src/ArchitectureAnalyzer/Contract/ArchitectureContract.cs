using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace ArchitectureAnalyzer.Contract;

/// <summary>
/// One declared architecture layer and the namespace prefixes that map onto it.
/// </summary>
public sealed class LayerDefinition
{
    /// <summary>Creates a layer definition.</summary>
    /// <param name="name">Layer name, referenced by dependency and API rules.</param>
    /// <param name="namespaceRoots">Namespace prefixes owned by this layer.</param>
    public LayerDefinition(string name, ImmutableArray<string> namespaceRoots)
    {
        Name = name;
        NamespaceRoots = namespaceRoots.IsDefault ? ImmutableArray<string>.Empty : namespaceRoots;
    }

    /// <summary>The layer name, matched case-sensitively by every other contract section.</summary>
    public string Name { get; }

    /// <summary>Namespace prefixes claimed by this layer.</summary>
    public ImmutableArray<string> NamespaceRoots { get; }
}

/// <summary>
/// A forbidden dependency edge between two declared layers.
/// </summary>
public sealed class ForbiddenDependencyRule
{
    /// <summary>Creates a forbidden dependency edge.</summary>
    /// <param name="from">Source layer name.</param>
    /// <param name="to">Target layer name.</param>
    /// <param name="reason">Human-readable rationale surfaced in the diagnostic message.</param>
    public ForbiddenDependencyRule(string from, string to, string reason)
    {
        From = from;
        To = to;
        Reason = reason;
    }

    /// <summary>The depending (source) layer.</summary>
    public string From { get; }

    /// <summary>The depended-upon (target) layer.</summary>
    public string To { get; }

    /// <summary>Why this edge is forbidden.</summary>
    public string Reason { get; }
}

/// <summary>
/// A single forbidden-API rule scoped to one declared layer.
/// </summary>
public sealed class ForbiddenApiRule
{
    /// <summary>Creates a forbidden-API rule.</summary>
    /// <param name="layer">The layer the rule applies to.</param>
    /// <param name="typeFullName">Fully qualified declaring type name.</param>
    /// <param name="memberName">Optional single member name; <see langword="null"/> means any member.</param>
    /// <param name="wholeType">When <see langword="true"/>, constructors are forbidden too.</param>
    /// <param name="reason">Human-readable rationale surfaced in the diagnostic message.</param>
    public ForbiddenApiRule(string layer, string typeFullName, string? memberName, bool wholeType, string reason)
    {
        Layer = layer;
        TypeFullName = typeFullName;
        MemberName = memberName;
        WholeType = wholeType;
        Reason = reason;
    }

    /// <summary>The layer this rule constrains.</summary>
    public string Layer { get; }

    /// <summary>Fully qualified name of the declaring type.</summary>
    public string TypeFullName { get; }

    /// <summary>Restricts the rule to a single member when set.</summary>
    public string? MemberName { get; }

    /// <summary>Whether the type's own constructors are forbidden as well.</summary>
    public bool WholeType { get; }

    /// <summary>Why this API is forbidden in <see cref="Layer"/>.</summary>
    public string Reason { get; }

    /// <summary>
    /// Determines whether a resolved symbol reference matches this rule.
    /// </summary>
    /// <param name="typeFullName">The referenced member's declaring type, fully qualified.</param>
    /// <param name="memberName">The referenced member's name (property name for accessors).</param>
    /// <param name="isConstructor">Whether the reference is a constructor invocation.</param>
    /// <returns><see langword="true"/> when the reference is forbidden by this rule.</returns>
    public bool Matches(string typeFullName, string memberName, bool isConstructor)
    {
        if (!string.Equals(TypeFullName, typeFullName, StringComparison.Ordinal))
        {
            return false;
        }

        if (isConstructor)
        {
            return WholeType || MemberName is null;
        }

        return WholeType
            || MemberName is null
            || string.Equals(MemberName, memberName, StringComparison.Ordinal);
    }
}

/// <summary>
/// Maps a marker attribute's fully qualified type name to a declared layer.
/// </summary>
public sealed class MarkerAttributeDefinition
{
    /// <summary>Creates a marker attribute mapping.</summary>
    /// <param name="attributeFqn">Fully qualified name of the marker attribute type.</param>
    /// <param name="layer">The declared layer the attribute classifies a type into.</param>
    public MarkerAttributeDefinition(string attributeFqn, string layer)
    {
        AttributeFqn = attributeFqn;
        Layer = layer;
    }

    /// <summary>Fully qualified name of the marker attribute type (ordinal match).</summary>
    public string AttributeFqn { get; }

    /// <summary>The declared layer the attribute maps to.</summary>
    public string Layer { get; }
}

/// <summary>
/// Optional declaration-rules controlling AARC004/AARC005/AARC006. When <see langword="null"/>
/// the analyzer keeps its namespace-only behaviour (backward compatible).
/// </summary>
public sealed class LayerDeclaration
{
    /// <summary>Creates a layer-declaration ruleset.</summary>
    /// <param name="required">When <see langword="true"/>, every non-exempt class must carry a recognized marker attribute (AARC004).</param>
    /// <param name="markerAttributes">Marker attributes mapping attribute FQNs to declared layers.</param>
    /// <param name="validateNamespaceConsistency">When <see langword="true"/>, attribute-vs-namespace mismatch is reported (AARC006).</param>
    /// <param name="markerNamespace">Optional namespace whose types are exempt (the attribute definitions themselves).</param>
    public LayerDeclaration(
        bool required,
        ImmutableArray<MarkerAttributeDefinition> markerAttributes,
        bool validateNamespaceConsistency,
        string? markerNamespace)
    {
        Required = required;
        MarkerAttributes = markerAttributes.IsDefault
            ? ImmutableArray<MarkerAttributeDefinition>.Empty
            : markerAttributes;
        ValidateNamespaceConsistency = validateNamespaceConsistency;
        MarkerNamespace = string.IsNullOrWhiteSpace(markerNamespace) ? null : markerNamespace;
    }

    /// <summary>Whether every non-exempt class must declare a layer via a marker attribute.</summary>
    public bool Required { get; }

    /// <summary>Marker attributes recognized by this contract.</summary>
    public ImmutableArray<MarkerAttributeDefinition> MarkerAttributes { get; }

    /// <summary>Whether attribute-vs-namespace inconsistency is reported (AARC006).</summary>
    public bool ValidateNamespaceConsistency { get; }

    /// <summary>Namespace exempt from AARC004 (typically the marker attribute definitions).</summary>
    public string? MarkerNamespace { get; }
}

/// <summary>
/// An immutable, validated Architecture Contract: the single source of truth the analyzer
/// interprets. The analyzer itself holds no architecture knowledge of its own.
/// </summary>
public sealed class ArchitectureContract
{
    private readonly ImmutableArray<KeyValuePair<string, string>> _namespaceRootsLongestFirst;
    private readonly ImmutableDictionary<string, ImmutableArray<ForbiddenApiRule>> _apiRulesByLayer;
    private readonly ImmutableDictionary<string, string> _markerLayerByFqn;

    /// <summary>Creates a contract from already-validated sections.</summary>
    /// <param name="layers">Declared layers.</param>
    /// <param name="forbiddenDependencies">Forbidden dependency edges.</param>
    /// <param name="forbiddenApis">Forbidden API rules.</param>
    /// <param name="layerDeclaration">Optional declaration-rules section; <see langword="null"/> keeps namespace-only behaviour.</param>
    public ArchitectureContract(
        ImmutableArray<LayerDefinition> layers,
        ImmutableArray<ForbiddenDependencyRule> forbiddenDependencies,
        ImmutableArray<ForbiddenApiRule> forbiddenApis,
        LayerDeclaration? layerDeclaration = null)
    {
        Layers = layers.IsDefault ? ImmutableArray<LayerDefinition>.Empty : layers;
        ForbiddenDependencies = forbiddenDependencies.IsDefault
            ? ImmutableArray<ForbiddenDependencyRule>.Empty
            : forbiddenDependencies;
        ForbiddenApis = forbiddenApis.IsDefault ? ImmutableArray<ForbiddenApiRule>.Empty : forbiddenApis;
        LayerDeclaration = layerDeclaration;

        _markerLayerByFqn = layerDeclaration?.MarkerAttributes.ToImmutableDictionary(
            mapping => mapping.AttributeFqn,
            mapping => mapping.Layer,
            StringComparer.Ordinal) ?? ImmutableDictionary<string, string>.Empty;

        // Longest root first so that a more specific prefix wins over a shorter one declared by
        // another layer (see docs/architecture.md, "Namespace classification").
        _namespaceRootsLongestFirst = Layers
            .SelectMany(layer => layer.NamespaceRoots.Select(root => new KeyValuePair<string, string>(root, layer.Name)))
            .OrderByDescending(entry => entry.Key.Length)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .ToImmutableArray();

        _apiRulesByLayer = ForbiddenApis
            .GroupBy(rule => rule.Layer, StringComparer.Ordinal)
            .ToImmutableDictionary(
                group => group.Key,
                group => group.ToImmutableArray(),
                StringComparer.Ordinal);
    }

    /// <summary>Declared layers, in contract order.</summary>
    public ImmutableArray<LayerDefinition> Layers { get; }

    /// <summary>Declared forbidden dependency edges, in contract order.</summary>
    public ImmutableArray<ForbiddenDependencyRule> ForbiddenDependencies { get; }

    /// <summary>Declared forbidden API rules, in contract order.</summary>
    public ImmutableArray<ForbiddenApiRule> ForbiddenApis { get; }

    /// <summary>Optional layer-declaration ruleset, or <see langword="null"/> for namespace-only.</summary>
    public LayerDeclaration? LayerDeclaration { get; }

    /// <summary>
    /// Resolves the declared layer a marker attribute FQN maps to.
    /// </summary>
    /// <param name="attributeFqn">Fully qualified marker attribute type name.</param>
    /// <returns>The mapped layer, or <see langword="null"/> when the FQN is not a recognized marker.</returns>
    public string? ResolveMarkerLayer(string attributeFqn)
    {
        return _markerLayerByFqn.TryGetValue(attributeFqn, out var layer) ? layer : null;
    }

    /// <summary>
    /// Resolves the layer that owns a namespace, using longest-matching-prefix.
    /// </summary>
    /// <param name="namespaceName">A dotted namespace name, or the empty string for global.</param>
    /// <returns>The owning layer name, or <see langword="null"/> when unclassified.</returns>
    public string? ResolveLayer(string? namespaceName)
    {
        if (string.IsNullOrEmpty(namespaceName))
        {
            return null;
        }

        foreach (var entry in _namespaceRootsLongestFirst)
        {
            if (IsWithin(namespaceName!, entry.Key))
            {
                return entry.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up a forbidden dependency edge.
    /// </summary>
    /// <param name="fromLayer">Source layer name.</param>
    /// <param name="toLayer">Target layer name.</param>
    /// <param name="reason">Receives the contract-supplied rationale when forbidden.</param>
    /// <returns><see langword="true"/> when the edge is forbidden.</returns>
    public bool IsForbiddenDependency(string fromLayer, string toLayer, out string reason)
    {
        foreach (var rule in ForbiddenDependencies)
        {
            if (string.Equals(rule.From, fromLayer, StringComparison.Ordinal)
                && string.Equals(rule.To, toLayer, StringComparison.Ordinal))
            {
                reason = rule.Reason;
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns the forbidden-API rules that apply to a layer.
    /// </summary>
    /// <param name="layer">Layer name.</param>
    /// <returns>The matching rules, or an empty array.</returns>
    public ImmutableArray<ForbiddenApiRule> GetApiRules(string layer)
    {
        return _apiRulesByLayer.TryGetValue(layer, out var rules) ? rules : ImmutableArray<ForbiddenApiRule>.Empty;
    }

    private static bool IsWithin(string namespaceName, string root)
    {
        if (root.Length == 0)
        {
            return false;
        }

        if (!namespaceName.StartsWith(root, StringComparison.Ordinal))
        {
            return false;
        }

        return namespaceName.Length == root.Length || namespaceName[root.Length] == '.';
    }
}
