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

    /// <summary>AARC008 — an architecture_analyzer operational property has an invalid value.</summary>
    public static readonly DiagnosticDescriptor InvalidConfigurationValue = new(
        id: "AARC008",
        title: "Invalid architecture analyzer configuration value",
        messageFormat: "The value '{1}' for property '{0}' is not valid; falling back to the default",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An architecture_analyzer.* property in .editorconfig or .globalconfig has a value "
            + "that cannot be parsed. The property is ignored and the hardcoded default is used.",
        helpLinkUri: HelpLinkPrefix + "aarc008");

    /// <summary>AARC004 — a required layer declaration is missing.</summary>
    public static readonly DiagnosticDescriptor MissingLayerDeclaration = new(
        id: "AARC004",
        title: "Missing required architecture layer declaration",
        messageFormat: "Type '{0}' must declare an architecture layer via one of the marker "
            + "attributes configured in layerDeclaration",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "layerDeclaration.required is true and a class carries no recognized marker "
            + "attribute (and is not exempt as nested, generated, partial-qualified or in the "
            + "marker namespace).",
        helpLinkUri: HelpLinkPrefix + "aarc004");

    /// <summary>AARC005 — a type declares more than one distinct architecture layer.</summary>
    public static readonly DiagnosticDescriptor MultipleLayerDeclarations = new(
        id: "AARC005",
        title: "Multiple distinct architecture layer declarations",
        messageFormat: "Type '{0}' declares more than one architecture layer: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A type carries marker attributes mapping to more than one distinct layer. "
            + "The declaration is ambiguous and must be reduced to a single layer.",
        helpLinkUri: HelpLinkPrefix + "aarc005");

    /// <summary>AARC006 — a declared layer contradicts the namespace-derived layer.</summary>
    public static readonly DiagnosticDescriptor LayerDeclarationNamespaceMismatch = new(
        id: "AARC006",
        title: "Architecture layer declaration contradicts namespace layer",
        messageFormat: "Type '{0}' declares layer '{1}' but its namespace '{2}' implies layer '{3}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "validateNamespaceConsistency is true and a type's declared (attribute) layer "
            + "differs from the layer its namespace implies. Attribute overrides are intentional, "
            + "so this is informational rather than blocking.",
        helpLinkUri: HelpLinkPrefix + "aarc006");

    /// <summary>AARC007 — a method carrying a configured interop attribute sits outside its allowed layer.</summary>
    public static readonly DiagnosticDescriptor InteropBoundaryViolation = new(
        id: "AARC007",
        title: "Interop declaration outside allowed layer",
        messageFormat: "Interop declaration '{0}' must be declared inside the '{1}' layer: {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A method carrying an attribute listed in interopBoundaryRules is declared "
            + "outside the layer the rule allows. The declaration must move into the allowed layer.",
        helpLinkUri: HelpLinkPrefix + "aarc007");
}
