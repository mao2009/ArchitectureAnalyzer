using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchitectureAnalyzer.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;

namespace ArchitectureAnalyzer.Tests;

/// <summary>
/// Generated-path bin/obj exclusion is judged relative to the project directory
/// (<c>build_property.ProjectDir</c>), never against the checkout location (#68).
/// </summary>
public sealed class GeneratedPathTests
{
    private const string Contract = """
        {
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "A.Domain" ] },
            { "name": "App", "namespaceRoots": [ "A.App" ] }
          ],
          "forbiddenDependencies": [
            { "from": "Domain", "to": "App", "reason": "Domain must not depend on App." }
          ]
        }
        """;

    private const string AppSource = "namespace A.App { public class Svc { } }";

    private const string DomainSource = "namespace A.Domain { public class D { A.App.Svc s = null!; } }";

    [Theory]
    [InlineData("/repo/src/project/", "/repo/src/project/Domain/D.cs")]
    [InlineData("/repo/bin/project/", "/repo/bin/project/Domain/D.cs")]
    [InlineData("/repo/obj/project/", "/repo/obj/project/Domain/D.cs")]
    [InlineData(@"C:\work\bin\repo\project\", @"C:\work\bin\repo\project\Domain\D.cs")]
    [InlineData("/repo/project/", "/repo/project/binary/D.cs")]
    [InlineData("/repo/project/", "/repo/project/object/D.cs")]
    [InlineData("/repo/project/", "/repo/obj/Shared/D.cs")]
    public async Task HandwrittenFile_IsAnalyzedRegardlessOfCheckoutLocation(string projectDir, string path)
    {
        var diagnostics = await RunAsync(projectDir, path);

        Assert.Contains(diagnostics, d => d.Id == ArchitectureDiagnostics.ForbiddenLayerDependency.Id);
    }

    [Theory]
    [InlineData("/repo/src/project/", "/repo/src/project/bin/Generated.cs")]
    [InlineData("/repo/src/project/", "/repo/src/project/obj/Generated.cs")]
    [InlineData("/repo/bin/project/", "/repo/bin/project/obj/Debug/Generated.cs")]
    [InlineData(@"C:\work\obj\repo\project\", @"C:\work\obj\repo\project\OBJ\Generated.cs")]
    public async Task FileUnderProjectBinOrObj_IsExcluded(string projectDir, string path)
    {
        var diagnostics = await RunAsync(projectDir, path);

        Assert.DoesNotContain(diagnostics, d => d.Id == ArchitectureDiagnostics.ForbiddenLayerDependency.Id);
    }

    private const string Include = "dotnet_diagnostic.AARC002.architecture_analyzer.generated_code";
    private const string SkipFlag = "dotnet_diagnostic.AARC002.architecture_analyzer.skip_generated_code";

    [Theory]
    [InlineData("/bin/project/", "/bin/project/Domain/D.cs", Include, "include")]
    [InlineData("/obj/project/", "/obj/project/Domain/D.cs", SkipFlag, "false")]
    [InlineData(@"C:\work\bin\repo\project\", @"C:\work\bin\repo\project\Domain\D.cs", Include, "include")]
    [InlineData(@"C:\work\obj\repo\project\", @"C:\work\obj\repo\project\Domain\D.cs", SkipFlag, "false")]
    public async Task AncestorBinOrObj_WithGeneratedCodeInclude_IsAnalyzed(string projectDir, string path, string key, string value)
    {
        var diagnostics = await RunAsync(projectDir, path, (key, value));

        Assert.Contains(diagnostics, d => d.Id == ArchitectureDiagnostics.ForbiddenLayerDependency.Id);
    }

    [Theory]
    [InlineData("/repo/project/", "/repo/project/bin/Generated.cs", Include, "include")]
    [InlineData("/repo/project/", "/repo/project/obj/Generated.cs", SkipFlag, "false")]
    [InlineData("/obj/project/", "/obj/project/bin/Generated.cs", Include, "include")]
    public async Task FileUnderProjectBinOrObj_WithGeneratedCodeInclude_IsAnalyzed(string projectDir, string path, string key, string value)
    {
        var diagnostics = await RunAsync(projectDir, path, (key, value));

        Assert.Contains(diagnostics, d => d.Id == ArchitectureDiagnostics.ForbiddenLayerDependency.Id);
    }

    [Fact]
    public async Task FileUnderProjectObj_WithAncestorBin_IsStillExcludedByDefault()
    {
        var diagnostics = await RunAsync("/bin/project/", "/bin/project/obj/Generated.cs");

        Assert.DoesNotContain(diagnostics, d => d.Id == ArchitectureDiagnostics.ForbiddenLayerDependency.Id);
    }

    private static async Task<ImmutableArray<Diagnostic>> RunAsync(
        string projectDir,
        string domainPath,
        params (string Key, string Value)[] extraOptions)
    {
        var references = await ReferenceAssemblies.Net.Net90.ResolveAsync(LanguageNames.CSharp, CancellationToken.None);
        var appPath = projectDir.Replace('\\', '/').TrimEnd('/') + "/App/Svc.cs";

        var compilation = CSharpCompilation.Create(
            "GeneratedPathTest",
            new[]
            {
                CSharpSyntaxTree.ParseText(AppSource, path: appPath),
                CSharpSyntaxTree.ParseText(DomainSource, path: domainPath),
            },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var options = new AnalyzerOptions(
            ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText(projectDir + ArchitectureContractAnalyzer.ContractFileName, Contract)),
            new GlobalOptionsProvider(
                new Dictionary<string, string> { ["build_property.ProjectDir"] = projectDir }
                    .Concat(extraOptions.Select(o => new KeyValuePair<string, string>(o.Key, o.Value)))
                    .ToDictionary(p => p.Key, p => p.Value)));

        return await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ArchitectureContractAnalyzer()), options)
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None);
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

    /// <summary>Mirrors MSBuild's generated global config: build properties are global and visible per tree.</summary>
    private sealed class GlobalOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly DictionaryOptions _options;

        public GlobalOptionsProvider(Dictionary<string, string> values)
        {
            _options = new DictionaryOptions(values);
        }

        public override AnalyzerConfigOptions GlobalOptions => _options;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;
    }

    private sealed class DictionaryOptions : AnalyzerConfigOptions
    {
        private readonly Dictionary<string, string> _values;

        public DictionaryOptions(Dictionary<string, string> values)
        {
            _values = values;
        }

        public override IEnumerable<string> Keys => _values.Keys;

        public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
    }
}
