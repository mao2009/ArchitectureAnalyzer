using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ArchitectureAnalyzer.CompatibilitySuite;

/// <summary>
/// Cross-analyzer parity suite (#33): every scenario of the baseline fixture matrix is compiled
/// once and analyzed by both the frozen PSXRecomp.Analyzer baseline and the current
/// ArchitectureAnalyzer, and the two outputs are compared semantically.
/// </summary>
public sealed class ParityTests
{
    /// <summary>
    /// The operational configuration the default parity run uses. The baseline's own
    /// <c>.editorconfig</c> pins PSXR001-006 to <c>error</c> (baseline doc §1.3); AARC006 defaults
    /// to <c>Warning</c>, so the equivalent pin is the one operational entry a migrating project
    /// needs (baseline doc D7). Everything else is left at its default.
    /// </summary>
    private static readonly ImmutableDictionary<string, string> BaselineEditorConfig =
        ImmutableDictionary<string, string>.Empty.Add(BaselineContract.Aarc006SeverityPinKey, "error");

    /// <summary>Fixture IDs, for xunit's serializable theory data.</summary>
    public static TheoryData<string> FixtureIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var fixture in ParityFixtures.All)
            {
                data.Add(fixture.Id);
            }

            return data;
        }
    }

    [Fact]
    public void MatrixCoverageIsComplete()
    {
        var ids = ParityFixtures.All.Select(fixture => fixture.Id).ToImmutableArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(57, ids.Length);

        // Section totals from the baseline document §4.
        Assert.Equal(15, ids.Count(id => id.StartsWith("F-M", StringComparison.Ordinal)));
        Assert.Equal(2, ids.Count(id => id.StartsWith("F-P", StringComparison.Ordinal) || id.StartsWith("F-X", StringComparison.Ordinal)));
        Assert.Equal(4, ids.Count(id => id.StartsWith("F-N", StringComparison.Ordinal)));
        Assert.Equal(11, ids.Count(id => id.StartsWith("F-D", StringComparison.Ordinal)));
        Assert.Equal(19, ids.Count(id => id.StartsWith("F-A", StringComparison.Ordinal)));
        Assert.Equal(6, ids.Count(id => id.StartsWith("F-I", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public async Task ScenarioIsAccountedFor(string fixtureId)
    {
        var fixture = ParityFixtures.All.Single(candidate => candidate.Id == fixtureId);
        var result = await ParityHarness.RunAsync(fixture.Sources, BaselineEditorConfig);

        AssertNoUnmappedDiagnostics(fixture.Id, result);

        // Violation detected / valid stays clean, asserted on both sides independently of the diff.
        if (fixture.Kind == FixtureKind.Valid)
        {
            Assert.True(
                result.Psxr.IsEmpty && result.Aarc.IsEmpty,
                $"{fixture.Id} is a clean scenario but produced findings.\n{result.Describe()}");
        }
        else
        {
            Assert.False(
                result.Psxr.IsEmpty,
                $"{fixture.Id} is a violation scenario but the baseline reported nothing. "
                + "The fixture no longer reproduces the baseline behaviour it was written for.");
        }

        AssertDeclaredDivergence(fixture.Id, fixture.Divergence, result);
    }

    [Fact]
    public async Task EveryViolationIsStillDetectedByArchitectureAnalyzer()
    {
        // The headline compatibility claim: an architectural fault the baseline catches must not
        // become invisible. Exceptions are the fixtures whose divergence is declared and classified.
        var undetected = new List<string>();

        foreach (var fixture in ParityFixtures.All.Where(candidate => candidate.Kind == FixtureKind.Violation))
        {
            var result = await ParityHarness.RunAsync(fixture.Sources, BaselineEditorConfig);
            if (result.Aarc.IsEmpty)
            {
                undetected.Add($"{fixture.Id} ({fixture.Divergence.Class})");
            }
        }

        // The one former blind spot, F-M13 (baseline doc D4), was closed by the merged #42:
        // AARC004 now prefers the handwritten part, so no violation detected by the baseline
        // escapes the suite.
        Assert.Empty(undetected);
    }

    [Fact]
    public async Task RequireLayerDeclarationOverride_RelaxesOnlyTheDeclarationRule()
    {
        var editorConfig = BaselineEditorConfig.Add(
            "dotnet_diagnostic.AARC002.architecture_analyzer.require_layer_declaration", "false");

        // The declaration rule is the one the toggle governs: AARC004 goes quiet, PSXR001 does not.
        var declaration = await ParityHarness.RunAsync(
            Fixture("F-M02").Sources, editorConfig);
        Assert.Empty(declaration.Aarc);
        Assert.Equal(
            new[] { ParityCategory.MissingLayerDeclaration },
            declaration.Psxr.Select(finding => finding.Category));

        // Every other rule keeps parity under the same override.
        foreach (var id in new[] { "F-D01", "F-A01", "F-I01" })
        {
            var result = await ParityHarness.RunAsync(
                Fixture(id).Sources, editorConfig);
            Assert.True(result.IsEquivalent, $"{id} lost parity under require_layer_declaration=false.\n{result.Describe()}");
        }
    }

    [Fact]
    public async Task ValidateNamespaceLayerOverride_RestoresParityForAContractThatOptedOut()
    {
        // A contract that does not opt in to namespace consistency loses the PSXR003 equivalent...
        const string OptedOut = "\"validateNamespaceConsistency\": true";
        var contract = BaselineContract.Json.Replace(OptedOut, "\"validateNamespaceConsistency\": false", StringComparison.Ordinal);
        Assert.DoesNotContain(OptedOut, contract, StringComparison.Ordinal);

        var withoutToggle = await ParityHarness.RunAsync(
            Fixture("F-N01").Sources, BaselineEditorConfig, contract);
        Assert.Empty(withoutToggle.Aarc);
        Assert.Equal(
            new[] { ParityCategory.NamespaceLayerMismatch },
            withoutToggle.Psxr.Select(finding => finding.Category));

        // ...and the operational toggle brings it back without touching the contract.
        var withToggle = await ParityHarness.RunAsync(
            Fixture("F-N01").Sources,
            BaselineEditorConfig.Add("dotnet_diagnostic.AARC002.architecture_analyzer.validate_namespace_layer", "true"),
            contract);
        Assert.True(withToggle.IsEquivalent, withToggle.Describe());
    }

    [Fact]
    public async Task Aarc006SeverityPin_IsRequiredToReproduceBaselineStrictness()
    {
        // Without the pin the same fault is found, but only as a warning: a build that used to
        // break now merely complains (baseline doc D7).
        var unpinned = await ParityHarness.RunAsync(
            Fixture("F-N01").Sources);
        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(unpinned.Psxr).Severity);
        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(unpinned.Aarc).Severity);
        Assert.False(unpinned.IsEquivalent);

        var pinned = await ParityHarness.RunAsync(
            Fixture("F-N01").Sources, BaselineEditorConfig);
        Assert.True(pinned.IsEquivalent, pinned.Describe());
    }

    private static ParityFixture Fixture(string id) =>
        ParityFixtures.All.Single(candidate => candidate.Id == id);

    private static void AssertNoUnmappedDiagnostics(string id, ParityResult result)
    {
        Assert.True(
            result.UnmappedAarc.IsEmpty,
            $"{id} produced AARC diagnostics that have no baseline counterpart and must never "
            + "appear in a parity scenario (a broken contract or an invalid operational value): "
            + string.Join("; ", result.UnmappedAarc.Select(diagnostic => diagnostic.ToString())));
    }

    private static void AssertDeclaredDivergence(string id, ExpectedDivergence declared, ParityResult result)
    {
        var psxrOnly = Categories(result.PsxrOnly);
        var aarcOnly = Categories(result.AarcOnly);

        Assert.True(
            declared.PsxrOnly == psxrOnly && declared.AarcOnly == aarcOnly,
            $"{id}: observed difference does not match the classified one.\n"
            + $"declared   PSXR-only=[{declared.PsxrOnly}] AARC-only=[{declared.AarcOnly}] ({declared.Class})\n"
            + $"observed   PSXR-only=[{psxrOnly}] AARC-only=[{aarcOnly}]\n"
            + result.Describe()
            + (declared.Note.Length == 0 ? string.Empty : "\nclassification: " + declared.Note));

        var hasDifference = psxrOnly.Length > 0 || aarcOnly.Length > 0;
        Assert.Equal(hasDifference, declared.Class != DivergenceClass.None);
    }

    private static string Categories(ImmutableArray<SemanticFinding> findings) =>
        string.Join(
            ",",
            findings.Select(finding => finding.Category.ToString()).OrderBy(name => name, StringComparer.Ordinal));
}
