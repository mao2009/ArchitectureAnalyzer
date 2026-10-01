using ArchitectureAnalyzer.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;

namespace ArchitectureAnalyzer.Tests;

/// <summary>
/// Strict architecture-coverage policy (AARC010), introduced by schema v2.
/// </summary>
public sealed class LayerCoverageTests
{
    private const string StrictContract = """
        {
          "schemaVersion": 2,
          "unclassifiedCode": "error",
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
            { "name": "Application", "namespaceRoots": [ "Sample.Application" ] }
          ]
        }
        """;

    private const string MarkerSource = """
        namespace Sample.Arch
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class DomainMarker : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class AppMarker : System.Attribute { }
        }
        """;

    private const string StrictMarkerContract = """
        {
          "schemaVersion": 2,
          "unclassifiedCode": "error",
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
            { "name": "Application", "namespaceRoots": [ "Sample.Application" ] }
          ],
          "layerDeclaration": {
            "required": false,
            "markerAttributes": [
              { "attributeFqn": "Sample.Arch.DomainMarker", "layer": "Domain" },
              { "attributeFqn": "Sample.Arch.AppMarker", "layer": "Application" }
            ],
            "markerNamespace": "Sample.Arch"
          }
        }
        """;

    [Fact]
    public async Task VersionlessContract_UnclassifiedType_RemainsSilent()
    {
        const string contract = """
            {
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ]
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = """
                namespace Sample.Tools
                {
                    public class Helper
                    {
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task SchemaV2_DefaultIgnore_UnclassifiedType_IsSilent()
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
            TestCode = """
                namespace Sample.Tools
                {
                    public class Helper
                    {
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task StrictCoverage_UnclassifiedType_ReportsAarc010()
    {
        var test = new ArchitectureAnalyzerTest(StrictContract)
        {
            TestCode = """
                namespace Sample.Tools
                {
                    public class Helper
                    {
                    }
                }
                """,
        };

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.ArchitectureCoverageGap)
                .WithLocation(3, 18)
                .WithArguments("Sample.Tools.Helper", "Sample.Tools"));

        await test.RunAsync();
    }

    [Fact]
    public async Task StrictCoverage_ClassifiedNamespace_IsSilent()
    {
        var test = new ArchitectureAnalyzerTest(StrictContract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class Entity
                    {
                    }
                }
                """,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task StrictCoverage_MarkerAttributeClassifiesOtherwiseUnclassifiedType()
    {
        var test = new ArchitectureAnalyzerTest(StrictMarkerContract)
        {
            TestCode = """
                namespace Sample.Tools
                {
                    [Sample.Arch.DomainMarker]
                    public class Helper
                    {
                    }
                }
                """,
        };
        test.TestState.Sources.Add(("/0/Markers.cs", MarkerSource));

        await test.RunAsync();
    }

    [Fact]
    public async Task StrictCoverage_NestedType_InheritsContainingTypeMarker()
    {
        var test = new ArchitectureAnalyzerTest(StrictMarkerContract)
        {
            TestCode = """
                namespace Sample.Tools
                {
                    [Sample.Arch.DomainMarker]
                    public class Outer
                    {
                        public class Inner
                        {
                        }
                    }
                }
                """,
        };
        test.TestState.Sources.Add(("/0/Markers.cs", MarkerSource));

        await test.RunAsync();
    }

    [Fact]
    public async Task StrictCoverage_MarkerNamespace_IsExempt()
    {
        var test = new ArchitectureAnalyzerTest(StrictMarkerContract)
        {
            TestCode = """
                namespace Sample.Arch
                {
                    public class MarkerHelper
                    {
                    }
                }
                """,
        };
        test.TestState.Sources.Add(("/0/Markers.cs", MarkerSource));

        await test.RunAsync();
    }

    [Fact]
    public async Task StrictCoverage_GeneratedPath_IsSkippedByDefault()
    {
        var test = new ArchitectureAnalyzerTest(StrictContract)
        {
            TestCode = "// source supplied below",
        };
        test.TestState.Sources.Add(("/0/obj/Generated.cs", """
            namespace Sample.Tools
            {
                public class GeneratedHelper
                {
                }
            }
            """));

        await test.RunAsync();
    }

    [Fact]
    public async Task StrictCoverage_GeneratedPath_CanBeIncludedOperationally()
    {
        var test = new ArchitectureAnalyzerTest(StrictContract)
        {
            TestCode = "// source supplied below",
        };
        test.TestState.Sources.Add(("/0/obj/Generated.cs", """
            namespace Sample.Tools
            {
                public class GeneratedHelper
                {
                }
            }
            """));
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.generated_code = include
            """));

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.ArchitectureCoverageGap)
                .WithLocation("/0/obj/Generated.cs", 3, 18)
                .WithArguments("Sample.Tools.GeneratedHelper", "Sample.Tools"));

        await test.RunAsync();
    }

    [Fact]
    public async Task StrictCoverage_EnabledFalse_ExcludesTree()
    {
        var test = new ArchitectureAnalyzerTest(StrictContract)
        {
            TestCode = """
                namespace Sample.Tools
                {
                    public class Helper
                    {
                    }
                }
                """,
        };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.enabled = false
            """));

        await test.RunAsync();
    }

    [Fact]
    public async Task StrictCoverage_Aarc010RuleToggleCanStageRollout()
    {
        var test = new ArchitectureAnalyzerTest(StrictContract)
        {
            TestCode = """
                namespace Sample.Tools
                {
                    public class Helper
                    {
                    }
                }
                """,
        };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC010.enabled = false
            """));

        await test.RunAsync();
    }

    [Fact]
    public async Task RequiredDeclaration_UnclassifiedClass_ReportsAarc004WithoutDuplicateAarc010()
    {
        const string contract = """
            {
              "schemaVersion": 2,
              "unclassifiedCode": "error",
              "layers": [
                { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] }
              ],
              "layerDeclaration": {
                "required": true,
                "markerAttributes": [
                  { "attributeFqn": "Sample.Arch.DomainMarker", "layer": "Domain" }
                ],
                "markerNamespace": "Sample.Arch"
              }
            }
            """;

        var test = new ArchitectureAnalyzerTest(contract)
        {
            TestCode = """
                namespace Sample.Tools
                {
                    public class Helper
                    {
                    }
                }
                """,
        };
        test.TestState.Sources.Add(("/0/Markers.cs", MarkerSource));

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.MissingLayerDeclaration)
                .WithLocation(3, 18)
                .WithArguments("Sample.Tools.Helper"));

        await test.RunAsync();
    }

    [Fact]
    public async Task MultipleMarkerLayers_ReportAarc005WithoutCoverageGap()
    {
        var test = new ArchitectureAnalyzerTest(StrictMarkerContract)
        {
            TestCode = """
                namespace Sample.Tools
                {
                    [Sample.Arch.DomainMarker]
                    [Sample.Arch.AppMarker]
                    public class Ambiguous
                    {
                    }
                }
                """,
        };
        test.TestState.Sources.Add(("/0/Markers.cs", MarkerSource));

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.MultipleLayerDeclarations)
                .WithLocation(5, 18)
                .WithArguments("Sample.Tools.Ambiguous", "Domain, Application"));

        await test.RunAsync();
    }
}
