using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace ArchitectureAnalyzer.Tests.TestInfrastructure;

/// <summary>
/// Shared harness for <see cref="ArchitectureContractAnalyzer"/> tests.
/// </summary>
/// <remarks>
/// Unlike an attribute-driven analyzer, the contract <em>is</em> the test input here, so the
/// contract JSON is supplied per test as an <c>AdditionalFiles</c> entry rather than baked into
/// the harness.
/// </remarks>
public sealed class ArchitectureAnalyzerTest : CSharpAnalyzerTest<ArchitectureContractAnalyzer, XUnit29Verifier>
{
    /// <summary>Path the testing SDK assigns to the single scenario source file.</summary>
    public const string ScenarioFilePath = "/0/Test0.cs";

    /// <summary>Creates a harness with no contract file at all (the opt-in no-op case).</summary>
    public ArchitectureAnalyzerTest()
    {
        ReferenceAssemblies = ReferenceAssemblies.Net.Net90;
    }

    /// <summary>Creates a harness whose AdditionalFiles carry the given contract.</summary>
    /// <param name="contractJson">Raw contract JSON.</param>
    /// <param name="fileName">Contract file name; defaults to the conventional name.</param>
    public ArchitectureAnalyzerTest(string contractJson, string fileName = ArchitectureContractAnalyzer.ContractFileName)
        : this()
    {
        TestState.AdditionalFiles.Add((fileName, contractJson));
    }

    /// <summary>
    /// Builds an expected diagnostic anchored at a line/column of the scenario source file.
    /// </summary>
    /// <param name="descriptor">The expected descriptor.</param>
    /// <param name="line">1-based line.</param>
    /// <param name="column">1-based column.</param>
    /// <param name="arguments">Expected message arguments.</param>
    /// <returns>The expected diagnostic.</returns>
    public static DiagnosticResult Expect(DiagnosticDescriptor descriptor, int line, int column, params object[] arguments)
    {
        return new DiagnosticResult(descriptor)
            .WithLocation(ScenarioFilePath, line, column)
            .WithArguments(arguments);
    }

    /// <summary>
    /// Builds an expected diagnostic that carries no source location (reported at compilation end).
    /// </summary>
    /// <param name="descriptor">The expected descriptor.</param>
    /// <param name="arguments">Expected message arguments.</param>
    /// <returns>The expected diagnostic.</returns>
    public static DiagnosticResult ExpectNoLocation(DiagnosticDescriptor descriptor, params object[] arguments)
    {
        return new DiagnosticResult(descriptor).WithNoLocation().WithArguments(arguments);
    }
}
