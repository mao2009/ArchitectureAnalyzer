using System.Collections.Immutable;
using ArchitectureAnalyzer.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;

namespace ArchitectureAnalyzer.Tests;

/// <summary>
/// Regression tests for #70: XML doc <c>cref</c> references are not code dependencies, and AARC002
/// must be identical whatever the <see cref="DocumentationMode"/> (i.e. GenerateDocumentationFile).
/// </summary>
public sealed class DocumentationCommentDependencyTests
{
    private const string Contract = """
        {
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
            { "name": "Application", "namespaceRoots": [ "Sample.Application" ] }
          ],
          "forbiddenDependencies": [
            { "from": "Domain", "to": "Application", "reason": "no" }
          ]
        }
        """;

    private const string ApplicationTypes = """
        namespace Sample.Application
        {
            public class AppService { public void Run() { } }
            public class Repo<T> { }
        }
        """;

    public static TheoryData<string> DocComments => new()
    {
        "/// <summary>See <see cref=\"Sample.Application.AppService\"/>.</summary>",
        "/// <summary>x</summary><seealso cref=\"Sample.Application.AppService\"/>",
        "/// <inheritdoc cref=\"Sample.Application.AppService\"/>",
        "/// <summary>See <see cref=\"Sample.Application.Repo{T}\"/>.</summary>",
        "/// <summary>See <see cref=\"global::Sample.Application.AppService\"/>.</summary>",
        "/// <summary>See <see cref=\"Sample.Application.AppService.Run\"/>.</summary>",
    };

    [Theory]
    [MemberData(nameof(DocComments))]
    public async Task DocCref_IsNotADependency_InAnyDocumentationMode(string docComment)
    {
        var source = ApplicationTypes + $$"""

            namespace Sample.Domain
            {
                {{docComment}}
                public class DomainEntity { }
            }
            """;

        foreach (var mode in AllModes)
        {
            Assert.Empty(await RunAarc002Async(source, mode));
        }
    }

    [Fact]
    public async Task RealDependency_IsReportedIdentically_InEveryDocumentationMode()
    {
        // The cref precedes the real use, so a doc-mode-sensitive site would shift the location.
        var source = ApplicationTypes + """

            namespace Sample.Domain
            {
                /// <summary>See <see cref="Sample.Application.AppService"/>.</summary>
                public class DomainEntity
                {
                    public Sample.Application.AppService Service { get; set; }
                }
            }
            """;

        var results = new List<string>();
        foreach (var mode in AllModes)
        {
            var diagnostics = await RunAarc002Async(source, mode);
            var single = Assert.Single(diagnostics);
            results.Add($"{single.GetMessage()}@{single.Location.GetLineSpan()}");
        }

        Assert.Single(results.Distinct());
        Assert.Contains("(10,", results[0]);
    }

    private static readonly DocumentationMode[] AllModes =
    {
        DocumentationMode.None,
        DocumentationMode.Parse,
        DocumentationMode.Diagnose,
    };

    private static async Task<ImmutableArray<Diagnostic>> RunAarc002Async(string source, DocumentationMode mode)
    {
        var references = await ReferenceAssemblies.Net.Net90
            .ResolveAsync(LanguageNames.CSharp, CancellationToken.None);

        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(documentationMode: mode),
            path: "/0/Test0.cs");
        var compilation = CSharpCompilation.Create(
            "DocModeTest",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var options = new AnalyzerOptions(
            ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText(ArchitectureContractAnalyzer.ContractFileName, Contract)));

        var diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ArchitectureContractAnalyzer()), options)
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None);

        return diagnostics
            .Where(d => d.Id == ArchitectureDiagnostics.ForbiddenLayerDependency.Id)
            .ToImmutableArray();
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }
}
