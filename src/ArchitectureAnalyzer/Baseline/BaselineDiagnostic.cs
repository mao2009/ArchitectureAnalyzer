using System.Collections.Immutable;
using ArchitectureAnalyzer.Diagnostics;
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

    internal static Diagnostic? CreateCapture(
        ArchitectureBaseline baseline,
        DiagnosticDescriptor descriptor,
        Location location,
        string key)
    {
        if (!baseline.CaptureMode)
        {
            return null;
        }

        return Diagnostic.Create(
            ArchitectureDiagnostics.ArchitectureBaselineCapture,
            location,
            descriptor.Id,
            key);
    }
}
