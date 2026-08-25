using System.Collections.Immutable;
using ArchitectureAnalyzer.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;

namespace ArchitectureAnalyzer.Tests;

/// <summary>
/// Dependency-direction enforcement (AARC002) driven entirely by the supplied contract.
/// </summary>
public sealed class ForbiddenDependencyTests
{
    private const string Reason = "Domain must not depend on the outer Application layer.";

    private const string Contract = """
        {
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
            { "name": "Application", "namespaceRoots": [ "Sample.Application" ] }
          ],
          "forbiddenDependencies": [
            { "from": "Domain", "to": "Application", "reason": "Domain must not depend on the outer Application layer." }
          ]
        }
        """;

    [Fact]
    public async Task DomainReferencingApplication_ReportsForbiddenDependency()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                namespace Sample.Application
                {
                    public class AppService
                    {
                    }
                }

                namespace Sample.Domain
                {
                    public class DomainEntity
                    {
                        public Sample.Application.AppService Service { get; set; }
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenLayerDependency,
            12,
            35,
            "Sample.Domain.DomainEntity",
            "Domain",
            "Sample.Application.AppService",
            "Application",
            Reason));

        await test.RunAsync();
    }

    [Fact]
    public async Task ApplicationReferencingDomain_IsAllowed()
    {
        // The contract forbids Domain -> Application only; the check must not be symmetric.
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainEntity
                    {
                    }
                }

                namespace Sample.Application
                {
                    public class AppService
                    {
                        public Sample.Domain.DomainEntity Entity { get; set; }
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task TwoIndependentViolations_ReportBothDiagnostics()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                namespace Sample.Application
                {
                    public class AppService
                    {
                    }

                    public class OtherService
                    {
                    }
                }

                namespace Sample.Domain
                {
                    public class FirstEntity
                    {
                        public Sample.Application.AppService Service { get; set; }
                    }

                    public class SecondEntity
                    {
                        public Sample.Application.OtherService Other { get; set; }
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenLayerDependency,
            16,
            35,
            "Sample.Domain.FirstEntity",
            "Domain",
            "Sample.Application.AppService",
            "Application",
            Reason));

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenLayerDependency,
            21,
            35,
            "Sample.Domain.SecondEntity",
            "Domain",
            "Sample.Application.OtherService",
            "Application",
            Reason));

        await test.RunAsync();
    }

    [Fact]
    public async Task RepeatedReferenceToSamePair_IsReportedOnce()
    {
        const string source = """
            namespace Sample.Application
            {
                public class AppService
                {
                }
            }

            namespace Sample.Domain
            {
                public class DomainEntity
                {
                    public Sample.Application.AppService First { get; set; }

                    public Sample.Application.AppService Second { get; set; }
                }
            }
            """;

        // This assertion is deliberately about the *count*, not the location: the analyzer runs
        // with EnableConcurrentExecution, so which of the two equivalent references wins the
        // de-duplication race is not deterministic. CSharpAnalyzerTest always verifies exact
        // locations, so this one case drives Roslyn directly instead.
        var diagnostics = await RunAnalyzerAsync(Contract, source);
        var dependencyDiagnostics = diagnostics
            .Where(diagnostic => diagnostic.Id == ArchitectureDiagnostics.ForbiddenLayerDependency.Id)
            .ToList();

        Assert.Single(dependencyDiagnostics);
        Assert.Equal(
            "'Sample.Domain.DomainEntity' (Domain) must not depend on "
                + "'Sample.Application.AppService' (Application): " + Reason,
            dependencyDiagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task UnclassifiedNamespace_IsInvisibleToTheAnalyzer()
    {
        // Sample.Tooling matches no declared namespaceRoot, so it has no layer and no rule can
        // apply to it (docs/design.md §5 and §9).
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                namespace Sample.Application
                {
                    public class AppService
                    {
                    }
                }

                namespace Sample.Tooling
                {
                    public class Helper
                    {
                        public Sample.Application.AppService Service { get; set; }
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    /// <summary>
    /// Runs the analyzer over a single source file and contract without going through
    /// CSharpAnalyzerTest, for the cases where only the diagnostic count is deterministic.
    /// </summary>
    private static async Task<ImmutableArray<Diagnostic>> RunAnalyzerAsync(string contractJson, string source)
    {
        var references = await ReferenceAssemblies.Net.Net90
            .ResolveAsync(LanguageNames.CSharp, CancellationToken.None);

        var compilation = CSharpCompilation.Create(
            "DedupTest",
            new[] { CSharpSyntaxTree.ParseText(source, path: "Test0.cs") },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var options = new AnalyzerOptions(
            ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText(ArchitectureContractAnalyzer.ContractFileName, contractJson)));

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new ArchitectureContractAnalyzer()),
            options);

        return await withAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None);
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
