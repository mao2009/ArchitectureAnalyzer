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
/// <para>
/// Property keys use the pattern <c>dotnet_diagnostic.{ID}.architecture_analyzer.{property}</c>
/// so that they inherit .editorconfig file-scope semantics automatically, matching the naming
/// convention finalized in docs/configuration-design.md §3.2.
/// </para>
/// <para>
/// <b>Fail-closed decision table (#31).</b> Every property below is read with two separate
/// values: the <i>default</i> applied when the property is absent, and the <i>invalid fallback</i>
/// applied when the property is present but unparseable. They differ wherever the default is the
/// permissive interpretation, so that a typo can never quietly switch enforcement off:
/// </para>
/// <list type="table">
///   <item><description>
///     <c>enabled</c> — bool — default <c>true</c> — invalid <c>true</c> — safety-critical
///   </description></item>
///   <item><description>
///     <c>rule.AARC002.enabled</c> / <c>rule.AARC003.enabled</c> — bool — default <c>true</c> —
///     invalid <c>true</c> — safety-critical
///   </description></item>
///   <item><description>
///     <c>contract_required</c> — bool — default <c>true</c> — invalid <c>true</c> — safety-critical
///   </description></item>
///   <item><description>
///     <c>require_layer_declaration</c> — bool — default <c>true</c> — invalid <c>true</c> —
///     safety-critical (<c>true</c> is the value that keeps AARC004 enforcing)
///   </description></item>
///   <item><description>
///     <c>validate_namespace_layer</c> — bool — default <c>false</c> — invalid <b><c>true</c></b> —
///     safety-critical (the default is permissive, so the invalid fallback enforces instead)
///   </description></item>
///   <item><description>
///     <c>skip_generated_code</c> (AARC002/AARC003) — bool — default <c>true</c> —
///     invalid <b><c>false</c></b> — safety-critical (skipping narrows detection coverage)
///   </description></item>
///   <item><description>
///     <c>generated_code</c> — enum <c>exclude|include</c> — default <c>exclude</c> —
///     invalid <b><c>include</c></b> — safety-critical (same reason)
///   </description></item>
/// </list>
/// <para>
/// Both configuration diagnostics (AARC008 invalid value, AARC009 unknown property) are Warnings
/// and can be silenced per project with <c>dotnet_diagnostic.AARC008.severity = none</c>; the
/// fail-closed fallback stays in force regardless of the diagnostic's severity.
/// </para>
/// </remarks>
public static class ConfigReader
{
    private const string ArchitectureAnalyzerPrefix = "architecture_analyzer.";

    /// <summary>Substring identifying a key as belonging to this analyzer.</summary>
    private const string ArchitectureAnalyzerMarker = "." + ArchitectureAnalyzerPrefix;

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

        // invalidFallback is the fail-closed value applied when a property is present but
        // unparseable; see the decision table on the class. It is deliberately NOT always the
        // same as defaultValue: where the default is permissive, a typo must still enforce.
        var enabled = ReadBoolOption(
            options, EnabledKey, defaultValue: true, invalidFallback: true, diagnostics);
        if (!enabled)
        {
            return OperationalConfig.Disabled;
        }

        var requireLayerDeclaration = ReadBoolOption(
            options, RequireLayerDeclarationKey, defaultValue: true, invalidFallback: true, diagnostics);
        var skipGeneratedCodeBool2 = ReadBoolOption(
            options, SkipGeneratedCodeAarc002Key, defaultValue: true, invalidFallback: false, diagnostics);
        var skipGeneratedCodeBool3 = ReadBoolOption(
            options, SkipGeneratedCodeAarc003Key, defaultValue: true, invalidFallback: false, diagnostics);
        var validateNamespaceLayer = ReadBoolOption(
            options, ValidateNamespaceLayerKey, defaultValue: false, invalidFallback: true, diagnostics);
        var contractRequired = ReadBoolOption(
            options, ContractRequiredKey, defaultValue: true, invalidFallback: true, diagnostics);

        var generatedCode = ReadEnumOption(
            options,
            GeneratedCodeKey,
            GeneratedCodeSetting.Exclude,
            invalidFallback: GeneratedCodeSetting.Include,
            diagnostics);
        var skipGeneratedCode = generatedCode switch
        {
            GeneratedCodeSetting.Include => false,
            _ => skipGeneratedCodeBool2 && skipGeneratedCodeBool3,
        };

        var aarc002Enabled = ReadBoolOption(
            options, RuleEnabledKey("AARC002"), defaultValue: true, invalidFallback: true, diagnostics);
        var aarc003Enabled = ReadBoolOption(
            options, RuleEnabledKey("AARC003"), defaultValue: true, invalidFallback: true, diagnostics);

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
    /// Reads a bool property from the given options. An absent or blank property yields
    /// <paramref name="defaultValue"/>; a present-but-unparseable one reports AARC008 and yields
    /// the fail-closed <paramref name="invalidFallback"/>.
    /// </summary>
    internal static bool ReadBoolOption(
        AnalyzerConfigOptions options,
        string key,
        bool defaultValue,
        bool invalidFallback,
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

        ReportInvalidValue(key, rawValue!, diagnostics);
        return invalidFallback;
    }

    /// <summary>
    /// Reads an enum-like property, reporting AARC008 and applying the fail-closed
    /// <paramref name="invalidFallback"/> on unparseable values.
    /// </summary>
    internal static TEnum ReadEnumOption<TEnum>(
        AnalyzerConfigOptions options,
        string key,
        TEnum defaultValue,
        TEnum invalidFallback,
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

        if (Enum.TryParse(rawValue, ignoreCase: true, out TEnum result) && Enum.IsDefined(typeof(TEnum), result))
        {
            return result;
        }

        ReportInvalidValue(key, rawValue!, diagnostics);
        return invalidFallback;
    }

    /// <summary>Builds the per-rule point toggle key: <c>...rule.{ruleId}.enabled</c>.</summary>
    public static string RuleEnabledKey(string ruleId)
    {
        return RuleTogglePrefix + ruleId + ".enabled";
    }

    /// <summary>
    /// Every key this analyzer understands. Any other key containing
    /// <c>.architecture_analyzer.</c> is a typo (in the property name or in the diagnostic-id
    /// segment) and is reported as AARC009. Only AARC002/AARC003 have honored rule toggles, so a
    /// <c>rule.AARC004.enabled</c> entry is a silent no-op and is flagged too.
    /// </summary>
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ContractRequiredKey,
        RequireLayerDeclarationKey,
        EnabledKey,
        ValidateNamespaceLayerKey,
        GeneratedCodeKey,
        SkipGeneratedCodeAarc002Key,
        SkipGeneratedCodeAarc003Key,
        RuleTogglePrefix + "AARC002.enabled",
        RuleTogglePrefix + "AARC003.enabled",
    };

    /// <summary>
    /// Reports AARC009 for every <c>architecture_analyzer.*</c> key that this analyzer does not
    /// recognize, so that a mistyped property name surfaces instead of silently doing nothing.
    /// </summary>
    /// <remarks>
    /// Key enumeration relies on <see cref="AnalyzerConfigOptions.Keys"/>, added in Roslyn 4.4 and
    /// overridden by the compiler's own options implementation. A host that predates it (or a
    /// third-party provider that never overrode the property) throws; unknown-key detection is
    /// then skipped, which never affects the fail-closed value handling.
    /// </remarks>
    public static void ReportUnknownKeys(
        AnalyzerConfigOptionsProvider provider,
        Compilation compilation,
        ConcurrentDictionary<string, (DiagnosticDescriptor Descriptor, Location Location, string Key, string Value)> diagnostics)
    {
        if (provider is null || compilation is null || diagnostics is null)
        {
            return;
        }

        ScanKeys(provider.GlobalOptions, diagnostics);
        foreach (var tree in compilation.SyntaxTrees)
        {
            ScanKeys(provider.GetOptions(tree), diagnostics);
        }
    }

    private static void ScanKeys(
        AnalyzerConfigOptions options,
        ConcurrentDictionary<string, (DiagnosticDescriptor Descriptor, Location Location, string Key, string Value)> diagnostics)
    {
        List<string> keys;
        try
        {
            keys = new List<string>(options.Keys);
        }
        catch (Exception ex) when (ex is NotImplementedException || ex is MissingMemberException)
        {
            return;
        }

        foreach (var key in keys)
        {
            if (key.IndexOf(ArchitectureAnalyzerMarker, StringComparison.OrdinalIgnoreCase) < 0
                || KnownKeys.Contains(key))
            {
                continue;
            }

            options.TryGetValue(key, out var rawValue);
            diagnostics.TryAdd(
                key,
                (
                    ArchitectureDiagnostics.UnknownConfigurationProperty,
                    Location.None,
                    key,
                    rawValue ?? string.Empty));
        }
    }

    private static void ReportInvalidValue(
        string key,
        string rawValue,
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