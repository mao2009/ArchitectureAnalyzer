using System.Threading.Tasks;
using ArchitectureAnalyzer.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace ArchitectureAnalyzer.Tests;

/// <summary>
/// AnalyzerConfig / .editorconfig operational overrides (#30): global and tree-specific
/// <c>architecture_analyzer.*</c> properties read through AnalyzerConfigOptionsProvider.
/// </summary>
public sealed class ConfigurationTests
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

    [Fact]
    public async Task NoEditorConfig_PreservesCurrentBehavior()
    {
        // No .editorconfig at all: AARC002 still fires exactly as in v0.1.
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
            Reason));

        await test.RunAsync();
    }

    [Fact]
    public async Task GlobalSetting_RequireLayerDeclarationKeepsUnclassifiedInvisible()
    {
        // Same observable behavior as today when the toggle is explicitly enabled for all trees.
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                namespace Sample.Application
                {
                    public class AppService
                    {
                    }
                }

                namespace Sample.Tooling
                {
                    public class Helper
                    {
                        public Sample.Application.AppService Service { get; set; }
                    }
                }
                """,
        };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.require_layer_declaration = true
            """));

        await test.RunAsync();
    }

    [Fact]
    public async Task TreeSpecificSetting_RuleToggleDisablesAarc002ForThatFile()
    {
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
                    }
                }
                """,
        };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC002.enabled = false
            """));

        await test.RunAsync();
    }

    [Fact]
    public async Task RuleToggleDisablingAarc003_SuppressesForbiddenApiViolation()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainEntity
                    {
                        public void Log() => System.Console.WriteLine("x");
                    }
                }
                """,
        };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC003.enabled = false
            """));

        await test.RunAsync();
    }

    [Fact]
    public async Task RuleToggleDefault_ForbiddenApiViolationStillReported()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainEntity
                    {
                        public void Log() => System.Console.WriteLine("x");
                    }
                }
                """,
        };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC002.enabled = true
            """));

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenApiUsage,
            5,
            30,
            "Console.WriteLine",
            "Domain",
            "No console output in the Domain layer."));

        await test.RunAsync();
    }

    [Fact]
    public async Task InvalidBoolValue_ReportsAarc008AndFallsBackToDefault()
    {
        // An unparseable bool stays fail-closed: the value is ignored, the default applies, and
        // a warning (AARC008) surfaces the typo instead of silently changing enforcement.
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
                    }
                }
                """,
        };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.require_layer_declaration = definitely-not-a-bool
            """));

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
            ArchitectureDiagnostics.ForbiddenLayerDependency,
            12,
            35,
            "Sample.Domain.DomainEntity",
            "Domain",
            "Sample.Application.AppService",
            "Application",
            Reason));

        test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.ExpectNoLocation(
            ArchitectureDiagnostics.InvalidConfigurationValue,
            "dotnet_diagnostic.AARC002.architecture_analyzer.require_layer_declaration",
            "definitely-not-a-bool"));

        await test.RunAsync();
    }

    [Fact]
    public async Task GeneratedCodeEnabledValue_Include_AnalyzesObjDirectoryFile()
    {
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = "// intentionally empty",
        };
        test.TestState.Sources.Add(("/0/obj/ObjDomain.cs", """
            namespace Sample.Domain
            {
                public class DomainEntity
                {
                    public void Log() => System.Console.WriteLine("x");
                }
            }
            """));

        // The enum-like generated_code=include override turns off generated-path skipping for
        // that file, so the AARC003 violation inside the obj/ file is reported.
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.generated_code = include
            """));

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.ForbiddenApiUsage)
                .WithLocation("/0/obj/ObjDomain.cs", 5, 30)
                .WithArguments("Console.WriteLine", "Domain", "No console output in the Domain layer."));

        await test.RunAsync();
    }

    [Fact]
    public async Task GeneratedCodeDefault_SkipsObjDirectoryFile()
    {
        // With no operational override, a file under /obj/ matching the generated-path
        // convention is skipped (v0.1 behavior, fail-open default for optional generated code).
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = "// intentionally empty",
        };
        test.TestState.Sources.Add(("/0/obj/ObjDomain.cs", """
            namespace Sample.Domain
            {
                public class DomainEntity
                {
                    public void Log() => System.Console.WriteLine("x");
                }
            }
            """));

        await test.RunAsync();
    }

    [Fact]
    public async Task GeneratedCodeBoolOption_False_AnalyzesObjDirectoryFile()
    {
        // The #29-finalized bool form skip_generated_code=false has the same effect as the
        // enum-like generated_code=include override.
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = "// intentionally empty",
        };
        test.TestState.Sources.Add(("/0/obj/ObjDomain.cs", """
            namespace Sample.Domain
            {
                public class DomainEntity
                {
                    public void Log() => System.Console.WriteLine("x");
                }
            }
            """));

        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.skip_generated_code = false
            """));

        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.ForbiddenApiUsage)
                .WithLocation("/0/obj/ObjDomain.cs", 5, 30)
                .WithArguments("Console.WriteLine", "Domain", "No console output in the Domain layer."));

        await test.RunAsync();
    }

    [Fact]
    public async Task ContractRequiredFalse_InvalidContractIsSilent()
    {
        // contract_required=false: a referenced-but-unloadable contract no longer fails the
        // build. AARC001 is suppressed and the analyzer is a no-op.
        var test = new ArchitectureAnalyzerTest("this is not json")
        {
            TestCode = """
                namespace Sample.Domain
                {
                    public class DomainEntity
                    {
                    }
                }
                """,
        };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            [*.cs]
            dotnet_diagnostic.AARC001.architecture_analyzer.contract_required = false
            """));

        await test.RunAsync();
    }

    [Fact]
    public async Task DeeperEditorConfigWins()
    {
        // A nested .editorconfig overrides the root one for files in its directory only.
        var test = new ArchitectureAnalyzerTest(Contract);
        test.TestState.Sources.Add(("/0/Legacy/AppService.cs", """
            namespace Sample.Application
            {
                public class AppService
                {
                }
            }
            """));
        test.TestState.Sources.Add(("/0/Legacy/DomainEntity.cs", """
            namespace Sample.Domain
            {
                public class DomainEntity
                {
                    public Sample.Application.AppService Service { get; set; }
                }
            }
            """));
        test.TestState.Sources.Add(("/0/DomainOther.cs", """
            namespace Sample.Domain
            {
                public class DomainOther
                {
                    public Sample.Application.AppService Service { get; set; }
                }
            }
            """));

        // Root: AARC002 enabled everywhere. Deeper file: disabled only under /Legacy/.
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC002.enabled = true
            """));
        test.TestState.AnalyzerConfigFiles.Add(("/0/Legacy/.editorconfig", """
            [*.cs]
            dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC002.enabled = false
            """));

        // Only the file outside Legacy/ is still regulated.
        test.ExpectedDiagnostics.Add(
            new DiagnosticResult(ArchitectureDiagnostics.ForbiddenLayerDependency)
                .WithLocation("/0/DomainOther.cs", 5, 35)
                .WithArguments(
                    "Sample.Domain.DomainOther",
                    "Domain",
                    "Sample.Application.AppService",
                    "Application",
                    Reason));

        await test.RunAsync();
    }

    [Fact]
    public async Task DotnetDiagnosticSeverityOverride_ReportsAsWarning()
    {
        // The standard Roslyn severity key is honored end-to-end: the descriptor reports Error
        // by default, yet the .editorconfig override surfaces the diagnostic as a warning.
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
                    }
                }
                """,
        };
        test.TestState.AnalyzerConfigFiles.Add(("/0/.editorconfig", """
            root = true

            [*.cs]
            dotnet_diagnostic.AARC002.severity = warning
            """));

        test.ExpectedDiagnostics.Add(
            ArchitectureAnalyzerTest.Expect(
                ArchitectureDiagnostics.ForbiddenLayerDependency,
                12,
                35,
                "Sample.Domain.DomainEntity",
                "Domain",
                "Sample.Application.AppService",
                "Application",
                Reason)
            .WithSeverity(DiagnosticSeverity.Warning));

        await test.RunAsync();
    }

    [Fact]
    public async Task WindowsStyleObjPath_IsRecognizedAsGenerated()
    {
        // IsGeneratedPath normalizes backslashes, so a Windows-style `\work\obj\` segment keeps
        // the default generated-code skip in force — path-separator independent.
        var diagnostics = await RunAnalyzerWithPathAsync(Contract, """
            namespace Sample.Domain
            {
                public class DomainEntity
                {
                    public void Log() => System.Console.WriteLine("x");
                }
            }
            """, path: @"C:\work\obj\Domain.cs");

        Assert.DoesNotContain(diagnostics,
            diagnostic => diagnostic.Id == ArchitectureDiagnostics.ForbiddenApiUsage.Id);
    }

    /// <summary>Runs the analyzer against a syntax tree pinned to an arbitrary file path.</summary>
    private static async Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> RunAnalyzerWithPathAsync(
        string contractJson,
        string source,
        string path)
    {
        var references = await ReferenceAssemblies.Net.Net90
            .ResolveAsync(LanguageNames.CSharp, CancellationToken.None);

        var compilation = CSharpCompilation.Create(
            "PathSepTest",
            new[] { CSharpSyntaxTree.ParseText(source, path: path) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var options = new AnalyzerOptions(
            System.Collections.Immutable.ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText(ArchitectureContractAnalyzer.ContractFileName, contractJson)));

        var withAnalyzers = compilation.WithAnalyzers(
            System.Collections.Immutable.ImmutableArray.Create<DiagnosticAnalyzer>(
                new ArchitectureContractAnalyzer()),
            options);

        return await withAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None);
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }
}