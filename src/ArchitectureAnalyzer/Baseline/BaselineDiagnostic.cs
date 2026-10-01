using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace ArchitectureAnalyzer.Baseline;

/// <summary>Creates baselinable diagnostics with a stable SARIF-visible identity.</summary>
internal static class BaselineDiagnostic
{
    internal static Diagnostic? Create(
        ArchitectureBaseline baseline,
        DiagnosticDescriptor descriptor,
        Location location,
        string key,
        params object[] messageArgs)
    {
        if (baseline.Contains(descriptor.Id, key))
        {
            return null;
        }

        var properties = ImmutableDictionary<string, string?>.Empty
            .Add(ArchitectureBaseline.BaselineKeyProperty, key);

        return Diagnostic.Create(descriptor, location, properties, messageArgs);
    }
}
