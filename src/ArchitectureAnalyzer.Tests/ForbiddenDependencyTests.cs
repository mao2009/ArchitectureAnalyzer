using ArchitectureAnalyzer.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;

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
    public async Task RepeatedReferenceToSamePair_IsReportedOnceAtEarliestSite()
    {
        // A pair is reported once, at its earliest reference site. The analyzer driver runs
        // syntax-node actions concurrently, so without the explicit tie-break in
        // AnalyzeDependencyDirection the reported site would be whichever reference won the race
        // and would vary from run to run (the F-D07 flake in the cross-analyzer parity suite).
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
                        public Sample.Application.AppService First { get; set; }

                        public Sample.Application.AppService Second { get; set; }
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
}
