using Microsoft.CodeAnalysis;

namespace ArchitectureAnalyzer.Diagnostics;

/// <summary>
/// The complete set of diagnostics produced by <see cref="ArchitectureContractAnalyzer"/>.
/// </summary>
/// <remarks>
/// These descriptors carry no project-specific knowledge: every layer name, namespace root and
/// API rule is supplied at analysis time by the consuming project's Architecture Contract.
/// See <c>docs/design.md</c> §3.
/// </remarks>
public static class ArchitectureDiagnostics
{
    /// <summary>Diagnostic category shared by every rule in this analyzer.</summary>
    public const string Category = "Architecture";

    private const string HelpLinkPrefix =
        "https://github.com/mao2009/ArchitectureAnalyzer/blob/main/docs/diagnostics.md#";

    /// <summary>AARC001 — the Architecture Contract file exists but could not be loaded.</summary>
    public static readonly DiagnosticDescriptor ArchitectureContractInvalid = new(
        id: "AARC001",
        title: "Architecture contract could not be loaded",
        messageFormat: "Architecture contract '{0}' could not be loaded: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A referenced architecture.contract.json file must be readable, valid JSON and "
            + "internally consistent, otherwise architecture enforcement would silently disappear.",
        helpLinkUri: HelpLinkPrefix + "aarc001",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>AARC002 — a type referenced another type across a forbidden layer edge.</summary>
    public static readonly DiagnosticDescriptor ForbiddenLayerDependency = new(
        id: "AARC002",
        title: "Forbidden architecture dependency direction",
        messageFormat: "'{0}' ({1}) must not depend on '{2}' ({3}): {4}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A type whose namespace maps to one declared layer referenced a type in another "
            + "declared layer over an edge listed in the contract's forbiddenDependencies.",
        helpLinkUri: HelpLinkPrefix + "aarc002");

    /// <summary>AARC003 — a type used an API its layer forbids.</summary>
    public static readonly DiagnosticDescriptor ForbiddenApiUsage = new(
        id: "AARC003",
        title: "Forbidden API usage in architecture layer",
        messageFormat: "'{0}' is forbidden in the {1} layer: {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A type whose namespace maps to a declared layer used an API listed in that "
            + "layer's forbiddenApis entries in the Architecture Contract.",
        helpLinkUri: HelpLinkPrefix + "aarc003");
}
