using ArchitectureAnalyzer.Contract;
using ArchitectureAnalyzer.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;

namespace ArchitectureAnalyzer.Tests;

/// <summary>
/// Contract discovery, parsing and schema-validation behaviour (AARC001), plus direct unit tests
/// of the Roslyn-free <see cref="ArchitectureContractLoader"/>.
/// </summary>
public sealed class ContractLoadingTests
{
    private const string ValidContract = """
        {
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
            { "name": "Application", "namespaceRoots": [ "Sample.Application" ] }
          ],
          "forbiddenDependencies": [
            { "from": "Domain", "to": "Application", "reason": "Domain must not depend on the outer Application layer." }
          ],
          "forbiddenApis": [
            { "layer": "Domain", "type": "System.Console", "reason": "Console I/O must be abstracted behind an Infrastructure adapter." }
          ]
        }
        """;

    private const string CleanSource = """
        namespace Sample.Application
        {
            public class AppService
            {
                public Sample.Domain.DomainEntity Entity { get; set; }
            }
        }

        namespace Sample.Domain
        {
            public class DomainEntity
            {
                public string Name { get; set; }
            }
        }
        """;

    [Fact]
    public async Task ValidContractWithNoViolations_ReportsNothing()
    {
        var test = new ArchitectureAnalyzerTest(ValidContract)
        {
            TestCode = CleanSource,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task NoContractFile_IsSilentNoOp()
    {
        // Even code that would violate the sample contract is invisible without a contract file:
        // enforcement is opt-in per consuming project.
        var test = new ArchitectureAnalyzerTest
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainEntity
                    {
                        public Sample.Application.AppService Service { get; set; }

                        public void Run() => System.Console.WriteLine("hello");
                    }
                }

                namespace Sample.Application
                {
                    public class AppService
                    {
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task MalformedJson_ReportsContractInvalid()
    {
        var test = new ArchitectureAnalyzerTest("{ \"layers\": [ ")
        {
            TestCode = CleanSource,
        };

        // The reason argument is the raw System.Text.Json parse message, which is localized and
        // version-dependent, so only the diagnostic identity is asserted here.
        test.ExpectedDiagnostics.Add(
            new DiagnosticResult("AARC001", DiagnosticSeverity.Error).WithNoLocation());

        await test.RunAsync();
    }

    [Fact]
    public async Task ForbiddenDependencyReferencingUndeclaredLayer_ReportsContractInvalid()
    {
        const string contract = """
            {
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ],
              "forbiddenDependencies": [
                { "from": "Domain", "to": "Application", "reason": "Domain must not depend on the outer Application layer." }
              ]
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = CleanSource,
        };

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.ArchitectureContractInvalid,
            ArchitectureContractAnalyzer.ContractFileName,
            "layer 'Application' referenced in forbiddenDependencies is not declared in layers"));

        await test.RunAsync();
    }

    [Fact]
    public async Task ForbiddenApiReferencingUndeclaredLayer_ReportsContractInvalid()
    {
        const string contract = """
            {
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ],
              "forbiddenApis": [
                { "layer": "Infrastructure", "type": "System.Console", "reason": "no console here" }
              ]
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = CleanSource,
        };

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.ArchitectureContractInvalid,
            ArchitectureContractAnalyzer.ContractFileName,
            "layer 'Infrastructure' referenced in forbiddenApis is not declared in layers"));

        await test.RunAsync();
    }

    [Fact]
    public void Loader_ValidJson_ProducesContract()
    {
        var result = ArchitectureContractLoader.Load(ValidContract);

        Assert.True(result.Succeeded, result.ErrorReason);
        var contract = result.Contract!;
        Assert.Equal(2, contract.Layers.Length);
        Assert.Equal("Domain", contract.ResolveLayer("Sample.Domain.Orders"));
        Assert.Equal("Application", contract.ResolveLayer("Sample.Application"));
        Assert.Null(contract.ResolveLayer("Sample.Infrastructure"));
        Assert.True(contract.IsForbiddenDependency("Domain", "Application", out var reason));
        Assert.Equal("Domain must not depend on the outer Application layer.", reason);
        Assert.False(contract.IsForbiddenDependency("Application", "Domain", out _));
        Assert.Single(contract.GetApiRules("Domain"));
        Assert.Empty(contract.GetApiRules("Application"));
    }

    [Fact]
    public void Loader_MalformedJson_Fails()
    {
        var result = ArchitectureContractLoader.Load("{ \"layers\": [ ");

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorReason));
    }

    [Fact]
    public void Loader_MissingLayers_Fails()
    {
        var result = ArchitectureContractLoader.Load("{ }");

        Assert.False(result.Succeeded);
        Assert.Equal("required property 'layers' is missing", result.ErrorReason);
    }

    [Fact]
    public void Loader_NullText_ReportsFileNotFound()
    {
        var result = ArchitectureContractLoader.Load(null);

        Assert.False(result.Succeeded);
        Assert.Equal("file not found", result.ErrorReason);
    }

    [Fact]
    public void Loader_UnknownProperties_AreIgnored()
    {
        const string contract = """
            {
              "schemaVersion": "99",
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ], "color": "red" }
              ],
              "somethingFromTheFuture": { "nested": true }
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.True(result.Succeeded, result.ErrorReason);
        Assert.Single(result.Contract!.Layers);
    }

    [Fact]
    public void Loader_LongestNamespaceRootWins()
    {
        const string contract = """
            {
              "layers": [
                { "name": "Outer", "namespaceRoots": [ "Sample" ] },
                { "name": "Inner", "namespaceRoots": [ "Sample.Domain.Orders" ] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.True(result.Succeeded, result.ErrorReason);
        var parsed = result.Contract!;
        Assert.Equal("Outer", parsed.ResolveLayer("Sample.Domain"));
        Assert.Equal("Inner", parsed.ResolveLayer("Sample.Domain.Orders"));
        Assert.Equal("Inner", parsed.ResolveLayer("Sample.Domain.Orders.Pricing"));
        Assert.Null(parsed.ResolveLayer("SampleOther"));
    }
}
