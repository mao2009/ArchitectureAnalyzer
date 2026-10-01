using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ArchitectureAnalyzer.Tests;

/// <summary>
/// Structural performance invariants that should not depend on wall-clock timing.
/// </summary>
public sealed class PerformanceStructureTests
{
    private const string Contract = """
        {
          "schemaVersion": 5,
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
            { "name": "Application", "namespaceRoots": [ "Sample.Application" ] }
          ],
          "forbiddenDependencies": [
            {
              "from": "Domain",
              "to": "Application",
              "reason": "Domain must not depend on Application."
            }
          ]
        }
        """;

    [Fact]
    public async Task ContractAdditionalFile_IsReadOncePerCompilationSnapshot()
    {
        var trees = Enumerable.Range(0, 200)
            .Select(index => CSharpSyntaxTree.ParseText(
                $$"""
                  namespace Sample.Application
                  {
                      public sealed class AppService{{index}}
                      {
                      }
                  }

                  namespace Sample.Domain
                  {
                      public sealed class Entity{{index}}
                      {
                          public Sample.Application.AppService{{index}}? Service { get; set; }
                      }
                  }
                  """,
                path: $"/src/File{index:D3}.cs"))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "PerformanceStructure",
            trees,
            TestReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var contract = new CountingAdditionalText(
            ArchitectureContractAnalyzer.ContractFileName,
            Contract);

        await RunAsync(compilation, contract);

        Assert.Equal(1, contract.ReadCount);

        // Model the IDE/build incremental case: a new immutable compilation snapshot is created
        // after one file changes. The contract should be read once for the new snapshot too, not
        // once per syntax/symbol callback. A global cross-compilation cache would be unsafe
        // because AdditionalFiles can change between snapshots.
        var original = trees[0];
        var replacement = CSharpSyntaxTree.ParseText(
            original.ToString() + Environment.NewLine + "// edit",
            path: original.FilePath);
        var updated = compilation.ReplaceSyntaxTree(original, replacement);

        await RunAsync(updated, contract);

        Assert.Equal(2, contract.ReadCount);
    }

    private static async Task RunAsync(CSharpCompilation compilation, AdditionalText contract)
    {
        var options = new AnalyzerOptions(ImmutableArray.Create(contract));
        var analyzerOptions = new CompilationWithAnalyzersOptions(
            options,
            onAnalyzerException: null,
            concurrentAnalysis: true,
            logAnalyzerExecutionTime: false,
            reportSuppressedDiagnostics: false);

        _ = await compilation
            .WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new ArchitectureContractAnalyzer()),
                analyzerOptions)
            .GetAnalyzerDiagnosticsAsync();
    }

    private static ImmutableArray<MetadataReference> TestReferences()
    {
        return ImmutableArray.Create<MetadataReference>(
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location));
    }

    private sealed class CountingAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        internal CountingAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text);
        }

        public override string Path { get; }

        internal int ReadCount { get; private set; }

        public override SourceText? GetText(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return _text;
        }
    }
}
