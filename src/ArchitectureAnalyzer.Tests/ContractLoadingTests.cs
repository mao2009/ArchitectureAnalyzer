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
    public async Task MultipleContractFiles_ReportAmbiguousConfigurationInsteadOfChoosingOne()
    {
        var test = new ArchitectureAnalyzerTest
        {
            TestCode = CleanSource,
        };
        test.TestState.AdditionalFiles.Add(("z/architecture.contract.json", ValidContract));
        test.TestState.AdditionalFiles.Add(("a/ARCHITECTURE.CONTRACT.JSON", ValidContract));

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.ArchitectureContractInvalid,
            ArchitectureContractAnalyzer.ContractFileName,
            "multiple architecture contract files were supplied: "
                + "a/ARCHITECTURE.CONTRACT.JSON, z/architecture.contract.json; "
                + "include exactly one 'architecture.contract.json' AdditionalFiles item"));

        await test.RunAsync();
    }

    [Fact]
    public async Task MultipleContractFiles_WithMixedPathSeparators_AreStillDetected()
    {
        var test = new ArchitectureAnalyzerTest
        {
            TestCode = CleanSource,
        };
        test.TestState.AdditionalFiles.Add(("z\\architecture.contract.json", ValidContract));
        test.TestState.AdditionalFiles.Add(("a/architecture.contract.json", ValidContract));

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.ArchitectureContractInvalid,
            ArchitectureContractAnalyzer.ContractFileName,
            "multiple architecture contract files were supplied: "
                + "a/architecture.contract.json, z/architecture.contract.json; "
                + "include exactly one 'architecture.contract.json' AdditionalFiles item"));

        await test.RunAsync();
    }

    [Fact]
    public async Task MultipleContractFiles_ContractRequiredFalse_IsSilentNoOp()
    {
        var test = new ArchitectureAnalyzerTest
        {
            TestCode = CleanSource,
        };
        test.TestState.AdditionalFiles.Add(("z/architecture.contract.json", ValidContract));
        test.TestState.AdditionalFiles.Add(("a/architecture.contract.json", ValidContract));
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            [*.cs]
            dotnet_diagnostic.AARC001.architecture_analyzer.contract_required = false
            """));

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
              "schemaVersion": 1,
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
    public void Loader_VersionlessContract_RemainsSchemaV1Compatible()
    {
        var result = ArchitectureContractLoader.Load(ValidContract);

        Assert.True(result.Succeeded, result.ErrorReason);
    }

    [Fact]
    public void Loader_SchemaVersionOne_Succeeds()
    {
        const string contract = """
            {
              "schemaVersion": 1,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.True(result.Succeeded, result.ErrorReason);
    }

    [Fact]
    public void Loader_UnsupportedSchemaVersion_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 2,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("unsupported schemaVersion '2'; supported schemaVersion is 1", result.ErrorReason);
    }

    [Fact]
    public async Task UnsupportedSchemaVersion_ReportsContractInvalid()
    {
        const string contract = """
            {
              "schemaVersion": 2,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
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
            "unsupported schemaVersion '2'; supported schemaVersion is 1"));

        await test.RunAsync();
    }

    [Theory]
    [InlineData(""1"")]
    [InlineData("null")]
    [InlineData("1.5")]
    [InlineData("true")]
    public void Loader_InvalidSchemaVersionType_Fails(string versionJson)
    {
        var contract = $"""
            {
              "schemaVersion": {{versionJson}},
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'schemaVersion' must be an integer when present", result.ErrorReason);
    }

    [Fact]
    public void Loader_DuplicateNamespaceRoot_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 1,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Shared" ] },
                { "name": "Application", "namespaceRoots": [ "Sample.Shared" ] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("namespace root 'Sample.Shared' is declared more than once in layers", result.ErrorReason);
    }

    [Fact]
    public void Loader_DuplicateForbiddenDependency_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 1,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
                { "name": "Application", "namespaceRoots": [ "Sample.Application" ] }
              ],
              "forbiddenDependencies": [
                { "from": "Domain", "to": "Application" },
                { "from": "Domain", "to": "Application", "reason": "duplicate" }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("forbidden dependency 'Domain' -> 'Application' is declared more than once", result.ErrorReason);
    }

    [Fact]
    public void Loader_ConflictingInteropAllowedLayers_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 1,
              "layers": [
                { "name": "NativeA", "namespaceRoots": [ "Sample.NativeA" ] },
                { "name": "NativeB", "namespaceRoots": [ "Sample.NativeB" ] }
              ],
              "interopBoundaryRules": [
                {
                  "attribute": "System.Runtime.InteropServices.DllImportAttribute",
                  "allowedLayer": "NativeA"
                },
                {
                  "attribute": "System.Runtime.InteropServices.DllImportAttribute",
                  "allowedLayer": "NativeB"
                }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "interop attribute 'System.Runtime.InteropServices.DllImportAttribute' has conflicting allowed layers "
                + "'NativeA' and 'NativeB'",
            result.ErrorReason);
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
