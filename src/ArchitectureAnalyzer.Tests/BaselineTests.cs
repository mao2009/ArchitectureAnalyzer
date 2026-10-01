using ArchitectureAnalyzer.Baseline;
using ArchitectureAnalyzer.Diagnostics;

namespace ArchitectureAnalyzer.Tests;

/// <summary>Baseline schema and ratcheting behavior.</summary>
public sealed class BaselineTests
{
    private const string DependencyContract = """
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
    public void Loader_AcceptsAllBaselinableDiagnosticIds()
    {
        const string baseline = """
            {
              "version": 1,
              "entries": [
                { "diagnosticId": "AARC002", "key": "dep" },
                { "diagnosticId": "AARC003", "key": "api" },
                { "diagnosticId": "AARC004", "key": "missing" },
                { "diagnosticId": "AARC005", "key": "multiple" },
                { "diagnosticId": "AARC006", "key": "mismatch" },
                { "diagnosticId": "AARC007", "key": "interop" },
                { "diagnosticId": "AARC010", "key": "coverage" },
                { "diagnosticId": "AARC011", "key": "cycle" }
              ]
            }
            """;

        var result = ArchitectureBaselineLoader.Load(baseline);

        Assert.True(result.Succeeded, result.ErrorReason);
        Assert.True(result.Baseline!.Contains("AARC002", "dep"));
        Assert.True(result.Baseline.Contains("AARC011", "cycle"));
    }

    [Theory]
    [InlineData("AARC001")]
    [InlineData("AARC008")]
    [InlineData("AARC009")]
    [InlineData("AARC012")]
    [InlineData("AARC013")]
    public void Loader_RejectsIntegrityAndConfigurationDiagnostics(string diagnosticId)
    {
        var baseline = """
            {
              "version": 1,
              "entries": [
                {
                  "diagnosticId": "__ID__",
                  "key": "unsafe"
                }
              ]
            }
            """.Replace("__ID__", diagnosticId, StringComparison.Ordinal);

        var result = ArchitectureBaselineLoader.Load(baseline);

        Assert.False(result.Succeeded);
        Assert.Equal($"diagnosticId '{diagnosticId}' cannot be baselined", result.ErrorReason);
    }

    [Fact]
    public void Loader_RejectsDuplicateEntry()
    {
        const string baseline = """
            {
              "version": 1,
              "entries": [
                { "diagnosticId": "AARC002", "key": "A -> B" },
                { "diagnosticId": "AARC002", "key": "A -> B", "message": "metadata only" }
              ]
            }
            """;

        var result = ArchitectureBaselineLoader.Load(baseline);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "baseline entry 'AARC002' / 'A -> B' is declared more than once",
            result.ErrorReason);
    }

    [Fact]
    public async Task Aarc002_ExactBaselineHit_IsSilentAcrossLineMovement()
    {
        const string baseline = """
            {
              "version": 1,
              "entries": [
                {
                  "diagnosticId": "AARC002",
                  "key": "Sample.Domain.DomainEntity -> Sample.Application.AppService"
                }
              ]
            }
            """;

        var test = new ArchitectureAnalyzerTest(DependencyContract)
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
        test.TestState.AdditionalFiles.Add((ArchitectureBaseline.FileName, baseline));

        await test.RunAsync();
    }

    [Fact]
    public async Task Aarc002_ChangedSourceSymbol_IsNewViolation()
    {
        const string baseline = """
            {
              "version": 1,
              "entries": [
                {
                  "diagnosticId": "AARC002",
                  "key": "Sample.Domain.DomainEntity -> Sample.Application.AppService"
                }
              ]
            }
            """;

        var test = new ArchitectureAnalyzerTest(DependencyContract)
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
                    public class RenamedEntity
                    {
                        public Sample.Application.AppService Service { get; set; }
                    }
                }
                """,
        };
        test.TestState.AdditionalFiles.Add((ArchitectureBaseline.FileName, baseline));
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenLayerDependency,
            12,
            35,
            "Sample.Domain.RenamedEntity",
            "Domain",
            "Sample.Application.AppService",
            "Application",
            "Domain must not depend on Application."));

        await test.RunAsync();
    }

    [Fact]
    public async Task Aarc003_BaselineKeyUsesOwningMemberAndForbiddenRuleIdentity()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ],
              "forbiddenApis": [
                {
                  "layer": "Domain",
                  "type": "System.Console",
                  "member": "WriteLine",
                  "reason": "No console."
                }
              ]
            }
            """;
        const string baseline = """
            {
              "version": 1,
              "entries": [
                {
                  "diagnosticId": "AARC003",
                  "key": "Sample.Domain.LegacyConsole.Run() -> System.Console.WriteLine"
                }
              ]
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class LegacyConsole
                    {
                        public void Run()
                        {
                            System.Console.WriteLine("legacy");
                        }
                    }
                }
                """,
        };
        test.TestState.AdditionalFiles.Add((ArchitectureBaseline.FileName, baseline));

        await test.RunAsync();
    }

    [Fact]
    public async Task Aarc003_ChangedOwningMember_IsNewViolation()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ],
              "forbiddenApis": [
                {
                  "layer": "Domain",
                  "type": "System.Console",
                  "member": "WriteLine",
                  "reason": "No console."
                }
              ]
            }
            """;
        const string baseline = """
            {
              "version": 1,
              "entries": [
                {
                  "diagnosticId": "AARC003",
                  "key": "Sample.Domain.LegacyConsole.Run() -> System.Console.WriteLine"
                }
              ]
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class LegacyConsole
                    {
                        public void RenamedRun()
                        {
                            System.Console.WriteLine("legacy");
                        }
                    }
                }
                """,
        };
        test.TestState.AdditionalFiles.Add((ArchitectureBaseline.FileName, baseline));
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenApiUsage,
            7,
            13,
            "Console.WriteLine",
            "Domain",
            "No console."));

        await test.RunAsync();
    }

    [Fact]
    public async Task Aarc010_BaselineCanRatchetCoverageDebt()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "unclassifiedCode": "error",
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ]
            }
            """;
        const string baseline = """
            {
              "version": 1,
              "entries": [
                { "diagnosticId": "AARC010", "key": "Sample.Tools.Tool" }
              ]
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = "namespace Sample.Tools { public class Tool { } }",
        };
        test.TestState.AdditionalFiles.Add((ArchitectureBaseline.FileName, baseline));

        await test.RunAsync();
    }

    [Fact]
    public async Task Aarc011_BaselineCanRatchetDeclaredCycleDebt()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [
                { "name": "A", "namespaceRoots": [ "Sample.A" ] },
                { "name": "B", "namespaceRoots": [ "Sample.B" ] }
              ],
              "allowedDependencies": [
                { "from": "B", "to": [ "A" ] },
                { "from": "A", "to": [ "B" ] }
              ],
              "dependencyGraph": {
                "requireAcyclic": true
              }
            }
            """;
        const string baseline = """
            {
              "version": 1,
              "entries": [
                { "diagnosticId": "AARC011", "key": "A -> B -> A" }
              ]
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = "namespace Sample.A { public class AType { } }",
        };
        test.TestState.AdditionalFiles.Add((ArchitectureBaseline.FileName, baseline));

        await test.RunAsync();
    }

    [Fact]
    public async Task InvalidBaseline_ReportsAarc012AndSuppressesNothing()
    {
        const string baseline = """
            {
              "version": 99,
              "entries": []
            }
            """;

        var test = new ArchitectureAnalyzerTest(DependencyContract)
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
        test.TestState.AdditionalFiles.Add((ArchitectureBaseline.FileName, baseline));
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.ArchitectureBaselineInvalid,
            ArchitectureBaseline.FileName,
            "unsupported baseline version '99'; supported version is 1"));
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenLayerDependency,
            12,
            35,
            "Sample.Domain.DomainEntity",
            "Domain",
            "Sample.Application.AppService",
            "Application",
            "Domain must not depend on Application."));

        await test.RunAsync();
    }

    [Fact]
    public async Task MultipleBaselineFiles_ReportAarc012AndSuppressNothing()
    {
        const string baseline = """
            {
              "version": 1,
              "entries": []
            }
            """;

        var test = new ArchitectureAnalyzerTest(DependencyContract)
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
        test.TestState.AdditionalFiles.Add(("/a/" + ArchitectureBaseline.FileName, baseline));
        test.TestState.AdditionalFiles.Add(("/b/" + ArchitectureBaseline.FileName, baseline));
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.ArchitectureBaselineInvalid,
            ArchitectureBaseline.FileName,
            "multiple architecture baseline files were supplied: /a/architecture.baseline.json, /b/architecture.baseline.json; include at most one 'architecture.baseline.json' AdditionalFiles item"));
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenLayerDependency,
            12,
            35,
            "Sample.Domain.DomainEntity",
            "Domain",
            "Sample.Application.AppService",
            "Application",
            "Domain must not depend on Application."));

        await test.RunAsync();
    }
}
