using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
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
        ArchitectureDiagnostics.ForbiddenApiUsage);

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
        var contractFile = FindContractFile(context.Options.AdditionalFiles);
        if (contractFile is null)
        {
            // Opt-in semantics: a project that never declares a contract is never analyzed.
            return;
        }

        var fileName = GetFileName(contractFile.Path);
        var text = contractFile.GetText(context.CancellationToken);
        var result = ArchitectureContractLoader.Load(text?.ToString());

        if (!result.Succeeded)
        {
            var reason = result.ErrorReason ?? "unknown error";
            context.RegisterCompilationEndAction(endContext => endContext.ReportDiagnostic(
                Diagnostic.Create(
                    ArchitectureDiagnostics.ArchitectureContractInvalid,
                    Location.None,
                    fileName,
                    reason)));
            return;
        }

        var contract = result.Contract!;

        var reportedDependencies = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        context.RegisterSyntaxNodeAction(
            nodeContext => AnalyzeDependencyDirection(nodeContext, contract, reportedDependencies),
            SyntaxKind.IdentifierName,
            SyntaxKind.GenericName);

        context.RegisterOperationBlockAction(blockContext => AnalyzeForbiddenApiUsage(blockContext, contract));
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
        ConcurrentDictionary<string, byte> reported)
    {
        if (IsGeneratedPath(context.Node.SyntaxTree.FilePath))
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

        var targetLayer = contract.ResolveLayer(GetNamespaceName(targetType.ContainingNamespace));
        if (targetLayer is null)
        {
            return;
        }

        var (sourceType, sourceLayer) = ResolveEnclosingType(name, semanticModel, contract, cancellationToken);
        if (sourceType is null
            || sourceLayer is null
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

    private static void AnalyzeForbiddenApiUsage(OperationBlockAnalysisContext context, ArchitectureContract contract)
    {
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

        var sourceLayer = contract.ResolveLayer(GetNamespaceName(sourceType.ContainingNamespace));
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
            if (IsGeneratedPath(block.Syntax.SyntaxTree.FilePath))
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
                    return (type, contract.ResolveLayer(GetNamespaceName(type.ContainingNamespace)));
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
