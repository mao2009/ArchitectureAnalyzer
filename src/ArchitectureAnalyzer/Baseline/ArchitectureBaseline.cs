using System;
using System.Collections.Immutable;

namespace ArchitectureAnalyzer.Baseline;

/// <summary>
/// Immutable set of known architecture-policy diagnostics tolerated by ratcheting.
/// </summary>
public sealed class ArchitectureBaseline
{
    /// <summary>Conventional AdditionalFiles basename for the baseline.</summary>
    public const string FileName = "architecture.baseline.json";

    /// <summary>Diagnostic property written to SARIF so the generator can reconstruct entries.</summary>
    public const string BaselineKeyProperty = "architectureBaselineKey";

    private readonly ImmutableHashSet<(string DiagnosticId, string Key)> _entries;

    internal ArchitectureBaseline(
        ImmutableHashSet<(string DiagnosticId, string Key)> entries,
        bool captureMode = false)
    {
        _entries = entries;
        CaptureMode = captureMode;
    }

    /// <summary>An empty baseline that suppresses nothing.</summary>
    public static ArchitectureBaseline Empty { get; } = new(
        ImmutableHashSet<(string DiagnosticId, string Key)>.Empty);

    /// <summary>
    /// Empty matching plus capture records, used only by the baseline generator.
    /// </summary>
    public static ArchitectureBaseline Capturing { get; } = new(
        ImmutableHashSet<(string DiagnosticId, string Key)>.Empty,
        captureMode: true);

    /// <summary>Whether capture-only AARC013 records should accompany policy diagnostics.</summary>
    public bool CaptureMode { get; }

    /// <summary>Whether the exact diagnostic identity is already accepted legacy debt.</summary>
    public bool Contains(string diagnosticId, string key)
    {
        return _entries.Contains((diagnosticId, key));
    }

    /// <summary>
    /// Architecture-policy diagnostics eligible for ratcheting. Integrity/configuration
    /// diagnostics are deliberately excluded.
    /// </summary>
    public static bool IsBaselinableDiagnosticId(string diagnosticId)
    {
        return diagnosticId is
            "AARC002"
            or "AARC003"
            or "AARC004"
            or "AARC005"
            or "AARC006"
            or "AARC007"
            or "AARC010"
            or "AARC011";
    }
}
