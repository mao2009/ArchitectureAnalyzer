using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace ArchitectureAnalyzer.Configuration;

/// <summary>
/// Typed operational configuration read from .editorconfig / AnalyzerConfig.
/// </summary>
/// <remarks>
/// Every property has a hardcoded default matching current v0.1 behavior, so projects without
/// any .editorconfig operational entries see no behavior change. Keys are read per-tree via
/// <see cref="AnalyzerConfigOptionsProvider.GetOptions(SyntaxTree)"/> (see docs/configuration-design.md).
/// </remarks>
public sealed class OperationalConfig
{
    /// <summary>The default instance used when no .editorconfig entries exist.</summary>
    public static readonly OperationalConfig Default = CreateDefault();

    /// <summary>A fully-disabled config, produced when <c>architecture_analyzer.enabled = false</c>.</summary>
    public static readonly OperationalConfig Disabled = new(
        enabled: false,
        requireLayerDeclaration: true,
        skipGeneratedCode: true,
        validateNamespaceLayer: false,
        contractRequired: true,
        rulesEnabled: ImmutableDictionary<string, bool>.Empty);

    private OperationalConfig(
        bool enabled,
        bool requireLayerDeclaration,
        bool skipGeneratedCode,
        bool validateNamespaceLayer,
        bool contractRequired,
        ImmutableDictionary<string, bool> rulesEnabled)
    {
        Enabled = enabled;
        RequireLayerDeclaration = requireLayerDeclaration;
        SkipGeneratedCode = skipGeneratedCode;
        ValidateNamespaceLayer = validateNamespaceLayer;
        ContractRequired = contractRequired;
        _rulesEnabled = rulesEnabled;
    }

    private readonly ImmutableDictionary<string, bool> _rulesEnabled;

    /// <summary>Whether analysis is enabled for the tree (fake-populated from <c>enabled</c>).</summary>
    public bool Enabled { get; }

    /// <summary>
    /// When <see langword="true"/> (default), code in a namespace that resolves to no declared
    /// layer is invisible to AARC002/AARC003. When <see langword="false"/>, unclassified code is
    /// treated as belonging to a synthetic "Unclassified" layer.
    /// </summary>
    public bool RequireLayerDeclaration { get; }

    /// <summary>
    /// When <see langword="true"/> (default), files matching generated-code path patterns are skipped.
    /// </summary>
    public bool SkipGeneratedCode { get; }

    /// <summary>
    /// When <see langword="true"/>, namespace/layer consistency is verified. Seam for the
    /// declaration-validation rules (#32); the flag is read here but the AARC006 verdict is
    /// produced only when a layerDeclaration section exists in the contract.
    /// </summary>
    public bool ValidateNamespaceLayer { get; }

    /// <summary>
    /// When <see langword="true"/> (default), a missing or invalid contract file produces AARC001.
    /// When <see langword="false"/>, the analyzer silently no-ops.
    /// </summary>
    public bool ContractRequired { get; }

    /// <summary>Returns whether a specific rule is switched on for the tree.</summary>
    /// <param name="ruleId">A diagnostic id such as "AARC002" or "AARC003".</param>
    public bool IsRuleEnabled(string ruleId)
    {
        return _rulesEnabled.TryGetValue(ruleId, out var enabled) ? enabled : true;
    }

    /// <summary>Creates an operational config from resolved values.</summary>
    public static OperationalConfig Create(
        bool enabled = true,
        bool requireLayerDeclaration = true,
        bool skipGeneratedCode = true,
        bool validateNamespaceLayer = false,
        bool contractRequired = true,
        IReadOnlyDictionary<string, bool>? rulesEnabled = null)
    {
        ImmutableDictionary<string, bool> rules;
        if (rulesEnabled is null)
        {
            rules = ImmutableDictionary<string, bool>.Empty;
        }
        else
        {
            var builder = ImmutableDictionary.CreateBuilder<string, bool>(StringComparer.Ordinal);
            foreach (var pair in rulesEnabled)
            {
                builder[pair.Key] = pair.Value;
            }

            rules = builder.ToImmutable();
        }

        return new OperationalConfig(
            enabled,
            requireLayerDeclaration,
            skipGeneratedCode,
            validateNamespaceLayer,
            contractRequired,
            rules);
    }

    private static OperationalConfig CreateDefault()
    {
        return Create();
    }
}