using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using ArchitectureAnalyzer.Configuration;
using ArchitectureAnalyzer.Contract;
using ArchitectureAnalyzer.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ArchitectureAnalyzer;

/// <summary>
/// A generic interpreter for a project-supplied Architecture Contract.
/// </summary>
/// <remarks>
/// This type contains no layer names, namespace roots or API rules of its own: everything it
/// enforces comes from the consuming project's <c>architecture.contract.json</c>, supplied as a
/// Roslyn <c>AdditionalFiles</c> item. See <c>docs/design.md</c> §3.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ArchitectureContractAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The AdditionalFiles file name (case-insensitive) that carries the contract.</summary>
    public const string ContractFileName = ArchitectureContractLoader.ContractFileName;

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        ArchitectureDiagnostics.ArchitectureContractInvalid,
        ArchitectureDiagnostics.ForbiddenLayerDependency,
        ArchitectureDiagnostics.ForbiddenApiUsage,
        ArchitectureDiagnostics.InvalidConfigurationValue,
        ArchitectureDiagnostics.UnknownConfigurationProperty,
        ArchitectureDiagnostics.MissingLayerDeclaration,
        ArchitectureDiagnostics.MultipleLayerDeclarations,
        ArchitectureDiagnostics.LayerDeclarationNamespaceMismatch,
        ArchitectureDiagnostics.InteropBoundaryViolation);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        // Read compilation-wide operational config (contract_required is GlobalOptions).
        var configProvider = context.Options.AnalyzerConfigOptionsProvider;
        var configDiagnostics =
            new ConcurrentDictionary<string, (DiagnosticDescriptor, Location, string, string)>();

        // Mistyped property names are invisible to TryGetValue lookups, so scan the configured
        // keys once per compilation and report the unrecognized ones (AARC009).
        ConfigReader.ReportUnknownKeys(configProvider, context.Compilation, configDiagnostics);

        var contractFile = FindContractFile(context.Options.AdditionalFiles);
        if (contractFile is null)
        {
            // Opt-in semantics: a project that never declares a contract is never analyzed.
            // Report any invalid config values found even when no contract exists.
            context.RegisterCompilationEndAction(endContext =>
                ReportConfigDiagnostics(endContext, configDiagnostics));
            return;
        }

        var fileName = GetFileName(contractFile.Path);
        var text = contractFile.GetText(context.CancellationToken);
        var result = ArchitectureContractLoader.Load(text?.ToString());

        if (!result.Succeeded)
        {
            var reason = result.ErrorReason ?? "unknown error";
            context.RegisterCompilationEndAction(endContext =>
            {
                if (IsContractRequired(endContext.Compilation, configProvider))
                {
                    endContext.ReportDiagnostic(Diagnostic.Create(
                        ArchitectureDiagnostics.ArchitectureContractInvalid,
                        Location.None,
                        fileName,
                        reason));
                }

                ReportConfigDiagnostics(endContext, configDiagnostics);
            });
            return;
        }

        var contract = result.Contract!;

        // Layer-classification (AARC004/AARC005/AARC006) driven by symbol metadata so that
        // marker attributes, nesting, partial parts and generic definitions are all visible.
        if (contract.LayerDeclaration is not null)
        {
            context.RegisterSymbolAction(
                symbolContext => AnalyzeLayerDeclaration(symbolContext, contract, configProvider, configDiagnostics),
                SymbolKind.NamedType);
        }

        var reportedDependencies = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        context.RegisterSyntaxNodeAction(
            nodeContext =>
            {
                var treeConfig = ConfigReader.Read(configProvider, nodeContext.Node.SyntaxTree, configDiagnostics);
                AnalyzeDependencyDirection(nodeContext, contract, reportedDependencies, treeConfig);
            },
            SyntaxKind.IdentifierName,
            SyntaxKind.GenericName);

context.RegisterOperationBlockAction(blockContext =>
        {
            var firstTree = blockContext.OperationBlocks.Length > 0
                ? blockContext.OperationBlocks[0].Syntax.SyntaxTree
                : null;
            var treeConfig = firstTree is not null
                ? ConfigReader.Read(configProvider, firstTree, configDiagnostics)
                : OperationalConfig.Default;
            AnalyzeForbiddenApiUsage(blockContext, contract, treeConfig);
        });

        // Interop-boundary enforcement is declaration-based (method attributes), which the
        // operation-block path can never observe. Registration is conditional so contracts
        // without interopBoundaryRules carry zero overhead.
        if (contract.InteropBoundaryRules.Length > 0)
        {
            var reportedInterop = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            context.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeInteropBoundary(nodeContext, contract, reportedInterop),
                SyntaxKind.MethodDeclaration);
        }

        context.RegisterCompilationEndAction(endContext =>
            ReportConfigDiagnostics(endContext, configDiagnostics));
    }

    private static AdditionalText? FindContractFile(ImmutableArray<AdditionalText> additionalFiles)
    {
        AdditionalText? best = null;
        foreach (var candidate in additionalFiles)
        {
            if (!string.Equals(GetFileName(candidate.Path), ContractFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // v0.1 supports exactly one contract; a deterministic ordinal path sort keeps the
            // choice stable rather than dependent on item ordering.
            if (best is null || string.CompareOrdinal(candidate.Path, best.Path) < 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static void AnalyzeDependencyDirection(
        SyntaxNodeAnalysisContext context,
        ArchitectureContract contract,
        ConcurrentDictionary<string, byte> reported,
        OperationalConfig config)
    {
        if (!config.Enabled || !config.IsRuleEnabled("AARC002"))
        {
            return;
        }

        if (config.SkipGeneratedCode && IsGeneratedPath(context.Node.SyntaxTree.FilePath))
        {
            return;
        }

        var name = (SimpleNameSyntax)context.Node;
        if (name.FirstAncestorOrSelf<AttributeSyntax>() is not null)
        {
            return;
        }

        var semanticModel = context.SemanticModel;
        var cancellationToken = context.CancellationToken;

        var targetType = ResolveReferencedType(name, semanticModel, cancellationToken);
        if (targetType is null)
        {
            return;
        }

var targetLayer = ResolveOperationalLayer(contract, targetType, config);
        if (targetLayer is null)
        {
            return;
        }

        var (sourceType, sourceLayer) = ResolveEnclosingType(name, semanticModel, contract, cancellationToken);
        if (sourceType is null)
        {
            return;
        }

        sourceLayer = sourceLayer ?? UnclassifiedLayerWhenAllowed(config);
        if (sourceLayer is null
            || string.Equals(sourceLayer, targetLayer, StringComparison.Ordinal))
        {
            return;
        }

        if (!contract.IsForbiddenDependency(sourceLayer, targetLayer, out var reason))
        {
            return;
        }

        var sourceDisplay = sourceType.ToDisplayString();
        var targetDisplay = targetType.ToDisplayString();
        if (!reported.TryAdd(sourceDisplay + "->" + targetDisplay, 0))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            ArchitectureDiagnostics.ForbiddenLayerDependency,
            name.GetLocation(),
            sourceDisplay,
            sourceLayer,
            targetDisplay,
            targetLayer,
            reason));
    }

    private static void AnalyzeForbiddenApiUsage(
        OperationBlockAnalysisContext context,
        ArchitectureContract contract,
        OperationalConfig config)
    {
        if (!config.Enabled || !config.IsRuleEnabled("AARC003"))
        {
            return;
        }

        var sourceType = context.OwningSymbol switch
        {
            IMethodSymbol method => method.ContainingType,
            IFieldSymbol field => field.ContainingType,
            IPropertySymbol property => property.ContainingType,
            IEventSymbol @event => @event.ContainingType,
            _ => null,
        };

        if (sourceType is null)
        {
            return;
        }

var sourceLayer = ResolveOperationalLayer(contract, sourceType, config);
        if (sourceLayer is null)
        {
            return;
        }

        var rules = contract.GetApiRules(sourceLayer);
        if (rules.IsEmpty)
        {
            return;
        }

        // A single expression chain (for example Random.Shared.Next()) can match the same
        // whole-type rule at several operations; report each rule once per analyzed member.
        var reportedRules = new HashSet<int>();

        foreach (var block in context.OperationBlocks)
        {
            if (config.SkipGeneratedCode && IsGeneratedPath(block.Syntax.SyntaxTree.FilePath))
            {
                continue;
            }

            foreach (var operation in EnumerateOperations(block))
            {
                ISymbol? member;
                Location location;
                switch (operation)
                {
                    case IInvocationOperation invocation:
                        member = invocation.TargetMethod;
                        location = invocation.Syntax.GetLocation();
                        break;
                    case IObjectCreationOperation creation:
                        member = creation.Constructor;
                        location = creation.Syntax.GetLocation();
                        break;
                    case IMemberReferenceOperation reference:
                        member = reference.Member;
                        location = reference.Syntax.GetLocation();
                        break;
                    default:
                        continue;
                }

                if (member?.ContainingType is not { } owner)
                {
                    continue;
                }

                var matchName = GetMatchName(member, out var isConstructor);
                var ownerFullName = owner.OriginalDefinition.ToDisplayString();

                for (var ruleIndex = 0; ruleIndex < rules.Length; ruleIndex++)
                {
                    if (!rules[ruleIndex].Matches(ownerFullName, matchName, isConstructor)
                        || !reportedRules.Add(ruleIndex))
                    {
                        continue;
                    }

                    context.ReportDiagnostic(Diagnostic.Create(
                        ArchitectureDiagnostics.ForbiddenApiUsage,
                        location,
                        FormatApi(owner, member),
                        sourceLayer,
                        rules[ruleIndex].Reason));
                }
            }
        }
    }

private static void AnalyzeLayerDeclaration(
        SymbolAnalysisContext context,
        ArchitectureContract contract,
        AnalyzerConfigOptionsProvider configProvider,
        ConcurrentDictionary<string, (DiagnosticDescriptor Descriptor, Location Location, string Key, string Value)> configDiagnostics)
    {
        if (context.Symbol is not INamedTypeSymbol type
            || type.TypeKind != TypeKind.Class
            || type.IsImplicitlyDeclared
            || IsGeneratedPath(type.Locations.FirstOrDefault()?.SourceTree?.FilePath))
        {
            return;
        }

        var declaration = contract.LayerDeclaration;
        if (declaration is null)
        {
            return;
        }

        var config = type.Locations.FirstOrDefault()?.SourceTree is { } tree
            ? ConfigReader.Read(configProvider, tree, configDiagnostics)
            : OperationalConfig.Default;

        // Types inside the marker namespace are the attribute definitions themselves and are
        // exempt from declaration rules.
        if (IsWithinNamespace(type.ContainingNamespace, declaration.MarkerNamespace))
        {
            return;
        }

        var ownLayers = GetAppliedMarkerLayers(type, contract);

        // AARC004 fires only when the contract requires declarations and the operational
        // require_layer_declaration toggle has not relaxed enforcement for this tree.
        if (declaration.Required
            && config.RequireLayerDeclaration
            && ownLayers.Count == 0
            && !IsNestedInDeclaredLayer(type, contract))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ArchitectureDiagnostics.MissingLayerDeclaration,
                type.Locations.FirstOrDefault() ?? Location.None,
                type.ToDisplayString()));
        }

        if (ownLayers.Count > 1)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ArchitectureDiagnostics.MultipleLayerDeclarations,
                type.Locations.FirstOrDefault() ?? Location.None,
                type.ToDisplayString(),
                string.Join(", ", ownLayers)));
            return;
        }

        // AARC006 fires when the contract opts in to namespace consistency or the operational
        // validate_namespace_layer toggle enables it for this tree.
        if ((declaration.ValidateNamespaceConsistency || config.ValidateNamespaceLayer)
            && ownLayers.Count == 1)
        {
            var declaredLayer = ownLayers[0];
            var namespaceLayer = contract.ResolveLayer(GetNamespaceName(type.ContainingNamespace));
            if (namespaceLayer is not null
                && !string.Equals(declaredLayer, namespaceLayer, StringComparison.Ordinal))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    ArchitectureDiagnostics.LayerDeclarationNamespaceMismatch,
                    type.Locations.FirstOrDefault() ?? Location.None,
                    type.ToDisplayString(),
                    declaredLayer,
                    GetNamespaceName(type.ContainingNamespace),
                    namespaceLayer));
            }
        }
    }

    /// <summary>
    /// Resolves the distinct declared layers a type's own marker attributes map to, in first-seen
    /// contract order.
    /// </summary>
    private static List<string> GetAppliedMarkerLayers(INamedTypeSymbol type, ArchitectureContract contract)
    {
        var layers = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass is not { } attributeClass)
            {
                continue;
            }

            var layer = contract.ResolveMarkerLayer(attributeClass.OriginalDefinition.ToDisplayString());
            if (layer is not null && seen.Add(layer))
            {
                layers.Add(layer);
            }
        }

        return layers;
    }

    /// <summary>
    /// Resolves the layer a type participates in: a recognized marker attribute on the type or any
    /// enclosing type wins, otherwise the namespace-derived layer applies.
    /// </summary>
    private static string? ResolveLayer(INamedTypeSymbol type, ArchitectureContract contract)
    {
        if (contract.LayerDeclaration is not null)
        {
            foreach (var current in ContainingTypeChain(type.OriginalDefinition))
            {
                var declared = GetAppliedMarkerLayers(current, contract);
                if (declared.Count > 0)
                {
                    return declared[0];
                }
            }
        }

        return contract.ResolveLayer(GetNamespaceName(type.ContainingNamespace));
    }

    /// <summary>
    /// Whether a nested type inherits a layer from an enclosing type that resolves to a known
    /// layer (attribute or namespace), exempting it from AARC004.
    /// </summary>
    private static bool IsNestedInDeclaredLayer(INamedTypeSymbol type, ArchitectureContract contract)
    {
        if (type.ContainingType is not { } containing)
        {
            return false;
        }

        return ResolveLayer(containing, contract) is not null;
    }

    private static IEnumerable<INamedTypeSymbol> ContainingTypeChain(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            yield return current;
        }
    }

    private static bool IsWithinNamespace(INamespaceSymbol namespaceSymbol, string? markerNamespace)
    {
        if (markerNamespace is null || markerNamespace.Length == 0)
        {
            return false;
        }

        return IsWithin(GetNamespaceName(namespaceSymbol), markerNamespace);
    }

    private static bool IsWithin(string namespaceName, string root)
    {
        if (!namespaceName.StartsWith(root, StringComparison.Ordinal))
        {
            return false;
        }

        return namespaceName.Length == root.Length || namespaceName[root.Length] == '.';
    }

    private static void AnalyzeInteropBoundary(
        SyntaxNodeAnalysisContext context,
        ArchitectureContract contract,
        ConcurrentDictionary<string, byte> reportedInterop)
    {
        if (IsGeneratedPath(context.Node.SyntaxTree.FilePath))
        {
            return;
        }

        if (context.SemanticModel.GetDeclaredSymbol(context.Node, context.CancellationToken) is not IMethodSymbol method
            || method.Locations.FirstOrDefault() is not { } declarationLocation)
        {
            return;
        }

        foreach (var attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass?.OriginalDefinition?.ToDisplayString() is not { } attributeFullName)
            {
                continue;
            }

            var rule = contract.ResolveInteropRule(attributeFullName);
            if (rule is null)
            {
                continue;
            }

            // Deterministically point at the declaration part that carries the matched
            // attribute: the merged partial-method symbol exposes several locations with no
            // guaranteed ordering.
            var violationLocation = attribute.ApplicationSyntaxReference
                ?.GetSyntax(context.CancellationToken)
                .FirstAncestorOrSelf<MethodDeclarationSyntax>()
                ?.Identifier.GetLocation()
                ?? declarationLocation;

            // The containing type's classification uses the same attribute-aware semantics as
            // AARC002/AARC003 (ResolveLayer): a marker attribute on the method's type or any
            // enclosing type wins over its namespace.
            var declaringLayer = ResolveLayer(method.ContainingType, contract);
            if (declaringLayer is not null
                && string.Equals(declaringLayer, rule.AllowedLayer, StringComparison.Ordinal))
            {
                continue;
            }

            // Unclassified code (declaringLayer is null) is never the allowed layer.
            // A declarative symbol merges partial parts, so the same (method, rule) pair
            // would otherwise be reported once per part.
            if (!reportedInterop.TryAdd(method.ToDisplayString() + "\u001f" + attributeFullName, 0))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                ArchitectureDiagnostics.InteropBoundaryViolation,
                violationLocation,
                method.Name,
                rule.AllowedLayer,
                rule.Reason));
        }
    }

    private static IEnumerable<IOperation> EnumerateOperations(IOperation root)
    {
        var stack = new Stack<IOperation>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is null || current.Kind == OperationKind.None)
            {
                continue;
            }

            yield return current;
            foreach (var child in current.ChildOperations)
            {
                stack.Push(child);
            }
        }
    }

    private static INamedTypeSymbol? ResolveReferencedType(
        SyntaxNode name,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var symbol = semanticModel.GetSymbolInfo(name, cancellationToken).Symbol
            ?? semanticModel.GetDeclaredSymbol(name, cancellationToken);

        return symbol switch
        {
            INamedTypeSymbol namedType => namedType.OriginalDefinition,
            IMethodSymbol method => method.ContainingType?.OriginalDefinition,
            IPropertySymbol property => property.ContainingType?.OriginalDefinition,
            IFieldSymbol field => field.ContainingType?.OriginalDefinition,
            IEventSymbol @event => @event.ContainingType?.OriginalDefinition,
            _ => null,
        };
    }

    private static (INamedTypeSymbol? Type, string? Layer) ResolveEnclosingType(
        SyntaxNode node,
        SemanticModel semanticModel,
        ArchitectureContract contract,
        CancellationToken cancellationToken)
    {
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor is TypeDeclarationSyntax typeDeclaration)
            {
                if (semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) is { } type)
                {
                    return (type, ResolveLayer(type, contract));
                }

                return (null, null);
            }
        }

        return (null, null);
    }

    private static string GetMatchName(ISymbol member, out bool isConstructor)
    {
        switch (member)
        {
            case IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor }:
                isConstructor = true;
                return member.Name;
            case IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet } accessor:
                isConstructor = false;
                return accessor.AssociatedSymbol?.Name ?? accessor.Name;
            default:
                isConstructor = false;
                return member.Name;
        }
    }

    private static string FormatApi(INamedTypeSymbol owner, ISymbol member)
    {
        switch (member)
        {
            case IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor }:
                return $"new {owner.Name}()";
            case IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet } accessor:
                return $"{owner.Name}.{accessor.AssociatedSymbol?.Name ?? accessor.Name}";
            default:
                return $"{owner.Name}.{member.Name}";
        }
    }

    private static string GetNamespaceName(INamespaceSymbol? namespaceSymbol)
    {
        return namespaceSymbol is null || namespaceSymbol.IsGlobalNamespace
            ? string.Empty
            : namespaceSymbol.ToDisplayString();
    }

    /// <summary>
    /// Determines whether contract enforcement is required for the compilation. Reads the
    /// operational <c>contract_required</c> property through the compilation's trees; per-tree
    /// resolution also surfaces <c>.globalconfig</c> values (docs/configuration-design.md §6.4).
    /// </summary>
    private static bool IsContractRequired(
        Compilation compilation,
        AnalyzerConfigOptionsProvider configProvider)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var config = ConfigReader.Read(configProvider, tree, diagnostics: null);
            if (!config.ContractRequired)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves the operational layer for a type, applying the <c>require_layer_declaration</c>
    /// toggle: a recognized marker attribute on the type or any enclosing type wins, otherwise the
    /// namespace-derived layer applies, and when the toggle is <see langword="false"/> an
    /// unclassified namespace resolves to the synthetic "Unclassified" layer so it participates in
    /// edge checks.
    /// </summary>
    private static string? ResolveOperationalLayer(
        ArchitectureContract contract,
        INamedTypeSymbol type,
        OperationalConfig config)
    {
        return ResolveLayer(type, contract) ?? UnclassifiedLayerWhenAllowed(config);
    }

    private static string? UnclassifiedLayerWhenAllowed(OperationalConfig config)
    {
        return config.RequireLayerDeclaration ? null : ConfigReader.UnclassifiedLayerName;
    }

    private static void ReportConfigDiagnostics(
        CompilationAnalysisContext context,
        ConcurrentDictionary<string, (DiagnosticDescriptor Descriptor, Location Location, string Key, string Value)> diagnostics)
    {
        foreach (var (descriptor, location, key, value) in diagnostics.Values)
        {
            context.ReportDiagnostic(Diagnostic.Create(descriptor, location, key, value));
        }

        diagnostics.Clear();
    }

    private static string GetFileName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var separator = path.LastIndexOfAny(new[] { '/', '\\' });
        return separator < 0 ? path : path.Substring(separator + 1);
    }

    private static bool IsGeneratedPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var normalized = path!.Replace('\\', '/');

        return normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0
            || normalized.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase) >= 0
            || normalized.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("bin/", StringComparison.OrdinalIgnoreCase);
    }
}
