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
/// Positive dependency policy for one declared source layer. When present, cross-layer
/// dependencies from <see cref="From"/> are allowed only to <see cref="Targets"/>.
/// </summary>
public sealed class AllowedDependencyRule
{
    /// <summary>Creates a positive dependency rule.</summary>
    /// <param name="from">Source layer name.</param>
    /// <param name="targets">Declared target layers this source may reference.</param>
    /// <param name="reason">Optional rationale surfaced when an unlisted target is referenced.</param>
    public AllowedDependencyRule(string from, ImmutableArray<string> targets, string reason)
    {
        From = from;
        Targets = targets.IsDefault ? ImmutableArray<string>.Empty : targets;
        Reason = reason;
    }

    /// <summary>The source layer governed by this allowlist.</summary>
    public string From { get; }

    /// <summary>Allowed cross-layer targets, in contract order.</summary>
    public ImmutableArray<string> Targets { get; }

    /// <summary>Optional rationale surfaced when the allowlist rejects an edge.</summary>
    public string Reason { get; }

    /// <summary>Whether the given target layer is explicitly allowed.</summary>
    public bool Allows(string targetLayer)
    {
        foreach (var target in Targets)
        {
            if (string.Equals(target, targetLayer, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
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
/// A single interop-boundary rule: methods carrying a configured attribute must be declared
/// in a specific layer (PSXR006 equivalent, generically expressed).
/// </summary>
public sealed class InteropBoundaryRule
{
    /// <summary>Creates an interop-boundary rule.</summary>
    /// <param name="attribute">Fully qualified attribute type name, matched via
    /// <c>AttributeClass.OriginalDefinition.ToDisplayString()</c>.</param>
    /// <param name="allowedLayer">The single layer where methods carrying this attribute may be declared.</param>
    /// <param name="reason">Human-readable rationale surfaced in the diagnostic message.</param>
    public InteropBoundaryRule(string attribute, string allowedLayer, string reason)
    {
        Attribute = attribute;
        AllowedLayer = allowedLayer;
        Reason = reason;
    }

    /// <summary>Fully qualified attribute type name (for example
    /// <c>System.Runtime.InteropServices.DllImportAttribute</c>).</summary>
    public string Attribute { get; }

    /// <summary>The layer where methods carrying this attribute must be declared.</summary>
    public string AllowedLayer { get; }

    /// <summary>Why this boundary exists, surfaced in the diagnostic message.</summary>
    public string Reason { get; }
}

/// <summary>
/// Policy for source types that cannot be assigned to any declared architecture layer.
/// </summary>
public enum UnclassifiedCodePolicy
{
    /// <summary>Preserve the legacy behavior: unclassified types do not produce a coverage diagnostic.</summary>
    Ignore,

    /// <summary>Report AARC010 for applicable source types that resolve to no architecture layer.</summary>
    Error,
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
    private readonly ImmutableDictionary<string, InteropBoundaryRule> _interopRulesByAttribute;
    private readonly ImmutableDictionary<string, AllowedDependencyRule> _allowedDependenciesBySource;

    /// <summary>Creates a contract from already-validated sections.</summary>
    /// <param name="layers">Declared layers.</param>
    /// <param name="forbiddenDependencies">Forbidden dependency edges.</param>
    /// <param name="forbiddenApis">Forbidden API rules.</param>
    /// <param name="layerDeclaration">Optional declaration-rules section; <see langword="null"/> keeps namespace-only behaviour.</param>
    /// <param name="interopBoundaryRules">Interop-boundary rules; empty when the section is absent.</param>
    /// <param name="unclassifiedCode">Coverage policy for types that resolve to no declared layer.</param>
    /// <param name="allowedDependencies">Optional positive dependency rules, empty when absent.</param>
    public ArchitectureContract(
        ImmutableArray<LayerDefinition> layers,
        ImmutableArray<ForbiddenDependencyRule> forbiddenDependencies,
        ImmutableArray<ForbiddenApiRule> forbiddenApis,
        LayerDeclaration? layerDeclaration = null,
        ImmutableArray<InteropBoundaryRule>? interopBoundaryRules = null,
        UnclassifiedCodePolicy unclassifiedCode = UnclassifiedCodePolicy.Ignore,
        ImmutableArray<AllowedDependencyRule>? allowedDependencies = null)
    {
        Layers = layers.IsDefault ? ImmutableArray<LayerDefinition>.Empty : layers;
        ForbiddenDependencies = forbiddenDependencies.IsDefault
            ? ImmutableArray<ForbiddenDependencyRule>.Empty
            : forbiddenDependencies;
        ForbiddenApis = forbiddenApis.IsDefault ? ImmutableArray<ForbiddenApiRule>.Empty : forbiddenApis;
        LayerDeclaration = layerDeclaration;
        UnclassifiedCode = unclassifiedCode;
        AllowedDependencies = allowedDependencies ?? ImmutableArray<AllowedDependencyRule>.Empty;
        if (AllowedDependencies.IsDefault)
        {
            AllowedDependencies = ImmutableArray<AllowedDependencyRule>.Empty;
        }

        _allowedDependenciesBySource = AllowedDependencies.IsEmpty
            ? ImmutableDictionary<string, AllowedDependencyRule>.Empty
            : AllowedDependencies.ToImmutableDictionary(
                rule => rule.From,
                rule => rule,
                StringComparer.Ordinal);

        _markerLayerByFqn = layerDeclaration?.MarkerAttributes.ToImmutableDictionary(
            mapping => mapping.AttributeFqn,
            mapping => mapping.Layer,
            StringComparer.Ordinal) ?? ImmutableDictionary<string, string>.Empty;

        InteropBoundaryRules = interopBoundaryRules ?? ImmutableArray<InteropBoundaryRule>.Empty;
        if (InteropBoundaryRules.IsDefault)
        {
            InteropBoundaryRules = ImmutableArray<InteropBoundaryRule>.Empty;
        }

        _interopRulesByAttribute = InteropBoundaryRules.IsEmpty
            ? ImmutableDictionary<string, InteropBoundaryRule>.Empty
            : InteropBoundaryRules.ToImmutableDictionary(
                rule => rule.Attribute,
                rule => rule,
                StringComparer.Ordinal);

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

    /// <summary>Positive dependency allowlists, in contract order.</summary>
    public ImmutableArray<AllowedDependencyRule> AllowedDependencies { get; }

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

    /// <summary>Coverage policy for source types that resolve to no declared layer.</summary>
    public UnclassifiedCodePolicy UnclassifiedCode { get; }

    /// <summary>Declared interop-boundary rules, in contract order.</summary>
    public ImmutableArray<InteropBoundaryRule> InteropBoundaryRules { get; }

    /// <summary>
    /// Looks up the interop-boundary rule configured for an attribute type name.
    /// </summary>
    /// <param name="attributeFullName">Fully qualified attribute type name.</param>
    /// <returns>The matching rule, or <see langword="null"/> when none is configured.</returns>
    public InteropBoundaryRule? ResolveInteropRule(string attributeFullName)
    {
        return _interopRulesByAttribute.TryGetValue(attributeFullName, out var rule) ? rule : null;
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
    /// Determines whether a cross-layer dependency is disallowed by either the explicit denylist
    /// or a positive allowlist for the source layer. Explicit forbidden edges take precedence so
    /// their more specific rationale is preserved.
    /// </summary>
    /// <param name="fromLayer">Resolved source layer.</param>
    /// <param name="toLayer">Resolved target layer.</param>
    /// <param name="reason">Receives the diagnostic rationale when disallowed.</param>
    /// <returns><see langword="true"/> when the dependency must produce AARC002.</returns>
    public bool IsDependencyDisallowed(string fromLayer, string toLayer, out string reason)
    {
        if (IsForbiddenDependency(fromLayer, toLayer, out reason))
        {
            return true;
        }

        if (_allowedDependenciesBySource.TryGetValue(fromLayer, out var allowlist)
            && !allowlist.Allows(toLayer))
        {
            reason = string.IsNullOrWhiteSpace(allowlist.Reason)
                ? $"target layer '{toLayer}' is not listed in allowedDependencies for '{fromLayer}'"
                : allowlist.Reason;
            return true;
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
