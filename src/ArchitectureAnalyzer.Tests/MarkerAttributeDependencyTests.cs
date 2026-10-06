using System.Threading.Tasks;
using ArchitectureAnalyzer.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace ArchitectureAnalyzer.Tests;

/// <summary>
/// #71: only the application of a <c>layerDeclaration</c> marker attribute (its own type name) is
/// exempt from AARC002. The same marker type used inside another attribute's constructor argument,
/// named argument or <c>typeof</c> is a real dependency.
/// </summary>
public sealed class MarkerAttributeDependencyTests
{
    private const string Reason = "Domain must not depend on the outer Application layer.";

    // The marker type lives in a layered namespace so a reference to it can be a dependency.
    private const string Contract = """
        {
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Sample.Domain" ] },
            { "name": "Application", "namespaceRoots": [ "Sample.Application" ] }
          ],
          "forbiddenDependencies": [
            { "from": "Domain", "to": "Application", "reason": "Domain must not depend on the outer Application layer." }
          ],
          "layerDeclaration": {
            "required": false,
            "markerAttributes": [
              { "attributeFqn": "Sample.Application.DomainMarkerAttribute", "layer": "Domain" }
            ]
          }
        }
        """;

    private const string Fixture = """
        namespace Sample.Application
        {
            public sealed class DomainMarkerAttribute : System.Attribute { public const int Code = 1; }
            public static class DomainConstants { public const int Value = 1; }
        }

        namespace Sample.Domain
        {
            using Sample.Application;

            public sealed class TagAttribute : System.Attribute
            {
                public TagAttribute() { }
                public TagAttribute(object value) { }
                public object Value { get; set; }
            }
        SCENARIO
        }
        """;

    private const string MarkerFqn = "Sample.Application.DomainMarkerAttribute";

    [Theory]
    [InlineData("    [DomainMarker] public class Foo { }")]
    [InlineData("    [DomainMarkerAttribute] public class Foo { }")]
    [InlineData("    [Sample.Application.DomainMarker] public class Foo { }")]
    [InlineData("    [global::Sample.Application.DomainMarkerAttribute] public class Foo { }")]
    public async Task MarkerAttributeApplication_IsNotADependency(string scenario)
    {
        await RunAsync(scenario, null, null);
    }

    [Theory]
    [InlineData("    [Tag(typeof(DomainMarkerAttribute))] public class Foo { }", "DomainMarkerAttribute", MarkerFqn)]
    [InlineData("    [Tag(Value = typeof(DomainMarkerAttribute))] public class Foo { }", "DomainMarkerAttribute", MarkerFqn)]
    [InlineData("    [Tag(DomainMarkerAttribute.Code)] public class Foo { }", "DomainMarkerAttribute", MarkerFqn)]
    [InlineData("    [Tag(Value = DomainMarkerAttribute.Code)] public class Foo { }", "DomainMarkerAttribute", MarkerFqn)]
    [InlineData("    [Tag(DomainConstants.Value)] public class Foo { }", "DomainConstants", "Sample.Application.DomainConstants")]
    public async Task MarkerTypeInsideOtherAttributeArguments_IsADependency(string scenario, string needle, string target)
    {
        await RunAsync(scenario, needle, target);
    }

    [Fact]
    public async Task MarkerApplicationAlongsideMarkerTypeof_ReportsOnlyTheTypeof()
    {
        await RunAsync(
            "    [DomainMarker, Tag(typeof(DomainMarkerAttribute))] public class Foo { }",
            "DomainMarkerAttribute",
            MarkerFqn);
    }

    private static async Task RunAsync(string scenario, string? needle, string? target)
    {
        var line = System.Array.FindIndex(Fixture.Split('\n'), l => l.Trim() == "SCENARIO") + 1;
        var test = new ArchitectureAnalyzerTest(Contract)
        {
            TestCode = Fixture.Replace("SCENARIO", scenario),
        };

        if (needle is not null)
        {
            // Last occurrence: in "[DomainMarker, Tag(typeof(DomainMarkerAttribute))]" the first
            // match would be the (exempt) application, not the typeof operand.
            var column = scenario.LastIndexOf(needle, System.StringComparison.Ordinal) + 1;
            test.ExpectedDiagnostics.Add(ArchitectureAnalyzerTest.Expect(
                ArchitectureDiagnostics.ForbiddenLayerDependency,
                line,
                column,
                "Sample.Domain.Foo",
                "Domain",
                target!,
                "Application",
                Reason));
        }

        await test.RunAsync();
    }
}
