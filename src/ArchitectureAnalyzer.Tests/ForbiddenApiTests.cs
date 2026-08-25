using ArchitectureAnalyzer.Diagnostics;

namespace ArchitectureAnalyzer.Tests;

/// <summary>
/// Forbidden-API enforcement (AARC003) driven entirely by the supplied contract.
/// </summary>
public sealed class ForbiddenApiTests
{
    private const string ConsoleReason = "Console I/O must be abstracted behind an Infrastructure adapter.";

    private const string RandomReason = "Non-deterministic randomness is forbidden in the Domain layer.";

    private const string TimeReason = "Non-deterministic time source is forbidden in the Domain layer.";

    private const string Contract = """
        {
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
          ],
          "forbiddenApis": [
            { "layer": "Domain", "type": "System.Console", "reason": "Console I/O must be abstracted behind an Infrastructure adapter." },
            { "layer": "Domain", "type": "System.Random", "wholeType": true, "reason": "Non-deterministic randomness is forbidden in the Domain layer." },
            { "layer": "Domain", "type": "System.DateTime", "member": "Now", "reason": "Non-deterministic time source is forbidden in the Domain layer." }
          ]
        }
        """;

    [Fact]
    public async Task DomainCallingForbiddenAnyMemberApi_ReportsForbiddenApiUsage()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
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
            ConsoleReason));

        await test.RunAsync();
    }

    [Fact]
    public async Task DomainConstructingWholeTypeForbiddenApi_ReportsForbiddenApiUsage()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainEntity
                    {
                        public object Roll()
                        {
                            return new System.Random();
                        }
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenApiUsage,
            7,
            20,
            "new Random()",
            "Domain",
            RandomReason));

        await test.RunAsync();
    }

    [Fact]
    public async Task DomainReadingForbiddenNamedMember_ReportsForbiddenApiUsage()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainEntity
                    {
                        public System.DateTime Stamp()
                        {
                            return System.DateTime.Now;
                        }
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenApiUsage,
            7,
            20,
            "DateTime.Now",
            "Domain",
            TimeReason));

        await test.RunAsync();
    }

    [Fact]
    public async Task DomainReadingAllowedMemberOfPartiallyForbiddenType_IsAllowed()
    {
        // Only DateTime.Now is listed; DateTime.UnixEpoch must stay usable.
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainEntity
                    {
                        public System.DateTime Epoch()
                        {
                            return System.DateTime.UnixEpoch;
                        }
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task DomainCallingAllowedApi_ReportsNothing()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainEntity
                    {
                        public string Normalize(string value)
                        {
                            return value.Trim();
                        }
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task UnclassifiedNamespaceUsingForbiddenApi_IsAllowed()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                namespace Sample.Tooling
                {
                    public class Helper
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
}
