using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ArchitectureAnalyzer.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ArchitectureAnalyzer.Configuration;

/// <summary>
/// Reads operational configuration from Roslyn's AnalyzerConfigOptionsProvider.
/// </summary>
/// <remarks>
/// Property keys use the pattern <c>dotnet_diagnostic.{ID}.architecture_analyzer.{property}</c>
/// so that they inherit .editorconfig file-scope semantics automatically, matching the naming
/// convention finalized in docs/configuration-design.md §3.2.
/// </remarks>
public static class ConfigReader
{
    private const string ArchitectureAnalyzerPrefix = "architecture_analyzer.";

    /// <summary>The synthetic layer name used for unclassified namespaces when
    /// <c>require_layer_declaration</c> is <see langword="false"/>.</summary>
    public const string UnclassifiedLayerName = "Unclassified";

    /// <summary>Compilation-wide: whether a missing/invalid contract is an error (AARC001).</summary>
    public const string ContractRequiredKey = "dotnet_diagnostic.AARC001." + ArchitectureAnalyzerPrefix + "contract_required";

    /// <summary>Per-tree: whether unclassified namespaces participate in layer analysis.</summary>
    public const string RequireLayerDeclarationKey = "dotnet_diagnostic.AARC002." + ArchitectureAnalyzerPrefix + "require_layer_declaration";

    /// <summary>Per-tree: master switch that fully disables analysis for a tree.</summary>
    public const string EnabledKey = "dotnet_diagnostic.AARC002." + ArchitectureAnalyzerPrefix + "enabled";

    /// <summary>Per-tree: namespace/layer consistency verification (seam for #32 / AARC006).</summary>
    public const string ValidateNamespaceLayerKey = "dotnet_diagnostic.AARC002." + ArchitectureAnalyzerPrefix + "validate_namespace_layer";

    /// <summary>Per-tree: generated-code operational option (enum-like).</summary>
    public const string GeneratedCodeKey = "dotnet_diagnostic.AARC002." + ArchitectureAnalyzerPrefix + "generated_code";

    /// <summary>Per-tree: generated-code skip (bool form, finalized in #29).</summary>
    public const string SkipGeneratedCodeAarc002Key = "dotnet_diagnostic.AARC002." + ArchitectureAnalyzerPrefix + "skip_generated_code";

    /// <summary>Per-tree: generated-code skip mirror for AARC003.</summary>
    public const string SkipGeneratedCodeAarc003Key = "dotnet_diagnostic.AARC003." + ArchitectureAnalyzerPrefix + "skip_generated_code";

    /// <summary>Prefix of the per-rule toggle keys: <c>...architecture_analyzer.rule.&lt;ID&gt;.enabled</c>.</summary>
    private const string RuleTogglePrefix = "dotnet_diagnostic.AARC002." + ArchitectureAnalyzerPrefix + "rule.";

    /// <summary>
    /// Reads operational configuration for a specific syntax tree. Per-tree keys — and, as a
    /// fallback, compilation-wide <c>.globalconfig</c> values — are resolved through
    /// <see cref="AnalyzerConfigOptionsProvider.GetOptions"/>.
    /// </summary>
    /// <param name="provider">The options provider from the analysis context.</param>
    /// <param name="tree">The syntax tree whose options should be read.</param>
    /// <param name="diagnostics">Collects invalid-value diagnostics, reported at compilation end.</param>
    /// <returns>The resolved operational configuration.</returns>
    public static OperationalConfig Read(
        AnalyzerConfigOptionsProvider provider,
        SyntaxTree tree,
        ConcurrentDictionary<string, (DiagnosticDescriptor Descriptor, Location Location, string Key, string Value)>? diagnostics)
    {
        var options = provider.GetOptions(tree);

        var enabled = ReadBoolOption(options, EnabledKey, defaultValue: true, diagnostics);
        if (!enabled)
        {
            return OperationalConfig.Disabled;
        }

        var requireLayerDeclaration = ReadBoolOption(
            options, RequireLayerDeclarationKey, defaultValue: true, diagnostics);
        var skipGeneratedCodeBool2 = ReadBoolOption(
            options, SkipGeneratedCodeAarc002Key, defaultValue: true, diagnostics);
        var skipGeneratedCodeBool3 = ReadBoolOption(
            options, SkipGeneratedCodeAarc003Key, defaultValue: true, diagnostics);
        var validateNamespaceLayer = ReadBoolOption(
            options, ValidateNamespaceLayerKey, defaultValue: false, diagnostics);
        var contractRequired = ReadBoolOption(
            options, ContractRequiredKey, defaultValue: true, diagnostics);

        var generatedCode = ReadEnumOption(
            options, GeneratedCodeKey, GeneratedCodeSetting.Exclude, diagnostics);
        var skipGeneratedCode = generatedCode switch
        {
            GeneratedCodeSetting.Include => false,
            _ => skipGeneratedCodeBool2 && skipGeneratedCodeBool3,
        };

        var aarc002Enabled = ReadBoolOption(options, RuleEnabledKey("AARC002"), defaultValue: true, diagnostics);
        var aarc003Enabled = ReadBoolOption(options, RuleEnabledKey("AARC003"), defaultValue: true, diagnostics);

        return OperationalConfig.Create(
            enabled: true,
            requireLayerDeclaration,
            skipGeneratedCode,
            validateNamespaceLayer,
            contractRequired,
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["AARC002"] = aarc002Enabled,
                ["AARC003"] = aarc003Enabled,
            });
    }

    /// <summary>
    /// Reads a bool property from the given options, reporting a warning when the raw value is
    /// present but not parseable. Falls back to <paramref name="defaultValue"/> in every
    /// failure case.
    /// </summary>
    internal static bool ReadBoolOption(
        AnalyzerConfigOptions options,
        string key,
        bool defaultValue,
        ConcurrentDictionary<string, (DiagnosticDescriptor Descriptor, Location Location, string Key, string Value)>? diagnostics)
    {
        if (!options.TryGetValue(key, out var rawValue))
        {
            return defaultValue;
        }

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return defaultValue;
        }

        if (bool.TryParse(rawValue, out var result))
        {
            return result;
        }

        ReportInvalidValue(key, rawValue!, defaultValue, diagnostics);
        return defaultValue;
    }

    /// <summary>
    /// Reads an enum-like property, reporting a warning on unparseable values.
    /// </summary>
    internal static TEnum ReadEnumOption<TEnum>(
        AnalyzerConfigOptions options,
        string key,
        TEnum defaultValue,
        ConcurrentDictionary<string, (DiagnosticDescriptor Descriptor, Location Location, string Key, string Value)>? diagnostics)
        where TEnum : struct
    {
        if (!options.TryGetValue(key, out var rawValue))
        {
            return defaultValue;
        }

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return defaultValue;
        }

        if (Enum.TryParse(rawValue, ignoreCase: true, out TEnum result))
        {
            return result;
        }

        ReportInvalidValue(key, rawValue!, defaultValue, diagnostics);
        return defaultValue;
    }

    /// <summary>Builds the per-rule point toggle key: <c>...rule.{ruleId}.enabled</c>.</summary>
    public static string RuleEnabledKey(string ruleId)
    {
        return RuleTogglePrefix + ruleId + ".enabled";
    }

    private static void ReportInvalidValue<TValue>(
        string key,
        string rawValue,
        TValue fallback,
        ConcurrentDictionary<string, (DiagnosticDescriptor Descriptor, Location Location, string Key, string Value)>? diagnostics)
    {
        // Config is re-read for many syntax nodes; report each invalid property once per
        // compilation by deduplicating on the property key.
        diagnostics?.TryAdd(
            key,
            (
                ArchitectureDiagnostics.InvalidConfigurationValue,
                Location.None,
                key,
                rawValue));
    }

    /// <summary>Generated-code handling values for <c>architecture_analyzer.generated_code</c>.</summary>
    public enum GeneratedCodeSetting
    {
        /// <summary>Skip files matching generated-code path patterns (default, matches v0.1).</summary>
        Exclude,

        /// <summary>Analyze generated code as well.</summary>
        Include,
    }
}