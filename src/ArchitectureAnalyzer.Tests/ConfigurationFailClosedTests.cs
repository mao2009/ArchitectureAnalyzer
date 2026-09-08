using System.Threading.Tasks;
using ArchitectureAnalyzer.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace ArchitectureAnalyzer.Tests;

/// <summary>
/// Fail-closed policy for invalid and unknown <c>architecture_analyzer.*</c> configuration (#31).
/// </summary>
/// <remarks>
/// <para>
/// The assertion that matters in every case below is behavioral, not cosmetic: an unparseable
/// value must never let a violation slip through. AARC008's own severity (Warning) is deliberately
/// not the mechanism — the mechanism is the fallback value, which is the enforcing interpretation
/// wherever the documented default is the permissive one.
/// </para>
/// <para>
/// <b>Roslyn API note.</b> Detecting a mistyped property <em>name</em> requires enumerating the
/// configured keys, which <c>AnalyzerConfigOptions.TryGetValue</c> alone cannot do.
/// <c>AnalyzerConfigOptions.Keys</c> (Roslyn 4.4+) does provide it and the compiler's own
/// <c>DictionaryAnalyzerConfigOptions</c> overrides it, which is what
/// <see cref="UnknownProperty_ReportsAarc009AndLeavesEnforcementOn"/> proves end to end. The base
/// property is virtual-throwing, so hosts predating 4.4 simply skip unknown-key detection.
/// </para>
/// </remarks>
public sealed class ConfigurationFailClosedTests
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
          ],
          "forbiddenApis": [
            { "layer": "Domain", "type": "System.Console", "reason": "No console output in the Domain layer." }
          ]
        }
        """;

    private const string AttributesSource = """
        namespace Sample.Arch
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class LayerAttr : System.Attribute { }
        }
        """;

    /// <summary>Contract whose declaration rules are opt-in, so only the operational
    /// <c>validate_namespace_layer</c> property can switch AARC006 on.</summary>
    private const string DeclarationContract = """
        {
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
            { "name": "Application", "namespaceRoots": [ "Sample.Application" ] }
          ],
          "layerDeclaration": {
            "required": true,
            "markerAttributes": [
              { "attributeFqn": "Sample.Arch.LayerAttr", "layer": "Domain" }
            ],
            "markerNamespace": "Sample.Arch"
          }
        }
        """;

    /// <summary>The forbidden dependency scenario used by most cases: Domain -> Application.</summary>
    private const string ViolatingSource = """
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
        """;

    private const string GeneratedViolationSource = """
        namespace Sample.Domain
        {
            public class DomainEntity
            {
                public void Log() => System.Console.WriteLine("x");
            }
        }
        """;

    private static DiagnosticResult ExpectForbiddenDependency()
    {
        return ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenLayerDependency,
            12,
            35,
            "Sample.Domain.DomainEntity",
            "Domain",
            "Sample.Application.AppService",
            "Application",
            Reason);
    }

    private static DiagnosticResult ExpectInvalidConfig(string key, string value)
    {
        return ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.InvalidConfigurationValue, key, value);
    }

    // ------------------------------------------------------------------------------------------
    // Invalid values: the fallback must keep enforcement on
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task InvalidEnabled_KeepsAnalysisOn()
    {
        // `enabled` is the master switch: an unparseable value must not read as "off".
        var test = new ArchitectureAnalyzerTest(Contract) { TestCode = ViolatingSource };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.enabled = flase
            """));

        test.ExpectedDiagnostics.Add(ExpectForbiddenDependency());
        test.ExpectedDiagnostics.Add(ExpectInvalidConfig(
            "dotnet_diagnostic.AARC002.architecture_analyzer.enabled", "flase"));

        await test.RunAsync();
    }

    [Fact]
    public async Task InvalidRequireLayerDeclaration_KeepsAarc004Enforcing()
    {
        // require_layer_declaration=false relaxes AARC004; a typo must not achieve that.
        var test = new ArchitectureAnalyzerTest(DeclarationContract)
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
        test.TestState.Sources.Add(("/0/Test1.cs", AttributesSource));
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.require_layer_declaration = flase
            """));

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.MissingLayerDeclaration)
                .WithLocation(3, 18)
                .WithArguments("Sample.Domain.DomainThing"));
        test.ExpectedDiagnostics.Add(ExpectInvalidConfig(
            "dotnet_diagnostic.AARC002.architecture_analyzer.require_layer_declaration", "flase"));

        await test.RunAsync();
    }

    [Fact]
    public async Task InvalidValidateNamespaceLayer_TurnsCheckOnRatherThanOff()
    {
        // The core #31 case. The documented default is `false`, so falling back to the default
        // would leave AARC006 silently off after a typo. The fail-closed fallback is `true`.
        var test = new ArchitectureAnalyzerTest(DeclarationContract)
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
        test.TestState.Sources.Add(("/0/Test1.cs", AttributesSource));
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.validate_namespace_layer = flase
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
        test.ExpectedDiagnostics.Add(ExpectInvalidConfig(
            "dotnet_diagnostic.AARC002.architecture_analyzer.validate_namespace_layer", "flase"));

        await test.RunAsync();
    }

    [Fact]
    public async Task InvalidGeneratedCode_AnalyzesGeneratedFileInsteadOfSkippingIt()
    {
        // Skipping generated code narrows detection coverage, so the enum's fail-closed fallback
        // is `include` even though the default is `exclude`.
        var test = new ArchitectureAnalyzerTest(Contract) { TestCode = "// intentionally empty" };
        test.TestState.Sources.Add(("/0/obj/ObjDomain.cs", GeneratedViolationSource));
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.generated_code = includ
            """));

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.ForbiddenApiUsage)
                .WithLocation("/0/obj/ObjDomain.cs", 5, 30)
                .WithArguments("Console.WriteLine", "Domain", "No console output in the Domain layer."));
        test.ExpectedDiagnostics.Add(ExpectInvalidConfig(
            "dotnet_diagnostic.AARC002.architecture_analyzer.generated_code", "includ"));

        await test.RunAsync();
    }

    [Fact]
    public async Task InvalidSkipGeneratedCode_AnalyzesGeneratedFileInsteadOfSkippingIt()
    {
        var test = new ArchitectureAnalyzerTest(Contract) { TestCode = "// intentionally empty" };
        test.TestState.Sources.Add(("/0/obj/ObjDomain.cs", GeneratedViolationSource));
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.skip_generated_code = ture
            """));

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.ForbiddenApiUsage)
                .WithLocation("/0/obj/ObjDomain.cs", 5, 30)
                .WithArguments("Console.WriteLine", "Domain", "No console output in the Domain layer."));
        test.ExpectedDiagnostics.Add(ExpectInvalidConfig(
            "dotnet_diagnostic.AARC002.architecture_analyzer.skip_generated_code", "ture"));

        await test.RunAsync();
    }

    [Fact]
    public async Task InvalidRuleToggle_KeepsRuleEnabled()
    {
        // rule.<ID>.enabled = false is the per-diagnostic kill switch; a typo must not trip it.
        var test = new ArchitectureAnalyzerTest(Contract) { TestCode = ViolatingSource };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC002.enabled = offf
            """));

        test.ExpectedDiagnostics.Add(ExpectForbiddenDependency());
        test.ExpectedDiagnostics.Add(ExpectInvalidConfig(
            "dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC002.enabled", "offf"));

        await test.RunAsync();
    }

    // ------------------------------------------------------------------------------------------
    // Reporting shape
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task InvalidValueSpanningManyTrees_ReportsAarc008ExactlyOnce()
    {
        // Config is re-read for every analyzed node in every tree; the diagnostic is deduplicated
        // per property key, so a three-file compilation still yields a single AARC008.
        var test = new ArchitectureAnalyzerTest(Contract) { TestCode = ViolatingSource };
        test.TestState.Sources.Add(("/0/Second.cs", """
            namespace Sample.Domain
            {
                public class SecondEntity
                {
                    public Sample.Application.AppService Service { get; set; }
                }
            }
            """));
        test.TestState.Sources.Add(("/0/Third.cs", """
            namespace Sample.Domain
            {
                public class ThirdEntity
                {
                    public Sample.Application.AppService Service { get; set; }
                }
            }
            """));
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.enabled = flase
            """));

        test.ExpectedDiagnostics.Add(ExpectForbiddenDependency());
        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.ForbiddenLayerDependency)
                .WithLocation("/0/Second.cs", 5, 35)
                .WithArguments(
                    "Sample.Domain.SecondEntity", "Domain",
                    "Sample.Application.AppService", "Application", Reason));
        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.ForbiddenLayerDependency)
                .WithLocation("/0/Third.cs", 5, 35)
                .WithArguments(
                    "Sample.Domain.ThirdEntity", "Domain",
                    "Sample.Application.AppService", "Application", Reason));

        // Exactly one AARC008 for the one invalid property, despite three trees.
        test.ExpectedDiagnostics.Add(ExpectInvalidConfig(
            "dotnet_diagnostic.AARC002.architecture_analyzer.enabled", "flase"));

        await test.RunAsync();
    }

    [Fact]
    public async Task UnrelatedInvalidOption_DoesNotSuppressArchitectureDiagnostics()
    {
        // An invalid property that has nothing to do with AARC002/AARC003 must not disturb them.
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

                        public void Log() => System.Console.WriteLine("x");
                    }
                }
                """,
        };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC001.architecture_analyzer.contract_required = perhaps
            """));

        test.ExpectedDiagnostics.Add(ExpectForbiddenDependency());
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenApiUsage,
            14,
            30,
            "Console.WriteLine",
            "Domain",
            "No console output in the Domain layer."));
        test.ExpectedDiagnostics.Add(ExpectInvalidConfig(
            "dotnet_diagnostic.AARC001.architecture_analyzer.contract_required", "perhaps"));

        await test.RunAsync();
    }

    [Fact]
    public async Task AllValuesValid_ReportsNoConfigurationDiagnostic()
    {
        // The harness fails on any unexpected diagnostic, so the absence of AARC008/AARC009 here
        // is asserted by the run itself.
        var test = new ArchitectureAnalyzerTest(Contract) { TestCode = ViolatingSource };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.enabled = true
            dotnet_diagnostic.AARC002.architecture_analyzer.require_layer_declaration = true
            dotnet_diagnostic.AARC002.architecture_analyzer.validate_namespace_layer = false
            dotnet_diagnostic.AARC002.architecture_analyzer.generated_code = exclude
            dotnet_diagnostic.AARC002.architecture_analyzer.skip_generated_code = true
            dotnet_diagnostic.AARC003.architecture_analyzer.skip_generated_code = true
            dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC002.enabled = true
            dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC003.enabled = true
            dotnet_diagnostic.AARC001.architecture_analyzer.contract_required = true
            """));

        test.ExpectedDiagnostics.Add(ExpectForbiddenDependency());

        await test.RunAsync();
    }

    // ------------------------------------------------------------------------------------------
    // Unknown property names (AARC009)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnknownProperty_ReportsAarc009AndLeavesEnforcementOn()
    {
        // A mistyped property name is invisible to TryGetValue, so without key enumeration the
        // user's intended setting would silently never apply. This run also proves that
        // AnalyzerConfigOptions.Keys really does surface .editorconfig keys.
        var test = new ArchitectureAnalyzerTest(Contract) { TestCode = ViolatingSource };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.require_layer_declration = false
            """));

        test.ExpectedDiagnostics.Add(ExpectForbiddenDependency());
        // Roslyn stores .editorconfig keys lowercased, so the reported name is the lowercase form.
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.UnknownConfigurationProperty,
            "dotnet_diagnostic.aarc002.architecture_analyzer.require_layer_declration",
            "false"));

        await test.RunAsync();
    }

    [Fact]
    public async Task UnknownRuleToggle_ReportsAarc009()
    {
        // Only AARC002/AARC003 have honored rule toggles; any other one is a silent no-op today.
        var test = new ArchitectureAnalyzerTest(Contract) { TestCode = ViolatingSource };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC004.enabled = false
            """));

        test.ExpectedDiagnostics.Add(ExpectForbiddenDependency());
        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.UnknownConfigurationProperty,
            "dotnet_diagnostic.aarc002.architecture_analyzer.rule.aarc004.enabled",
            "false"));

        await test.RunAsync();
    }

    [Fact]
    public async Task UnrelatedEditorConfigEntries_AreNotReportedAsUnknown()
    {
        // Only keys containing `.architecture_analyzer.` are ours; everything else in the file
        // (other analyzers, formatting rules, severity overrides) must be left alone.
        var test = new ArchitectureAnalyzerTest(Contract) { TestCode = ViolatingSource };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            indent_style = space
            dotnet_diagnostic.CA1000.severity = none
            dotnet_diagnostic.AARC002.architecture_analyzer.enabled = true
            """));

        test.ExpectedDiagnostics.Add(ExpectForbiddenDependency());

        await test.RunAsync();
    }
}
