using ArchitectureAnalyzer.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;

namespace ArchitectureAnalyzer.Tests;

/// <summary>
/// Layer-declaration rules (AARC004/AARC005/AARC006) driven by the optional
/// <c>layerDeclaration</c> contract section (#32). Marker attributes are supplied by the test
/// contract, not hardcoded.
/// </summary>
public sealed class LayerDeclarationTests
{
    /// <summary>Marker attribute definitions, placed in a separate source file so the scenario
    /// file in <c>Test0.cs</c> keeps predictable line numbers.</summary>
    private const string AttributesSource = """
        namespace Sample.Arch
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class LayerAttr : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class LayerAttrAlt : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class AppAttr : System.Attribute { }
        }
        """;

    private const string RequiredContract = """
        {
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
            { "name": "Application", "namespaceRoots": [ "Sample.Application" ] }
          ],
          "layerDeclaration": {
            "required": true,
            "markerAttributes": [
              { "attributeFqn": "Sample.Arch.LayerAttr", "layer": "Domain" },
              { "attributeFqn": "Sample.Arch.LayerAttrAlt", "layer": "Domain" },
              { "attributeFqn": "Sample.Arch.AppAttr", "layer": "Application" }
            ],
            "markerNamespace": "Sample.Arch"
          }
        }
        """;

    private const string OptionalContract = """
        {
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
            { "name": "Application", "namespaceRoots": [ "Sample.Application" ] }
          ],
          "layerDeclaration": {
            "required": false,
            "validateNamespaceConsistency": true,
            "markerAttributes": [
              { "attributeFqn": "Sample.Arch.LayerAttr", "layer": "Domain" },
              { "attributeFqn": "Sample.Arch.LayerAttrAlt", "layer": "Domain" },
              { "attributeFqn": "Sample.Arch.AppAttr", "layer": "Application" }
            ],
            "markerNamespace": "Sample.Arch"
          }
        }
        """;

    private const string NamespaceOnlyContract = """
        {
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
          ]
        }
        """;

    /// <summary>Adds the marker attribute definitions as a second source file.</summary>
    private static void AddMarkerAttributes(ArchitectureAnalyzerTest test)
    {
        test.TestState.Sources.Add(("/0/Test1.cs", AttributesSource));
    }

    // ------------------------------------------------------------------------------------------
    // AARC004 - missing required declaration
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnattributedClass_WithRequiredDeclaration_ReportsAarc004()
    {
        var test = new ArchitectureAnalyzerTest(RequiredContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainThing
                    {
                    }
                }
                """,
        };
        AddMarkerAttributes(test);

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.MissingLayerDeclaration)
                .WithLocation(3, 18)
                .WithArguments("Sample.Domain.DomainThing"));

        await test.RunAsync();
    }

    [Fact]
    public async Task AttributedClass_WithRequiredDeclaration_IsValid()
    {
        var test = new ArchitectureAnalyzerTest(RequiredContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    [Sample.Arch.LayerAttr]
                    public class DomainThing
                    {
                    }
                }
                """,
        };
        AddMarkerAttributes(test);

        await test.RunAsync();
    }

    [Fact]
    public async Task UnattributedClass_WithOptionalDeclaration_IsSilent()
    {
        var test = new ArchitectureAnalyzerTest(OptionalContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainThing
                    {
                    }
                }
                """,
        };
        AddMarkerAttributes(test);

        await test.RunAsync();
    }

    [Fact]
    public async Task NestedClass_WithoutOwnAttribute_InheritsEnclosingDeclaration()
    {
        var test = new ArchitectureAnalyzerTest(RequiredContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    [Sample.Arch.LayerAttr]
                    public class Outer
                    {
                        public class Inner
                        {
                        }
                    }
                }
                """,
        };
        AddMarkerAttributes(test);

        await test.RunAsync();
    }

    [Fact]
    public async Task PartialClass_WithAttributeOnOnePart_SatisfiesMissing()
    {
        var test = new ArchitectureAnalyzerTest(RequiredContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    [Sample.Arch.LayerAttr]
                    public partial class DomainThing
                    {
                    }
                }
                namespace Sample.Domain
                {
                    public partial class DomainThing
                    {
                    }
                }
                """,
        };
        AddMarkerAttributes(test);

        await test.RunAsync();
    }

    [Fact]
    public async Task ClassInGeneratedPath_IsExemptFromRequiredDeclaration()
    {
        var test = new ArchitectureAnalyzerTest(RequiredContract)
        {
            TestCode = "// intentionally empty",
        };
        AddMarkerAttributes(test);
        test.TestState.Sources.Add(("/0/obj/Generated.cs", """
            namespace Sample.Domain
            {
                public class GeneratedThing
                {
                }
            }
            """));

        await test.RunAsync();
    }

    [Fact]
    public async Task ClassInMarkerNamespace_IsExemptFromRequiredDeclaration()
    {
        var test = new ArchitectureAnalyzerTest(RequiredContract)
        {
            TestCode = """
                namespace Sample.Arch
                {
                    public class AttributeHelper
                    {
                    }
                }
                """,
        };
        AddMarkerAttributes(test);

        await test.RunAsync();
    }

    // ------------------------------------------------------------------------------------------
    // AARC005 - multiple distinct declarations
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task MultipleDistinctLayerAttributes_ReportsAarc005()
    {
        var test = new ArchitectureAnalyzerTest(RequiredContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    [Sample.Arch.LayerAttr]
                    [Sample.Arch.AppAttr]
                    public class Ambiguous
                    {
                    }
                }
                """,
        };
        AddMarkerAttributes(test);

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.MultipleLayerDeclarations)
                .WithLocation(5, 18)
                .WithArguments("Sample.Domain.Ambiguous", "Domain, Application"));

        await test.RunAsync();
    }

    [Fact]
    public async Task TwoAttributesMappingToSameLayer_IsNotMultiple()
    {
        var test = new ArchitectureAnalyzerTest(RequiredContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    [Sample.Arch.LayerAttr]
                    [Sample.Arch.LayerAttrAlt]
                    public class Consistent
                    {
                    }
                }
                """,
        };
        AddMarkerAttributes(test);

        await test.RunAsync();
    }

    [Fact]
    public async Task MultipleDistinctLayerAttributes_OnNestedType_ReportsAarc005()
    {
        var test = new ArchitectureAnalyzerTest(RequiredContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    [Sample.Arch.LayerAttr]
                    public class Outer
                    {
                        [Sample.Arch.LayerAttr]
                        [Sample.Arch.AppAttr]
                        public class Inner
                        {
                        }
                    }
                }
                """,
        };
        AddMarkerAttributes(test);

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.MultipleLayerDeclarations)
                .WithLocation(8, 22)
                .WithArguments("Sample.Domain.Outer.Inner", "Domain, Application"));

        await test.RunAsync();
    }

    // ------------------------------------------------------------------------------------------
    // AARC006 - namespace consistency
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AttributeConsistentWithNamespace_IsSilent()
    {
        var test = new ArchitectureAnalyzerTest(OptionalContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    [Sample.Arch.LayerAttr]
                    public class DomainThing
                    {
                    }
                }
                """,
        };
        AddMarkerAttributes(test);

        await test.RunAsync();
    }

    [Fact]
    public async Task AttributeContradictsNamespace_ReportsAarc006()
    {
        var test = new ArchitectureAnalyzerTest(OptionalContract)
        {
            TestCode = """
                namespace Sample.Application
                {
                    [Sample.Arch.LayerAttr]
                    public class DomainThing
                    {
                    }
                }
                """,
        };
        AddMarkerAttributes(test);

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.LayerDeclarationNamespaceMismatch)
                .WithLocation(4, 18)
                .WithSeverity(DiagnosticSeverity.Warning)
                .WithArguments(
                    "Sample.Application.DomainThing",
                    "Domain",
                    "Sample.Application",
                    "Application"));

        await test.RunAsync();
    }

    [Fact]
    public async Task NestedType_OwnAttributeOverridesEnclosingNamespace()
    {
        // Inner's own Application attribute wins over the enclosing Sample.Domain namespace,
        // surfacing as AARC006 (declared Application vs namespace-implied Domain).
        var test = new ArchitectureAnalyzerTest(OptionalContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class Outer
                    {
                        [Sample.Arch.AppAttr]
                        public class Inner
                        {
                        }
                    }
                }
                """,
        };
        AddMarkerAttributes(test);

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.LayerDeclarationNamespaceMismatch)
                .WithLocation(6, 22)
                .WithSeverity(DiagnosticSeverity.Warning)
                .WithArguments(
                    "Sample.Domain.Outer.Inner",
                    "Application",
                    "Sample.Domain",
                    "Domain"));

        await test.RunAsync();
    }

    // ------------------------------------------------------------------------------------------
    // Partial conflicts / generic / backward compatibility
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task PartialConflictingAttributes_ReportsAarc005()
    {
        var test = new ArchitectureAnalyzerTest(RequiredContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    [Sample.Arch.LayerAttr]
                    public partial class Conflicted
                    {
                    }
                }
                namespace Sample.Domain
                {
                    [Sample.Arch.AppAttr]
                    public partial class Conflicted
                    {
                    }
                }
                """,
        };
        AddMarkerAttributes(test);

        // Location is the first declared part of the merged symbol.
        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.MultipleLayerDeclarations)
                .WithLocation(4, 26)
                .WithArguments("Sample.Domain.Conflicted", "Domain, Application"));

        await test.RunAsync();
    }

    [Fact]
    public async Task GenericType_WithAttribute_IsValid()
    {
        var test = new ArchitectureAnalyzerTest(RequiredContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    [Sample.Arch.LayerAttr]
                    public class Repository<T>
                    {
                    }
                }
                """,
        };
        AddMarkerAttributes(test);

        await test.RunAsync();
    }

    [Fact]
    public async Task NamespaceOnlyContract_AttributedClasses_AreSilent()
    {
        // Without a layerDeclaration section nothing new fires, even when marker attribute types
        // happen to be declared in source.
        var test = new ArchitectureAnalyzerTest(NamespaceOnlyContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    [Sample.Arch.LayerAttr]
                    public class DomainThing
                    {
                    }

                    public class PlainThing
                    {
                    }
                }
                """,
        };
        AddMarkerAttributes(test);

        await test.RunAsync();
    }

    // ------------------------------------------------------------------------------------------
    // Contract validation (AARC001)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task RequiredWithoutMarkerAttributes_ReportsContractInvalid()
    {
        const string contract = """
            {
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ],
              "layerDeclaration": {
                "required": true
              }
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainThing
                    {
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.ArchitectureContractInvalid,
            ArchitectureContractAnalyzer.ContractFileName,
            "layerDeclaration.required is true but no marker attributes are declared"));

        await test.RunAsync();
    }

    [Fact]
    public async Task MarkerAttributeMappingToUndeclaredLayer_ReportsContractInvalid()
    {
        const string contract = """
            {
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ],
              "layerDeclaration": {
                "markerAttributes": [
                  { "attributeFqn": "Sample.Arch.LayerAttr", "layer": "Infrastructure" }
                ]
              }
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainThing
                    {
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.ArchitectureContractInvalid,
            ArchitectureContractAnalyzer.ContractFileName,
            "marker attribute 'Sample.Arch.LayerAttr' maps to undeclared layer 'Infrastructure'"));

        await test.RunAsync();
    }

    [Fact]
    public async Task DuplicateMarkerAttributeFqn_ReportsContractInvalid()
    {
        const string contract = """
            {
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
                { "name": "Application", "namespaceRoots": [ "Sample.Application" ] }
              ],
              "layerDeclaration": {
                "markerAttributes": [
                  { "attributeFqn": "Sample.Arch.LayerAttr", "layer": "Domain" },
                  { "attributeFqn": "Sample.Arch.LayerAttr", "layer": "Application" }
                ]
              }
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainThing
                    {
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.ArchitectureContractInvalid,
            ArchitectureContractAnalyzer.ContractFileName,
            "duplicate marker attribute 'Sample.Arch.LayerAttr' in layerDeclaration.markerAttributes"));

        await test.RunAsync();
    }

    // ------------------------------------------------------------------------------------------
    // #30 integration seam: operational toggles acting on AARC004 / AARC006
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task RequireLayerDeclarationFalse_SuppressesAarc004()
    {
        var test = new ArchitectureAnalyzerTest(RequiredContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainThing
                    {
                    }
                }
                """,
        };
        AddMarkerAttributes(test);
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.require_layer_declaration = false
            """));

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidateNamespaceLayerTrue_EnablesAarc006()
    {
        const string contract = """
            {
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
                { "name": "Application", "namespaceRoots": [ "Sample.Application" ] }
              ],
              "layerDeclaration": {
                "required": false,
                "markerAttributes": [
                  { "attributeFqn": "Sample.Arch.LayerAttr", "layer": "Domain" },
                  { "attributeFqn": "Sample.Arch.AppAttr", "layer": "Application" }
                ],
                "markerNamespace": "Sample.Arch"
              }
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = """
                namespace Sample.Application
                {
                    [Sample.Arch.LayerAttr]
                    public class DomainThing
                    {
                    }
                }
                """,
        };
        AddMarkerAttributes(test);
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.validate_namespace_layer = true
            """));

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.LayerDeclarationNamespaceMismatch)
                .WithLocation(4, 18)
                .WithSeverity(DiagnosticSeverity.Warning)
                .WithArguments(
                    "Sample.Application.DomainThing",
                    "Domain",
                    "Sample.Application",
                    "Application"));

        await test.RunAsync();
    }
}