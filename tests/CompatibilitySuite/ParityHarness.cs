using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;

namespace ArchitectureAnalyzer.CompatibilitySuite;

/// <summary>
/// The semantic categories the parity suite compares. Raw diagnostic IDs are never compared:
/// each side's IDs are projected onto this enum through the mapping table in
/// <c>docs/compatibility/psxrecomp-analyzer-baseline.md</c> §2.
/// </summary>
public enum ParityCategory
{
    /// <summary>PSXR001 / AARC004.</summary>
    MissingLayerDeclaration,

    /// <summary>PSXR002 / AARC005.</summary>
    MultipleLayerDeclarations,

    /// <summary>PSXR003 / AARC006.</summary>
    NamespaceLayerMismatch,

    /// <summary>PSXR004 / AARC002.</summary>
    ForbiddenDependency,

    /// <summary>PSXR005 / AARC003.</summary>
    ForbiddenApi,

    /// <summary>PSXR006 / AARC007.</summary>
    InteropBoundary,
}

/// <summary>
/// One analyzer diagnostic reduced to the facts parity is defined over: what kind of architectural
/// fault, how loudly, where, and about which symbols. Analyzer-specific wording is deliberately
/// gone by the time a finding exists.
/// </summary>
/// <param name="Category">The mapped semantic category.</param>
/// <param name="Severity">Effective severity as reported by the compilation.</param>
/// <param name="File">Source file path the diagnostic points at.</param>
/// <param name="Line">1-based line.</param>
/// <param name="Character">1-based column.</param>
/// <param name="Facts">The normalized information the message must carry.</param>
public sealed record SemanticFinding(
    ParityCategory Category,
    DiagnosticSeverity Severity,
    string File,
    int Line,
    int Character,
    string Facts)
{
    /// <summary>Renders the finding for assertion failure output.</summary>
    /// <returns>A single-line human-readable rendering.</returns>
    public override string ToString()
    {
        return $"{Category}[{Severity}] {File}({Line},{Character}) {{{Facts}}}";
    }
}

/// <summary>One source file of a fixture scenario.</summary>
/// <param name="Path">The compilation-visible file path; <c>.g.cs</c> and friends select the
/// generated-code paths of both analyzers.</param>
/// <param name="Source">The C# text.</param>
public sealed record FixtureSource(string Path, string Source);

/// <summary>The outcome of compiling one fixture through both analyzers.</summary>
/// <param name="Psxr">Findings produced by the frozen PSXRecomp.Analyzer baseline.</param>
/// <param name="Aarc">Findings produced by the current ArchitectureAnalyzer.</param>
/// <param name="UnmappedAarc">AARC diagnostics with no PSXR counterpart (AARC001/AARC008), which
/// must never appear in a parity scenario.</param>
public sealed record ParityResult(
    ImmutableArray<SemanticFinding> Psxr,
    ImmutableArray<SemanticFinding> Aarc,
    ImmutableArray<Diagnostic> UnmappedAarc)
{
    /// <summary>Findings the baseline reports that ArchitectureAnalyzer does not.</summary>
    public ImmutableArray<SemanticFinding> PsxrOnly => Subtract(Psxr, Aarc);

    /// <summary>Findings ArchitectureAnalyzer reports that the baseline does not.</summary>
    public ImmutableArray<SemanticFinding> AarcOnly => Subtract(Aarc, Psxr);

    /// <summary>Whether both analyzers produced exactly the same multiset of findings.</summary>
    public bool IsEquivalent => PsxrOnly.IsEmpty && AarcOnly.IsEmpty;

    /// <summary>Renders both sides and the difference for assertion failure output.</summary>
    /// <returns>A multi-line report.</returns>
    public string Describe()
    {
        var builder = new StringBuilder();
        builder.AppendLine("PSXR findings:");
        Append(builder, Psxr);
        builder.AppendLine("AARC findings:");
        Append(builder, Aarc);
        builder.AppendLine("PSXR-only (potential capability regression):");
        Append(builder, PsxrOnly);
        builder.AppendLine("AARC-only (potential added strictness):");
        Append(builder, AarcOnly);
        return builder.ToString();

        static void Append(StringBuilder target, ImmutableArray<SemanticFinding> findings)
        {
            if (findings.IsEmpty)
            {
                target.AppendLine("  (none)");
                return;
            }

            foreach (var finding in findings)
            {
                target.Append("  ").AppendLine(finding.ToString());
            }
        }
    }

    private static ImmutableArray<SemanticFinding> Subtract(
        ImmutableArray<SemanticFinding> left,
        ImmutableArray<SemanticFinding> right)
    {
        var remaining = right.ToList();
        var result = ImmutableArray.CreateBuilder<SemanticFinding>();
        foreach (var candidate in left)
        {
            var index = remaining.FindIndex(existing => SemanticEquivalent(candidate, existing));
            if (index < 0)
            {
                result.Add(candidate);
            }
            else
            {
                remaining.RemoveAt(index);
            }
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// Subset comparison over <see cref="SemanticFinding"/>s. Exact position parity holds for every
    /// category except <see cref="ParityCategory.ForbiddenDependency"/>: the frozen baseline
    /// deduplicates a forbidden source-target pair at whatever reference site wins a concurrent
    /// syntax-node action race, so its reported line/character is not stable across runs (F-D07)
    /// and cannot be part of a deterministic comparison. That pair's position carries no
    /// information the <c>Facts</c> do not, so the category is compared by
    /// <c>(Severity, File, Facts)</c> instead. <see cref="ArchitectureAnalyzer"/> deliberately
    /// reports the earliest site, which the parity suite could otherwise never verify against this
    /// baseline.
    /// </summary>
    private static bool SemanticEquivalent(SemanticFinding left, SemanticFinding right)
    {
        if (left.Category != right.Category
            || left.Severity != right.Severity
            || !string.Equals(left.File, right.File, StringComparison.Ordinal)
            || !string.Equals(left.Facts, right.Facts, StringComparison.Ordinal))
        {
            return false;
        }

        if (left.Category == ParityCategory.ForbiddenDependency)
        {
            return true;
        }

        return left.Line == right.Line && left.Character == right.Character;
    }
}

/// <summary>
/// Compiles a scenario through the frozen PSXRecomp.Analyzer baseline and the current
/// ArchitectureAnalyzer and reduces both outputs to comparable <see cref="SemanticFinding"/>s.
/// </summary>
public static class ParityHarness
{
    /// <summary>Diagnostic IDs the mapping table declares AARC-only; never expected in a fixture.</summary>
    public static readonly ImmutableArray<string> UnmappedAarcIds = ImmutableArray.Create("AARC001", "AARC008");

    private static readonly ConcurrentDictionary<string, (Regex Pattern, int[] ArgumentIndices)> MessagePatterns =
        new(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, ParityCategory> PsxrCategories =
        new Dictionary<string, ParityCategory>(StringComparer.Ordinal)
        {
            ["PSXR001"] = ParityCategory.MissingLayerDeclaration,
            ["PSXR002"] = ParityCategory.MultipleLayerDeclarations,
            ["PSXR003"] = ParityCategory.NamespaceLayerMismatch,
            ["PSXR004"] = ParityCategory.ForbiddenDependency,
            ["PSXR005"] = ParityCategory.ForbiddenApi,
            ["PSXR006"] = ParityCategory.InteropBoundary,
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, ParityCategory> AarcCategories =
        new Dictionary<string, ParityCategory>(StringComparer.Ordinal)
        {
            ["AARC004"] = ParityCategory.MissingLayerDeclaration,
            ["AARC005"] = ParityCategory.MultipleLayerDeclarations,
            ["AARC006"] = ParityCategory.NamespaceLayerMismatch,
            ["AARC002"] = ParityCategory.ForbiddenDependency,
            ["AARC003"] = ParityCategory.ForbiddenApi,
            ["AARC007"] = ParityCategory.InteropBoundary,
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static ImmutableArray<MetadataReference>? _references;
    private static readonly SemaphoreSlim ReferenceLock = new(1, 1);

    /// <summary>
    /// Runs one scenario through both analyzers.
    /// </summary>
    /// <param name="sources">Scenario sources; the baseline marker attribute definitions are added
    /// automatically.</param>
    /// <param name="editorConfig">Operational <c>.editorconfig</c> properties applied to the
    /// ArchitectureAnalyzer side only. <c>dotnet_diagnostic.&lt;ID&gt;.severity</c> entries are also
    /// projected onto the compilation's specific diagnostic options, which is what MSBuild does with
    /// a real <c>.editorconfig</c> severity pin.</param>
    /// <param name="contractJson">The contract handed to ArchitectureAnalyzer; defaults to the
    /// transcription of the baseline's compiled-in rules.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Both sides' findings.</returns>
    public static async Task<ParityResult> RunAsync(
        IEnumerable<FixtureSource> sources,
        IReadOnlyDictionary<string, string>? editorConfig = null,
        string? contractJson = null,
        CancellationToken cancellationToken = default)
    {
        if (sources is null)
        {
            throw new ArgumentNullException(nameof(sources));
        }

        var options = editorConfig ?? ImmutableDictionary<string, string>.Empty;
        var all = sources
            .Concat(new[] { new FixtureSource("/0/PSXRecompArchitectureAttributes.cs", BaselineContract.MarkerAttributeSource) })
            .ToImmutableArray();

        var compilation = await CreateCompilationAsync(all, options, cancellationToken).ConfigureAwait(false);

        var compilerErrors = compilation.GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
        if (!compilerErrors.IsEmpty)
        {
            throw new InvalidOperationException(
                "The fixture does not compile, so neither analyzer sees valid semantics: "
                + string.Join("; ", compilerErrors.Select(diagnostic => diagnostic.ToString())));
        }

        // The baseline analyzer reads no configuration at all: its .editorconfig at the pinned SHA
        // only pins PSXR001-006 to their existing default severity (baseline doc §1.3).
        var psxrDiagnostics = await AnalyzeAsync(
            compilation,
            new PSXRecomp.Analyzer.PSXRecompArchitectureAnalyzer(),
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty),
            cancellationToken).ConfigureAwait(false);

        var aarcOptions = new AnalyzerOptions(
            ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText(
                    ArchitectureContractAnalyzer.ContractFileName,
                    contractJson ?? BaselineContract.Json)),
            new InMemoryAnalyzerConfigOptionsProvider(options));

        var aarcDiagnostics = await AnalyzeAsync(
            compilation,
            new ArchitectureContractAnalyzer(),
            aarcOptions,
            cancellationToken).ConfigureAwait(false);

        return new ParityResult(
            Normalize(psxrDiagnostics, PsxrCategories),
            Normalize(aarcDiagnostics, AarcCategories),
            aarcDiagnostics.Where(diagnostic => UnmappedAarcIds.Contains(diagnostic.Id)).ToImmutableArray());
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        CSharpCompilation compilation,
        DiagnosticAnalyzer analyzer,
        AnalyzerOptions options,
        CancellationToken cancellationToken)
    {
        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create(analyzer), options);
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ImmutableArray<SemanticFinding> Normalize(
        ImmutableArray<Diagnostic> diagnostics,
        ImmutableDictionary<string, ParityCategory> categories)
    {
        return diagnostics
            .Where(diagnostic => categories.ContainsKey(diagnostic.Id))
            .Select(diagnostic =>
            {
                var span = diagnostic.Location.GetLineSpan();
                var category = categories[diagnostic.Id];
                return new SemanticFinding(
                    category,
                    diagnostic.Severity,
                    span.Path,
                    span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1,
                    ExtractFacts(category, diagnostic));
            })
            .OrderBy(finding => finding.File, StringComparer.Ordinal)
            .ThenBy(finding => finding.Line)
            .ThenBy(finding => finding.Character)
            .ThenBy(finding => finding.Category)
            .ThenBy(finding => finding.Facts, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    /// <summary>
    /// Projects a diagnostic's message arguments onto the facts parity is defined over. The two
    /// analyzers word their messages differently and carry different argument counts, so this
    /// selects the shared information rather than comparing strings (baseline doc §5 D8).
    /// </summary>
    private static string ExtractFacts(ParityCategory category, Diagnostic diagnostic)
    {
        var arguments = MessageArguments(diagnostic);
        var isPsxr = diagnostic.Id.StartsWith("PSXR", StringComparison.Ordinal);

        return category switch
        {
            // PSXR001 additionally lists the available attribute names; AARC004 puts that set in
            // the contract instead. Only the offending type is a shared fact.
            ParityCategory.MissingLayerDeclaration => Join(arguments[0]),

            ParityCategory.MultipleLayerDeclarations => Join(arguments[0], arguments[1]),

            // PSXR003 carries (declaredLayer, namespace, namespaceLayer); AARC006 prepends the
            // type name.
            ParityCategory.NamespaceLayerMismatch => isPsxr
                ? Join(arguments[0], arguments[1], arguments[2])
                : Join(arguments[1], arguments[2], arguments[3]),

            ParityCategory.ForbiddenDependency =>
                Join(arguments[0], arguments[1], arguments[2], arguments[3], arguments[4]),

            ParityCategory.ForbiddenApi => Join(arguments[0], arguments[1], arguments[2]),

            // PSXR006 renders the fully qualified signature, AARC007 only the method name
            // (baseline doc D8). Reduce both to the method name so the comparison asserts that the
            // same method is named, and record the qualification loss in the divergence catalogue.
            ParityCategory.InteropBoundary => Join(MethodName(arguments[0])),

            _ => throw new ArgumentOutOfRangeException(nameof(category), category, message: null),
        };

        static string Join(params string[] facts) => string.Join(" | ", facts);
    }

    private static string MethodName(string display)
    {
        var withoutParameters = display.Split('(')[0];
        var lastDot = withoutParameters.LastIndexOf('.');
        return lastDot < 0 ? withoutParameters : withoutParameters.Substring(lastDot + 1);
    }

    /// <summary>
    /// Recovers a diagnostic's message arguments by inverting its descriptor's message format.
    /// Roslyn does not expose <c>Diagnostic.Arguments</c> publicly, and re-deriving the facts from
    /// the rendered message is what lets a single comparison work for two analyzers whose message
    /// wording differs.
    /// </summary>
    internal static string[] MessageArguments(Diagnostic diagnostic)
    {
        var format = diagnostic.Descriptor.MessageFormat.ToString(CultureInfo.InvariantCulture);
        var (pattern, argumentIndices) = MessagePatterns.GetOrAdd(format, BuildPattern);

        var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
        var match = pattern.Match(message);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Message '{message}' does not match the format '{format}' of {diagnostic.Id}.");
        }

        var arguments = new string[argumentIndices.Length];
        for (var i = 0; i < argumentIndices.Length; i++)
        {
            arguments[argumentIndices[i]] = match.Groups[i + 1].Value;
        }

        return arguments;
    }

    private static (Regex Pattern, int[] ArgumentIndices) BuildPattern(string format)
    {
        var pattern = new StringBuilder("^");
        var indices = new List<int>();

        for (var i = 0; i < format.Length;)
        {
            if (format[i] == '{')
            {
                var close = format.IndexOf('}', i);
                if (close < 0)
                {
                    throw new InvalidOperationException($"Unbalanced '{{' in message format '{format}'.");
                }

                indices.Add(int.Parse(
                    format.Substring(i + 1, close - i - 1),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture));
                pattern.Append("(.*?)");
                i = close + 1;
                continue;
            }

            pattern.Append(Regex.Escape(format[i].ToString(CultureInfo.InvariantCulture)));
            i++;
        }

        pattern.Append('$');
        return (new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), indices.ToArray());
    }

    private static async Task<CSharpCompilation> CreateCompilationAsync(
        ImmutableArray<FixtureSource> sources,
        IReadOnlyDictionary<string, string> editorConfig,
        CancellationToken cancellationToken)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = sources.Select(source => CSharpSyntaxTree.ParseText(
            SourceText.From(source.Source, Encoding.UTF8),
            parseOptions,
            path: source.Path,
            cancellationToken: cancellationToken));

        var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithSpecificDiagnosticOptions(SeverityPins(editorConfig));

        return CSharpCompilation.Create(
            "ParityScenario",
            trees,
            await ResolveReferencesAsync(cancellationToken).ConfigureAwait(false),
            compilationOptions);
    }

    /// <summary>
    /// Translates <c>dotnet_diagnostic.&lt;ID&gt;.severity</c> entries into compilation-level
    /// severity overrides, mirroring how the compiler applies a real <c>.editorconfig</c> pin.
    /// </summary>
    private static ImmutableDictionary<string, ReportDiagnostic> SeverityPins(
        IReadOnlyDictionary<string, string> editorConfig)
    {
        var pins = ImmutableDictionary.CreateBuilder<string, ReportDiagnostic>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in editorConfig)
        {
            const string Prefix = "dotnet_diagnostic.";
            const string Suffix = ".severity";
            if (!entry.Key.StartsWith(Prefix, StringComparison.Ordinal)
                || !entry.Key.EndsWith(Suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var id = entry.Key.Substring(Prefix.Length, entry.Key.Length - Prefix.Length - Suffix.Length);
            pins[id] = entry.Value.ToLowerInvariant() switch
            {
                "error" => ReportDiagnostic.Error,
                "warning" => ReportDiagnostic.Warn,
                "suggestion" => ReportDiagnostic.Info,
                "silent" => ReportDiagnostic.Hidden,
                "none" => ReportDiagnostic.Suppress,
                _ => ReportDiagnostic.Default,
            };
        }

        return pins.ToImmutable();
    }

    private static async Task<ImmutableArray<MetadataReference>> ResolveReferencesAsync(
        CancellationToken cancellationToken)
    {
        if (_references is { } cached)
        {
            return cached;
        }

        await ReferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _references ??= await ReferenceAssemblies.Net.Net90
                .ResolveAsync(LanguageNames.CSharp, cancellationToken)
                .ConfigureAwait(false);
            return _references.Value;
        }
        finally
        {
            ReferenceLock.Release();
        }
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text, Encoding.UTF8);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    private sealed class InMemoryAnalyzerConfigOptions : AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> _values;

        public InMemoryAnalyzerConfigOptions(IReadOnlyDictionary<string, string> values) => _values = values;

        public override bool TryGetValue(string key, out string value)
        {
            if (_values.TryGetValue(key, out var found))
            {
                value = found;
                return true;
            }

            value = null!;
            return false;
        }
    }

    private sealed class InMemoryAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _options;

        public InMemoryAnalyzerConfigOptionsProvider(IReadOnlyDictionary<string, string> values)
        {
            _options = new InMemoryAnalyzerConfigOptions(values);
        }

        public override AnalyzerConfigOptions GlobalOptions => _options;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;
    }
}
