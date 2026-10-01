using ArchitectureAnalyzer.Diagnostics;

namespace ArchitectureAnalyzer.Tests;

/// <summary>
/// Schema-v5 exact, justified architecture exceptions for AARC002/AARC003.
/// </summary>
public sealed class ArchitectureExceptionTests
{
    private const string DependencyReason = "Domain must not depend on the outer Application layer.";
    private const string ApiReason = "Console I/O must be abstracted behind an Infrastructure adapter.";

    private const string DependencyExceptionContract = """
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
              "reason": "Domain must not depend on the outer Application layer."
            }
          ],
          "exceptions": [
            {
              "diagnosticId": "AARC002",
              "sourceType": "Sample.Domain.LegacyBridge",
              "targetType": "Sample.Application.AppService",
              "justification": "Temporary compatibility bridge tracked by ARCH-123."
            }
          ]
        }
        """;

    private const string ApiExceptionContract = """
        {
          "schemaVersion": 5,
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
          ],
          "forbiddenApis": [
            {
              "layer": "Domain",
              "type": "System.Console",
              "reason": "Console I/O must be abstracted behind an Infrastructure adapter."
            }
          ],
          "exceptions": [
            {
              "diagnosticId": "AARC003",
              "sourceType": "Sample.Domain.LegacyConsole",
              "apiType": "System.Console",
              "member": "WriteLine",
              "justification": "Legacy diagnostics path tracked by ARCH-456."
            }
          ]
        }
        """;

    [Fact]
    public async Task Aarc002_ExactDependencyException_IsSilent()
    {
        var test = new ArchitectureAnalyzerTest(DependencyExceptionContract)
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
                    public class LegacyBridge
                    {
                        public Sample.Application.AppService Service { get; set; }
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task Aarc002_DifferentSourceType_RemainsViolation()
    {
        var test = new ArchitectureAnalyzerTest(DependencyExceptionContract)
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
            DependencyReason));

        await test.RunAsync();
    }

    [Fact]
    public async Task Aarc002_DifferentTargetType_RemainsViolation()
    {
        var test = new ArchitectureAnalyzerTest(DependencyExceptionContract)
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
                    public class LegacyBridge
                    {
                        public Sample.Application.OtherService Service { get; set; }
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenLayerDependency,
            16,
            35,
            "Sample.Domain.LegacyBridge",
            "Domain",
            "Sample.Application.OtherService",
            "Application",
            DependencyReason));

        await test.RunAsync();
    }

    [Fact]
    public async Task Aarc003_ExactSourceApiMemberException_IsSilent()
    {
        var test = new ArchitectureAnalyzerTest(ApiExceptionContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class LegacyConsole
                    {
                        public void Run()
                        {
                            System.Console.WriteLine("hello");
                        }
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task Aarc003_DifferentSourceType_RemainsViolation()
    {
        var test = new ArchitectureAnalyzerTest(ApiExceptionContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainEntity
                    {
                        public void Run()
                        {
                            System.Console.WriteLine("hello");
                        }
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenApiUsage,
            7,
            13,
            "Console.WriteLine",
            "Domain",
            ApiReason));

        await test.RunAsync();
    }

    [Fact]
    public async Task Aarc003_DifferentMember_RemainsViolation()
    {
        var test = new ArchitectureAnalyzerTest(ApiExceptionContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class LegacyConsole
                    {
                        public string? Run()
                        {
                            return System.Console.ReadLine();
                        }
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenApiUsage,
            7,
            20,
            "Console.ReadLine",
            "Domain",
            ApiReason));

        await test.RunAsync();
    }

    [Fact]
    public async Task StandardPragmaSuppression_RemainsAvailable()
    {
        var test = new ArchitectureAnalyzerTest(DependencyExceptionContract)
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
                #pragma warning disable AARC002
                        public Sample.Application.AppService Service { get; set; }
                #pragma warning restore AARC002
                    }
                }
                """,
        };

        await test.RunAsync();
    }
}
