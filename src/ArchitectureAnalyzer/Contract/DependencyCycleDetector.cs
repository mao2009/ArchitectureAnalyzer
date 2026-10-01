using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace ArchitectureAnalyzer.Contract;

/// <summary>
/// Finds deterministic representative cycles in the explicit positive dependency-policy graph.
/// </summary>
internal static class DependencyCycleDetector
{
    /// <summary>
    /// Returns one canonical cycle path for each cyclic strongly connected component.
    /// Paths repeat their starting layer at the end (for example A -> B -> A).
    /// </summary>
    internal static ImmutableArray<ImmutableArray<string>> FindCycles(ArchitectureContract contract)
    {
        if (contract.DependencyGraph?.RequireAcyclic != true || contract.AllowedDependencies.IsEmpty)
        {
            return ImmutableArray<ImmutableArray<string>>.Empty;
        }

        var adjacency = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal);
        foreach (var layer in contract.Layers)
        {
            adjacency[layer.Name] = ImmutableArray<string>.Empty;
        }

        foreach (var rule in contract.AllowedDependencies)
        {
            adjacency[rule.From] = rule.Targets
                .OrderBy(static target => target, StringComparer.Ordinal)
                .ToImmutableArray();
        }

        var indexByNode = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinkByNode = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var components = new List<List<string>>();
        var nextIndex = 0;

        foreach (var node in adjacency.Keys.OrderBy(static node => node, StringComparer.Ordinal))
        {
            if (!indexByNode.ContainsKey(node))
            {
                StrongConnect(
                    node,
                    adjacency,
                    indexByNode,
                    lowLinkByNode,
                    stack,
                    onStack,
                    components,
                    ref nextIndex);
            }
        }

        var cyclicComponents = components
            .Where(component => IsCyclic(component, adjacency))
            .Select(component =>
            {
                component.Sort(StringComparer.Ordinal);
                return component;
            })
            .OrderBy(static component => component[0], StringComparer.Ordinal)
            .ToArray();

        var cycles = ImmutableArray.CreateBuilder<ImmutableArray<string>>(cyclicComponents.Length);
        foreach (var component in cyclicComponents)
        {
            cycles.Add(FindCanonicalCycle(component, adjacency));
        }

        return cycles.ToImmutable();
    }

    private static void StrongConnect(
        string node,
        IReadOnlyDictionary<string, ImmutableArray<string>> adjacency,
        IDictionary<string, int> indexByNode,
        IDictionary<string, int> lowLinkByNode,
        Stack<string> stack,
        HashSet<string> onStack,
        ICollection<List<string>> components,
        ref int nextIndex)
    {
        indexByNode[node] = nextIndex;
        lowLinkByNode[node] = nextIndex;
        nextIndex++;

        stack.Push(node);
        onStack.Add(node);

        foreach (var target in adjacency[node])
        {
            if (!indexByNode.ContainsKey(target))
            {
                StrongConnect(
                    target,
                    adjacency,
                    indexByNode,
                    lowLinkByNode,
                    stack,
                    onStack,
                    components,
                    ref nextIndex);
                lowLinkByNode[node] = Math.Min(lowLinkByNode[node], lowLinkByNode[target]);
            }
            else if (onStack.Contains(target))
            {
                lowLinkByNode[node] = Math.Min(lowLinkByNode[node], indexByNode[target]);
            }
        }

        if (lowLinkByNode[node] != indexByNode[node])
        {
            return;
        }

        var component = new List<string>();
        string current;
        do
        {
            current = stack.Pop();
            onStack.Remove(current);
            component.Add(current);
        }
        while (!string.Equals(current, node, StringComparison.Ordinal));

        components.Add(component);
    }

    private static bool IsCyclic(
        IReadOnlyCollection<string> component,
        IReadOnlyDictionary<string, ImmutableArray<string>> adjacency)
    {
        if (component.Count > 1)
        {
            return true;
        }

        var node = component.First();
        return adjacency[node].Any(target => string.Equals(target, node, StringComparison.Ordinal));
    }

    private static ImmutableArray<string> FindCanonicalCycle(
        IReadOnlyList<string> component,
        IReadOnlyDictionary<string, ImmutableArray<string>> adjacency)
    {
        var start = component[0];
        var componentSet = new HashSet<string>(component, StringComparer.Ordinal);
        var path = new List<string> { start };
        var visiting = new HashSet<string>(StringComparer.Ordinal) { start };

        if (!FindPathBackToStart(start, start, componentSet, adjacency, path, visiting))
        {
            throw new InvalidOperationException(
                $"Cyclic strongly connected component containing '{start}' did not yield a cycle path.");
        }

        return path.ToImmutableArray();
    }

    private static bool FindPathBackToStart(
        string current,
        string start,
        HashSet<string> component,
        IReadOnlyDictionary<string, ImmutableArray<string>> adjacency,
        List<string> path,
        HashSet<string> visiting)
    {
        foreach (var target in adjacency[current])
        {
            if (!component.Contains(target))
            {
                continue;
            }

            if (string.Equals(target, start, StringComparison.Ordinal))
            {
                path.Add(start);
                return true;
            }

            if (!visiting.Add(target))
            {
                continue;
            }

            path.Add(target);
            if (FindPathBackToStart(target, start, component, adjacency, path, visiting))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
            visiting.Remove(target);
        }

        return false;
    }
}
