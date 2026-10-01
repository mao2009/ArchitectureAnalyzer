using ArchitectureAnalyzer.Diagnostics;

namespace ArchitectureAnalyzer.Tests;

/// <summary>
/// Schema-v4 declared dependency-graph cycle enforcement (AARC011).
/// </summary>
public sealed class DependencyCycleTests
{
    [Fact]
    public async Task RequireAcyclicFalse_CyclicPolicy_IsSilent()
    {
        const string contract = """
            {
              "schemaVersion": 4,
              "layers": [
                { "name": "A", "namespaceRoots": [ "Sample.A" ] },
                { "name": "B", "namespaceRoots": [ "Sample.B" ] }
              ],
              "allowedDependencies": [
                { "from": "A", "to": [ "B" ] },
                { "from": "B", "to": [ "A" ] }
              ],
              "dependencyGraph": {
                "requireAcyclic": false
              }
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = "namespace Sample.A { public class AType { } }",
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task RequireAcyclicTrue_AcyclicPolicy_IsSilent()
    {
        const string contract = """
            {
              "schemaVersion": 4,
              "layers": [
                { "name": "A", "namespaceRoots": [ "Sample.A" ] },
                { "name": "B", "namespaceRoots": [ "Sample.B" ] },
                { "name": "C", "namespaceRoots": [ "Sample.C" ] }
              ],
              "allowedDependencies": [
                { "from": "A", "to": [ "B" ] },
                { "from": "B", "to": [ "C" ] },
                { "from": "C", "to": [] }
              ],
              "dependencyGraph": {
                "requireAcyclic": true
              }
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = "namespace Sample.A { public class AType { } }",
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task RequireAcyclicTrue_TwoNodeCycle_ReportsCanonicalPath()
    {
        const string contract = """
            {
              "schemaVersion": 4,
              "layers": [
                { "name": "B", "namespaceRoots": [ "Sample.B" ] },
                { "name": "A", "namespaceRoots": [ "Sample.A" ] }
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

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = "namespace Sample.A { public class AType { } }",
        };
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.DeclaredDependencyCycle,
            "A -> B -> A"));

        await test.RunAsync();
    }

    [Fact]
    public async Task RequireAcyclicTrue_ExplicitSelfEdge_ReportsSelfCycle()
    {
        const string contract = """
            {
              "schemaVersion": 4,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ],
              "allowedDependencies": [
                { "from": "Domain", "to": [ "Domain" ] }
              ],
              "dependencyGraph": {
                "requireAcyclic": true
              }
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = "namespace Sample.Domain { public class Entity { } }",
        };
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.DeclaredDependencyCycle,
            "Domain -> Domain"));

        await test.RunAsync();
    }

    [Fact]
    public async Task RequireAcyclicTrue_MultipleCyclicComponents_ReportOneCycleEachInStableOrder()
    {
        const string contract = """
            {
              "schemaVersion": 4,
              "layers": [
                { "name": "E", "namespaceRoots": [ "Sample.E" ] },
                { "name": "D", "namespaceRoots": [ "Sample.D" ] },
                { "name": "C", "namespaceRoots": [ "Sample.C" ] },
                { "name": "B", "namespaceRoots": [ "Sample.B" ] },
                { "name": "A", "namespaceRoots": [ "Sample.A" ] }
              ],
              "allowedDependencies": [
                { "from": "E", "to": [ "C" ] },
                { "from": "D", "to": [ "E" ] },
                { "from": "C", "to": [ "D" ] },
                { "from": "B", "to": [ "A" ] },
                { "from": "A", "to": [ "B" ] }
              ],
              "dependencyGraph": {
                "requireAcyclic": true
              }
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = "namespace Sample.A { public class AType { } }",
        };
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.DeclaredDependencyCycle,
            "A -> B -> A"));
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.DeclaredDependencyCycle,
            "C -> D -> E -> C"));

        await test.RunAsync();
    }

    [Fact]
    public async Task RequireAcyclicTrue_MultipleCyclesInsideOneComponent_ReportOneCanonicalCycle()
    {
        const string contract = """
            {
              "schemaVersion": 4,
              "layers": [
                { "name": "C", "namespaceRoots": [ "Sample.C" ] },
                { "name": "B", "namespaceRoots": [ "Sample.B" ] },
                { "name": "A", "namespaceRoots": [ "Sample.A" ] }
              ],
              "allowedDependencies": [
                { "from": "A", "to": [ "C", "B" ] },
                { "from": "B", "to": [ "A" ] },
                { "from": "C", "to": [ "A" ] }
              ],
              "dependencyGraph": {
                "requireAcyclic": true
              }
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = "namespace Sample.A { public class AType { } }",
        };
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.DeclaredDependencyCycle,
            "A -> B -> A"));

        await test.RunAsync();
    }

    [Fact]
    public async Task RequireAcyclicTrue_PartialAllowlistStillChecksExplicitCycle()
    {
        const string contract = """
            {
              "schemaVersion": 4,
              "layers": [
                { "name": "A", "namespaceRoots": [ "Sample.A" ] },
                { "name": "B", "namespaceRoots": [ "Sample.B" ] },
                { "name": "Unlisted", "namespaceRoots": [ "Sample.Unlisted" ] }
              ],
              "allowedDependencies": [
                { "from": "A", "to": [ "B" ] },
                { "from": "B", "to": [ "A" ] }
              ],
              "dependencyGraph": {
                "requireAcyclic": true
              }
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = "namespace Sample.Unlisted { public class Tool { } }",
        };
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.DeclaredDependencyCycle,
            "A -> B -> A"));

        await test.RunAsync();
    }

    [Fact]
    public async Task ForbiddenDependencies_DoNotCreatePositiveGraphEdges()
    {
        const string contract = """
            {
              "schemaVersion": 4,
              "layers": [
                { "name": "A", "namespaceRoots": [ "Sample.A" ] },
                { "name": "B", "namespaceRoots": [ "Sample.B" ] }
              ],
              "allowedDependencies": [
                { "from": "A", "to": [ "B" ] }
              ],
              "forbiddenDependencies": [
                { "from": "B", "to": "A" }
              ],
              "dependencyGraph": {
                "requireAcyclic": true
              }
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = "namespace Sample.A { public class AType { } }",
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task MalformedDependencyGraph_ReportsAarc001InsteadOfAarc011()
    {
        const string contract = """
            {
              "schemaVersion": 4,
              "layers": [
                { "name": "A", "namespaceRoots": [ "Sample.A" ] },
                { "name": "B", "namespaceRoots": [ "Sample.B" ] }
              ],
              "allowedDependencies": [
                { "from": "A", "to": [ "B" ] },
                { "from": "B", "to": [ "A" ] }
              ],
              "dependencyGraph": {
                "requireAcyclic": "yes"
              }
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = "namespace Sample.A { public class AType { } }",
        };
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.ArchitectureContractInvalid,
            ArchitectureContractAnalyzer.ContractFileName,
            "property 'requireAcyclic' of dependencyGraph must be a boolean when present"));

        await test.RunAsync();
    }
}
