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
    public void Loader_MetadataPrefixedProperties_AreTolerated()
    {
        const string contract = """
            {
              "$schema": "https://example.invalid/contract.schema.json",
              "schemaVersion": 1,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ], "x-color": "red" }
              ],
              "x-somethingFromTheFuture": { "nested": true }
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.True(result.Succeeded, result.ErrorReason);
        Assert.Single(result.Contract!.Layers);
    }

    [Theory]
    [InlineData(
        """{ "layers": [], "somethingFromTheFuture": { "nested": true } }""",
        "unknown property 'somethingFromTheFuture' at $.somethingFromTheFuture (line 1, column 17); prefix metadata keys with '$' or 'x-'")]
    [InlineData(
        """{ "layers": [ { "name": "Domain", "namespaceRoots": [ "A.Domain" ], "color": "red" } ] }""",
        "unknown property 'color' at $.layers[0].color (line 1, column 69); prefix metadata keys with '$' or 'x-'")]
    [InlineData(
        """{ "schemaVersion": 4, "layers": [], "dependencyGraph": { "requireAcylic": true } }""",
        "unknown property 'requireAcylic' at $.dependencyGraph.requireAcylic (line 1, column 58); did you mean 'requireAcyclic'?")]
    [InlineData(
        """{ "layers": [], "forbiddenDependenices": [ { "from": "A", "to": "B" } ] }""",
        "unknown property 'forbiddenDependenices' at $.forbiddenDependenices (line 1, column 17); did you mean 'forbiddenDependencies'?")]
    [InlineData(
        """{ "layers": [ { "name": "Domain", "namespaceRoot": [ "A.Domain" ] } ] }""",
        "unknown property 'namespaceRoot' at $.layers[0].namespaceRoot (line 1, column 35); did you mean 'namespaceRoots'?")]
    [InlineData(
        """{ "layers": [ { "name": "A" }, { "name": "B" } ], "forbiddenDependencies": [ { "from": "A", "tos": "B" } ] }""",
        "unknown property 'tos' at $.forbiddenDependencies[0].tos (line 1, column 93); did you mean 'to'?")]
    [InlineData(
        """{ "layers": [ { "name": "A" } ], "layerDeclaration": { "markerAttributes": [ { "attributeFqn": "X", "Layer": "A" } ] } }""",
        "unknown property 'Layer' at $.layerDeclaration.markerAttributes[0].Layer (line 1, column 101); did you mean 'layer'?")]
    public void Loader_UnknownProperty_FailsWithPathPositionAndSuggestion(string contract, string expected)
    {
        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.ErrorReason);
    }

    [Fact]
    public void Loader_UnknownProperty_ReportsMultiLinePosition()
    {
        var result = ArchitectureContractLoader.Load("{\n  \"layers\": [],\n  // comment\n  \"layer\": []\n}");

        Assert.Equal(
            "unknown property 'layer' at $.layer (line 4, column 3); did you mean 'layers'?",
            result.ErrorReason);
    }

    [Fact]
    public void Loader_UnknownPropertyWithUnsupportedSchemaVersion_ReportsVersionFirst()
    {
        var result = ArchitectureContractLoader.Load("""{ "schemaVersion": 99, "layers": [], "typo": 1 }""");

        Assert.Equal(
            "unsupported schemaVersion '99'; supported schemaVersions are 1 through 5",
            result.ErrorReason);
    }

    [Fact]
    public void Loader_KnownPropertyFromNewerSchemaVersion_ReportsVersionRequirementNotUnknown()
    {
        var result = ArchitectureContractLoader.Load(
            """{ "schemaVersion": 3, "layers": [], "dependencyGraph": { "requireAcyclic": true } }""");

        Assert.Equal("property 'dependencyGraph' requires schemaVersion 4", result.ErrorReason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Loader_EveryKnownPropertyForSchemaVersion_Succeeds(int version)
    {
        var contract = $$"""
            {
              "schemaVersion": {{version}},
              {{(version >= 2 ? "\"unclassifiedCode\": \"error\"," : "")}}
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
                { "name": "Application", "namespaceRoots": [ "Sample.Application" ] }
              ],
              "forbiddenDependencies": [ { "from": "Domain", "to": "Application", "reason": "r" } ],
              {{(version >= 3 ? "\"allowedDependencies\": [ { \"from\": \"Application\", \"to\": [ \"Domain\" ], \"reason\": \"r\" } ]," : "")}}
              {{(version >= 4 ? "\"dependencyGraph\": { \"requireAcyclic\": true }," : "")}}
              {{(version >= 5 ? "\"exceptions\": [ { \"diagnosticId\": \"AARC002\", \"sourceType\": \"S\", \"targetType\": \"T\", \"justification\": \"j\" }, { \"diagnosticId\": \"AARC003\", \"sourceType\": \"S\", \"apiType\": \"A\", \"member\": \"M\", \"justification\": \"j\" } ]," : "")}}
              "forbiddenApis": [ { "layer": "Domain", "type": "System.Console", "member": "WriteLine", "wholeType": false, "reason": "r" } ],
              "layerDeclaration": {
                "required": true,
                "validateNamespaceConsistency": true,
                "markerNamespace": "Sample.Markers",
                "markerAttributes": [ { "attributeFqn": "Sample.Markers.DomainAttribute", "layer": "Domain" } ]
              },
              "interopBoundaryRules": [ { "attribute": "System.Runtime.InteropServices.DllImportAttribute", "allowedLayer": "Domain", "reason": "r" } ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.True(result.Succeeded, result.ErrorReason);
    }

    [Fact]
    public async Task UnknownContractProperty_ReportsContractInvalid()
    {
        var test = new ArchitectureAnalyzerTest(
            """{ "layers": [], "forbiddenDependenices": [] }""")
        {
            TestCode = CleanSource,
        };

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.ArchitectureContractInvalid,
            ArchitectureContractAnalyzer.ContractFileName,
            "unknown property 'forbiddenDependenices' at $.forbiddenDependenices (line 1, column 17); "
                + "did you mean 'forbiddenDependencies'?"));

        await test.RunAsync();
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
    public void Loader_SchemaVersionTwo_Succeeds()
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

        Assert.True(result.Succeeded, result.ErrorReason);
    }

    [Fact]
    public void Loader_SchemaVersionThree_Succeeds()
    {
        const string contract = """
            {
              "schemaVersion": 3,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.True(result.Succeeded, result.ErrorReason);
    }

    [Fact]
    public void Loader_SchemaVersionFour_Succeeds()
    {
        const string contract = """
            {
              "schemaVersion": 4,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.True(result.Succeeded, result.ErrorReason);
    }

    [Fact]
    public void Loader_SchemaVersionFive_Succeeds()
    {
        const string contract = """
            {
              "schemaVersion": 5,
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
              "schemaVersion": 6,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "unsupported schemaVersion '6'; supported schemaVersions are 1 through 5",
            result.ErrorReason);
    }

    [Fact]
    public async Task UnsupportedSchemaVersion_ReportsContractInvalid()
    {
        const string contract = """
            {
              "schemaVersion": 6,
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
            "unsupported schemaVersion '6'; supported schemaVersions are 1 through 5"));

        await test.RunAsync();
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("null")]
    [InlineData("1.5")]
    [InlineData("true")]
    public void Loader_InvalidSchemaVersionType_Fails(string versionJson)
    {
        var contract = "{ \"schemaVersion\": " + versionJson
            + ", \"layers\": [{ \"name\": \"Domain\", \"namespaceRoots\": [\"Sample.Domain\"] }] }";

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'schemaVersion' must be an integer when present", result.ErrorReason);
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 2, \"schemaVersion\": 1, \"layers\": [] }")]
    [InlineData("{ \"schemaVersion\": 1, \"schemaVersion\": 2, \"layers\": [] }")]
    public void Loader_DuplicateSchemaVersion_FailsRegardlessOfOrder(string contract)
    {
        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'schemaVersion' must not appear more than once", result.ErrorReason);
    }

    [Fact]
    public async Task DuplicateSchemaVersion_ReportsContractInvalid()
    {
        const string contract = """
            {
              "schemaVersion": 2,
              "schemaVersion": 1,
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
            "property 'schemaVersion' must not appear more than once"));

        await test.RunAsync();
    }

    [Fact]
    public void Loader_UnclassifiedCodeError_RequiresSchemaV2()
    {
        const string contract = """
            {
              "schemaVersion": 1,
              "unclassifiedCode": "error",
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'unclassifiedCode' requires schemaVersion 2", result.ErrorReason);
    }

    [Theory]
    [InlineData("strict")]
    [InlineData("ERROR")]
    public void Loader_InvalidUnclassifiedCodeString_Fails(string policy)
    {
        var contract = "{ \"schemaVersion\": 2, \"unclassifiedCode\": \"" + policy
            + "\", \"layers\": [{ \"name\": \"Domain\", \"namespaceRoots\": [\"Sample.Domain\"] }] }";

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'unclassifiedCode' must be 'ignore' or 'error'", result.ErrorReason);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("null")]
    public void Loader_NonStringUnclassifiedCode_Fails(string policyJson)
    {
        var contract = "{ \"schemaVersion\": 2, \"unclassifiedCode\": " + policyJson
            + ", \"layers\": [{ \"name\": \"Domain\", \"namespaceRoots\": [\"Sample.Domain\"] }] }";

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'unclassifiedCode' must be 'ignore' or 'error'", result.ErrorReason);
    }

    [Fact]
    public void Loader_UnclassifiedCodePolicies_AreParsed()
    {
        const string ignoreContract = """
            {
              "schemaVersion": 2,
              "unclassifiedCode": "ignore",
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ]
            }
            """;
        const string errorContract = """
            {
              "schemaVersion": 2,
              "unclassifiedCode": "error",
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ]
            }
            """;

        var ignored = ArchitectureContractLoader.Load(ignoreContract);
        var strict = ArchitectureContractLoader.Load(errorContract);

        Assert.True(ignored.Succeeded, ignored.ErrorReason);
        Assert.Equal(UnclassifiedCodePolicy.Ignore, ignored.Contract!.UnclassifiedCode);
        Assert.True(strict.Succeeded, strict.ErrorReason);
        Assert.Equal(UnclassifiedCodePolicy.Error, strict.Contract!.UnclassifiedCode);
    }

    [Fact]
    public void Loader_DuplicateUnclassifiedCode_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 2,
              "unclassifiedCode": "ignore",
              "unclassifiedCode": "error",
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'unclassifiedCode' must not appear more than once", result.ErrorReason);
    }

    [Fact]
    public void Loader_AllowedDependencies_RequiresSchemaV3()
    {
        const string contract = """
            {
              "schemaVersion": 2,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
                { "name": "Shared", "namespaceRoots": [ "Sample.Shared" ] }
              ],
              "allowedDependencies": [
                { "from": "Domain", "to": [ "Shared" ] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'allowedDependencies' requires schemaVersion 3", result.ErrorReason);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("true")]
    public void Loader_AllowedDependencies_MustBeArray(string sectionJson)
    {
        var contract = "{ \"schemaVersion\": 3, \"layers\": [], \"allowedDependencies\": "
            + sectionJson + " }";

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'allowedDependencies' must be a JSON array", result.ErrorReason);
    }

    [Fact]
    public void Loader_DuplicateAllowedDependenciesProperty_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 3,
              "layers": [],
              "allowedDependencies": [],
              "allowedDependencies": []
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'allowedDependencies' must not appear more than once", result.ErrorReason);
    }

    [Fact]
    public void Loader_AllowedDependencies_ParsesEmptyAndPopulatedTargets()
    {
        const string contract = """
            {
              "schemaVersion": 3,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
                { "name": "Application", "namespaceRoots": [ "Sample.Application" ] },
                { "name": "Shared", "namespaceRoots": [ "Sample.Shared" ] }
              ],
              "allowedDependencies": [
                { "from": "Domain", "to": [ "Shared" ], "reason": "Domain may depend only on Shared." },
                { "from": "Shared", "to": [] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.True(result.Succeeded, result.ErrorReason);
        Assert.Equal(2, result.Contract!.AllowedDependencies.Length);
        Assert.Equal("Domain", result.Contract.AllowedDependencies[0].From);
        Assert.Equal(new[] { "Shared" }, result.Contract.AllowedDependencies[0].Targets);
        Assert.Empty(result.Contract.AllowedDependencies[1].Targets);
    }

    [Fact]
    public void Loader_DuplicateAllowedDependencySource_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 3,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
                { "name": "Shared", "namespaceRoots": [ "Sample.Shared" ] }
              ],
              "allowedDependencies": [
                { "from": "Domain", "to": [ "Shared" ] },
                { "from": "Domain", "to": [] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("allowed dependency source 'Domain' is declared more than once", result.ErrorReason);
    }

    [Fact]
    public void Loader_DuplicateAllowedDependencyTarget_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 3,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
                { "name": "Shared", "namespaceRoots": [ "Sample.Shared" ] }
              ],
              "allowedDependencies": [
                { "from": "Domain", "to": [ "Shared", "Shared" ] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("allowed dependency 'Domain' -> 'Shared' is declared more than once", result.ErrorReason);
    }

    [Fact]
    public void Loader_AllowedDependencyUndeclaredSource_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 3,
              "layers": [
                { "name": "Shared", "namespaceRoots": [ "Sample.Shared" ] }
              ],
              "allowedDependencies": [
                { "from": "Domain", "to": [ "Shared" ] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("layer 'Domain' referenced in allowedDependencies is not declared in layers", result.ErrorReason);
    }

    [Fact]
    public void Loader_AllowedDependencyUndeclaredTarget_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 3,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ],
              "allowedDependencies": [
                { "from": "Domain", "to": [ "Shared" ] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("layer 'Shared' referenced in allowedDependencies is not declared in layers", result.ErrorReason);
    }

    [Fact]
    public void Loader_AllowedDependencyMissingTargets_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 3,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ],
              "allowedDependencies": [
                { "from": "Domain" }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'to' of allowedDependencies source 'Domain' must be a JSON array", result.ErrorReason);
    }

    [Fact]
    public void Loader_AllowedAndForbiddenSameEdge_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 3,
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
                { "name": "Shared", "namespaceRoots": [ "Sample.Shared" ] }
              ],
              "forbiddenDependencies": [
                { "from": "Domain", "to": "Shared" }
              ],
              "allowedDependencies": [
                { "from": "Domain", "to": [ "Shared" ] }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("dependency 'Domain' -> 'Shared' is declared as both allowed and forbidden", result.ErrorReason);
    }

    [Fact]
    public void Loader_DependencyGraph_RequiresSchemaV4()
    {
        const string contract = """
            {
              "schemaVersion": 3,
              "layers": [],
              "dependencyGraph": {
                "requireAcyclic": true
              }
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'dependencyGraph' requires schemaVersion 4", result.ErrorReason);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    public void Loader_DependencyGraph_MustBeObject(string sectionJson)
    {
        var contract = "{ \"schemaVersion\": 4, \"layers\": [], \"dependencyGraph\": "
            + sectionJson + " }";

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'dependencyGraph' must be a JSON object", result.ErrorReason);
    }

    [Fact]
    public void Loader_DuplicateDependencyGraphProperty_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 4,
              "layers": [],
              "dependencyGraph": {},
              "dependencyGraph": {}
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'dependencyGraph' must not appear more than once", result.ErrorReason);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void Loader_DependencyGraphRequireAcyclic_MustBeBoolean(string valueJson)
    {
        var contract = "{ \"schemaVersion\": 4, \"layers\": [], \"dependencyGraph\": "
            + "{ \"requireAcyclic\": " + valueJson + " } }";

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "property 'requireAcyclic' of dependencyGraph must be a boolean when present",
            result.ErrorReason);
    }

    [Fact]
    public void Loader_DuplicateRequireAcyclic_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 4,
              "layers": [],
              "dependencyGraph": {
                "requireAcyclic": false,
                "requireAcyclic": true
              }
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "property 'requireAcyclic' must not appear more than once in dependencyGraph",
            result.ErrorReason);
    }

    [Fact]
    public void Loader_DependencyGraphPolicy_DefaultsFalseAndParsesTrue()
    {
        const string defaultContract = """
            {
              "schemaVersion": 4,
              "layers": [],
              "dependencyGraph": {}
            }
            """;
        const string strictContract = """
            {
              "schemaVersion": 4,
              "layers": [],
              "dependencyGraph": {
                "requireAcyclic": true
              }
            }
            """;

        var defaultResult = ArchitectureContractLoader.Load(defaultContract);
        var strictResult = ArchitectureContractLoader.Load(strictContract);

        Assert.True(defaultResult.Succeeded, defaultResult.ErrorReason);
        Assert.False(defaultResult.Contract!.DependencyGraph!.RequireAcyclic);
        Assert.True(strictResult.Succeeded, strictResult.ErrorReason);
        Assert.True(strictResult.Contract!.DependencyGraph!.RequireAcyclic);
    }

    [Fact]
    public void Loader_Exceptions_RequiresSchemaV5()
    {
        const string contract = """
            {
              "schemaVersion": 4,
              "layers": [],
              "exceptions": []
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'exceptions' requires schemaVersion 5", result.ErrorReason);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("true")]
    public void Loader_Exceptions_MustBeArray(string sectionJson)
    {
        var contract = "{ \"schemaVersion\": 5, \"layers\": [], \"exceptions\": "
            + sectionJson + " }";

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'exceptions' must be a JSON array", result.ErrorReason);
    }

    [Fact]
    public void Loader_DuplicateExceptionsProperty_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [],
              "exceptions": [],
              "exceptions": []
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("property 'exceptions' must not appear more than once", result.ErrorReason);
    }

    [Fact]
    public void Loader_ExceptionEntry_MustBeObject()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [],
              "exceptions": [ "AARC002" ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("each entry of 'exceptions' must be a JSON object", result.ErrorReason);
    }

    [Fact]
    public void Loader_ExceptionSourceType_IsRequired()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [],
              "exceptions": [
                {
                  "diagnosticId": "AARC002",
                  "targetType": "Sample.Application.LegacyService",
                  "justification": "Required."
                }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("in 'exceptions': required property 'sourceType' is missing", result.ErrorReason);
    }

    [Fact]
    public void Loader_Aarc002Exception_Parses()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [],
              "exceptions": [
                {
                  "diagnosticId": "AARC002",
                  "sourceType": "Sample.Domain.LegacyBridge",
                  "targetType": "Sample.Application.LegacyService",
                  "justification": "Tracked by ARCH-123."
                }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.True(result.Succeeded, result.ErrorReason);
        var exception = Assert.IsType<DependencyArchitectureException>(Assert.Single(result.Contract!.Exceptions));
        Assert.Equal("AARC002", exception.DiagnosticId);
        Assert.Equal("Sample.Domain.LegacyBridge", exception.SourceType);
        Assert.Equal("Sample.Application.LegacyService", exception.TargetType);
        Assert.Equal("Tracked by ARCH-123.", exception.Justification);
    }

    [Fact]
    public void Loader_Aarc003Exception_Parses()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [],
              "exceptions": [
                {
                  "diagnosticId": "AARC003",
                  "sourceType": "Sample.Domain.LegacyClock",
                  "apiType": "System.DateTime",
                  "member": "Now",
                  "justification": "Tracked by ARCH-456."
                }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.True(result.Succeeded, result.ErrorReason);
        var exception = Assert.IsType<ForbiddenApiArchitectureException>(Assert.Single(result.Contract!.Exceptions));
        Assert.Equal("AARC003", exception.DiagnosticId);
        Assert.Equal("Sample.Domain.LegacyClock", exception.SourceType);
        Assert.Equal("System.DateTime", exception.ApiType);
        Assert.Equal("Now", exception.Member);
        Assert.Equal("Tracked by ARCH-456.", exception.Justification);
    }

    [Theory]
    [InlineData("AARC001")]
    [InlineData("AARC010")]
    [InlineData("AARC011")]
    public void Loader_UnsupportedExceptionDiagnostic_Fails(string diagnosticId)
    {
        var contract = "{ \"schemaVersion\": 5, \"layers\": [], \"exceptions\": ["
            + "{ \"diagnosticId\": \"" + diagnosticId
            + "\", \"sourceType\": \"Sample.Type\", \"justification\": \"No.\" } ] }";

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal(
            $"exceptions diagnosticId '{diagnosticId}' is not supported; supported IDs are AARC002 and AARC003",
            result.ErrorReason);
    }

    [Fact]
    public void Loader_ExceptionJustification_IsRequired()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [],
              "exceptions": [
                {
                  "diagnosticId": "AARC002",
                  "sourceType": "Sample.Domain.LegacyBridge",
                  "targetType": "Sample.Application.LegacyService"
                }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("in 'exceptions': required property 'justification' is missing", result.ErrorReason);
    }

    [Fact]
    public void Loader_Aarc002Exception_MissingTargetType_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [],
              "exceptions": [
                {
                  "diagnosticId": "AARC002",
                  "sourceType": "Sample.Domain.LegacyBridge",
                  "justification": "Required."
                }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("in AARC002 exception: required property 'targetType' is missing", result.ErrorReason);
    }

    [Fact]
    public void Loader_Aarc002Exception_ApiFieldsFail()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [],
              "exceptions": [
                {
                  "diagnosticId": "AARC002",
                  "sourceType": "Sample.Domain.LegacyBridge",
                  "targetType": "Sample.Application.LegacyService",
                  "apiType": "System.DateTime",
                  "justification": "Required."
                }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("AARC002 exception must not declare 'apiType' or 'member'", result.ErrorReason);
    }

    [Fact]
    public void Loader_Aarc003Exception_MissingApiType_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [],
              "exceptions": [
                {
                  "diagnosticId": "AARC003",
                  "sourceType": "Sample.Domain.LegacyClock",
                  "member": "Now",
                  "justification": "Required."
                }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("in AARC003 exception: required property 'apiType' is missing", result.ErrorReason);
    }

    [Fact]
    public void Loader_Aarc003Exception_MissingMember_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [],
              "exceptions": [
                {
                  "diagnosticId": "AARC003",
                  "sourceType": "Sample.Domain.LegacyClock",
                  "apiType": "System.DateTime",
                  "justification": "Required."
                }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("in AARC003 exception: required property 'member' is missing", result.ErrorReason);
    }

    [Fact]
    public void Loader_Aarc003Exception_TargetTypeFieldFails()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [],
              "exceptions": [
                {
                  "diagnosticId": "AARC003",
                  "sourceType": "Sample.Domain.LegacyClock",
                  "apiType": "System.DateTime",
                  "member": "Now",
                  "targetType": "Sample.Other",
                  "justification": "Required."
                }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal("AARC003 exception must not declare 'targetType'", result.ErrorReason);
    }

    [Fact]
    public void Loader_DuplicateAarc002Exception_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [],
              "exceptions": [
                {
                  "diagnosticId": "AARC002",
                  "sourceType": "Sample.Domain.LegacyBridge",
                  "targetType": "Sample.Application.LegacyService",
                  "justification": "First."
                },
                {
                  "diagnosticId": "AARC002",
                  "sourceType": "Sample.Domain.LegacyBridge",
                  "targetType": "Sample.Application.LegacyService",
                  "justification": "Second."
                }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "AARC002 exception 'Sample.Domain.LegacyBridge' -> 'Sample.Application.LegacyService' is declared more than once",
            result.ErrorReason);
    }

    [Fact]
    public void Loader_DuplicateAarc003Exception_Fails()
    {
        const string contract = """
            {
              "schemaVersion": 5,
              "layers": [],
              "exceptions": [
                {
                  "diagnosticId": "AARC003",
                  "sourceType": "Sample.Domain.LegacyClock",
                  "apiType": "System.DateTime",
                  "member": "Now",
                  "justification": "First."
                },
                {
                  "diagnosticId": "AARC003",
                  "sourceType": "Sample.Domain.LegacyClock",
                  "apiType": "System.DateTime",
                  "member": "Now",
                  "justification": "Second."
                }
              ]
            }
            """;

        var result = ArchitectureContractLoader.Load(contract);

        Assert.False(result.Succeeded);
        Assert.Equal(
            "AARC003 exception 'Sample.Domain.LegacyClock' -> 'System.DateTime.Now' is declared more than once",
            result.ErrorReason);
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
