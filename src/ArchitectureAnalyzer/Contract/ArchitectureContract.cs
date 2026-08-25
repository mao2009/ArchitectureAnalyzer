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
/// An immutable, validated Architecture Contract: the single source of truth the analyzer
/// interprets. The analyzer itself holds no architecture knowledge of its own.
/// </summary>
public sealed class ArchitectureContract
{
    private readonly ImmutableArray<KeyValuePair<string, string>> _namespaceRootsLongestFirst;
    private readonly ImmutableDictionary<string, ImmutableArray<ForbiddenApiRule>> _apiRulesByLayer;

    /// <summary>Creates a contract from already-validated sections.</summary>
    /// <param name="layers">Declared layers.</param>
    /// <param name="forbiddenDependencies">Forbidden dependency edges.</param>
    /// <param name="forbiddenApis">Forbidden API rules.</param>
    public ArchitectureContract(
        ImmutableArray<LayerDefinition> layers,
        ImmutableArray<ForbiddenDependencyRule> forbiddenDependencies,
        ImmutableArray<ForbiddenApiRule> forbiddenApis)
    {
        Layers = layers.IsDefault ? ImmutableArray<LayerDefinition>.Empty : layers;
        ForbiddenDependencies = forbiddenDependencies.IsDefault
            ? ImmutableArray<ForbiddenDependencyRule>.Empty
            : forbiddenDependencies;
        ForbiddenApis = forbiddenApis.IsDefault ? ImmutableArray<ForbiddenApiRule>.Empty : forbiddenApis;

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
