using System.Collections.Immutable;
using System.Diagnostics;
using ArchitectureAnalyzer.Contract;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ArchitectureAnalyzer.PerformanceBenchmark;

internal static class Program
{
    private const int ProjectCount = 3;
    private const int FilesPerProject = 100;
    private const int MeasurementIterations = 3;
    private const int LoaderParsesPerIteration = 1_000;

    // CI budgets are intentionally much wider than the measured baseline. They are a smoke guard
    // for order-of-magnitude regressions, not a microbenchmark assertion against shared runners.
    private const double LoaderBudgetMs = 500;
    private const double DependencyBudgetMs = 2_500;
    private const double ForbiddenApiBudgetMs = 3_000;
    private const double FullBudgetMs = 3_000;

    private static readonly ImmutableArray<MetadataReference> References = CreateFrameworkReferences();

    private static async Task<int> Main(string[] args)
    {
        var enforceBudgets = args.Contains("--ci", StringComparer.Ordinal);
        Console.WriteLine("ArchitectureAnalyzer representative performance benchmark");
        Console.WriteLine(
            $"workload: {ProjectCount} compilations x {FilesPerProject} source files "
            + $"({ProjectCount * FilesPerProject} files total), {MeasurementIterations} measured iterations");
        Console.WriteLine($"mode: {(enforceBudgets ? "CI regression guard" : "report only")}");
        Console.WriteLine();

        // Warm the JIT and Roslyn paths before recording medians.
        _ = ArchitectureContractLoader.Load(FullContract);
        var warmup = CreateWorkloads(SourceShape.Full);
        await RunAnalyzerScenarioAsync(warmup, FullContract).ConfigureAwait(false);

        var loaderMedian = MeasureMedian(() =>
        {
            for (var index = 0; index < LoaderParsesPerIteration; index++)
            {
                var result = ArchitectureContractLoader.Load(FullContract);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(result.ErrorReason);
                }
            }

            return 0;
        });

        var dependencyWorkloads = CreateWorkloads(SourceShape.Dependency);
        var dependencyResult = await MeasureAnalyzerMedianAsync(
            dependencyWorkloads,
            DependencyContract).ConfigureAwait(false);

        var apiWorkloads = CreateWorkloads(SourceShape.ForbiddenApi);
        var apiResult = await MeasureAnalyzerMedianAsync(
            apiWorkloads,
            ForbiddenApiContract).ConfigureAwait(false);

        var fullWorkloads = CreateWorkloads(SourceShape.Full);
        var fullResult = await MeasureAnalyzerMedianAsync(
            fullWorkloads,
            FullContract).ConfigureAwait(false);

        PrintMetric(
            $"contract loader ({LoaderParsesPerIteration} parses)",
            loaderMedian,
            LoaderBudgetMs);
        PrintMetric(
            $"dependency analysis ({ProjectCount * FilesPerProject} files)",
            dependencyResult.MedianMs,
            DependencyBudgetMs,
            dependencyResult.Diagnostics);
        PrintMetric(
            $"forbidden-API analysis ({ProjectCount * FilesPerProject} files)",
            apiResult.MedianMs,
            ForbiddenApiBudgetMs,
            apiResult.Diagnostics);
        PrintMetric(
            $"full schema-v5 analyzer ({ProjectCount * FilesPerProject} files)",
            fullResult.MedianMs,
            FullBudgetMs,
            fullResult.Diagnostics);

        Console.WriteLine();
        Console.WriteLine(
            "structural guard: architecture.contract.json was read exactly once per compilation "
            + "for every measured analyzer run");

        if (!enforceBudgets)
        {
            return 0;
        }

        var failures = new List<string>();
        CheckBudget("contract loader", loaderMedian, LoaderBudgetMs, failures);
        CheckBudget("dependency analysis", dependencyResult.MedianMs, DependencyBudgetMs, failures);
        CheckBudget("forbidden-API analysis", apiResult.MedianMs, ForbiddenApiBudgetMs, failures);
        CheckBudget("full analyzer", fullResult.MedianMs, FullBudgetMs, failures);

        if (failures.Count == 0)
        {
            Console.WriteLine("performance guard: PASS");
            return 0;
        }

        Console.Error.WriteLine("performance guard: FAIL");
        foreach (var failure in failures)
        {
            Console.Error.WriteLine("  " + failure);
        }

        return 1;
    }

    private static void CheckBudget(
        string name,
        double measuredMs,
        double budgetMs,
        ICollection<string> failures)
    {
        if (measuredMs > budgetMs)
        {
            failures.Add(
                $"{name} median {measuredMs:F1} ms exceeded the CI budget {budgetMs:F0} ms");
        }
    }

    private static void PrintMetric(
        string name,
        double medianMs,
        double budgetMs,
        int? diagnostics = null)
    {
        var diagnosticsText = diagnostics is null ? string.Empty : $", diagnostics/run={diagnostics.Value}";
        Console.WriteLine(
            $"{name}: median={medianMs:F1} ms, CI budget={budgetMs:F0} ms{diagnosticsText}");
    }

    private static double MeasureMedian(Func<int> action)
    {
        var samples = new double[MeasurementIterations];
        for (var iteration = 0; iteration < MeasurementIterations; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            _ = action();
            stopwatch.Stop();
            samples[iteration] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        return samples[samples.Length / 2];
    }

    private static async Task<(double MedianMs, int Diagnostics)> MeasureAnalyzerMedianAsync(
        ImmutableArray<CompilationWorkload> workloads,
        string contract)
    {
        var samples = new double[MeasurementIterations];
        var expectedDiagnostics = -1;

        for (var iteration = 0; iteration < MeasurementIterations; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await RunAnalyzerScenarioAsync(workloads, contract).ConfigureAwait(false);
            stopwatch.Stop();

            samples[iteration] = stopwatch.Elapsed.TotalMilliseconds;
            if (expectedDiagnostics < 0)
            {
                expectedDiagnostics = result;
            }
            else if (expectedDiagnostics != result)
            {
                throw new InvalidOperationException(
                    $"diagnostic count changed between benchmark iterations: "
                    + $"{expectedDiagnostics} -> {result}");
            }
        }

        Array.Sort(samples);
        return (samples[samples.Length / 2], expectedDiagnostics);
    }

    private static async Task<int> RunAnalyzerScenarioAsync(
        ImmutableArray<CompilationWorkload> workloads,
        string contract)
    {
        var diagnosticCount = 0;
        foreach (var workload in workloads)
        {
            var additionalText = new CountingAdditionalText(
                ArchitectureContractLoader.ContractFileName,
                contract);
            var analyzerOptions = new AnalyzerOptions(
                ImmutableArray.Create<AdditionalText>(additionalText));
            var analyzer = new ArchitectureContractAnalyzer();
            var options = new CompilationWithAnalyzersOptions(
                analyzerOptions,
                onAnalyzerException: null,
                concurrentAnalysis: true,
                logAnalyzerExecutionTime: false,
                reportSuppressedDiagnostics: false);

            var diagnostics = await workload.Compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer), options)
                .GetAnalyzerDiagnosticsAsync()
                .ConfigureAwait(false);

            if (additionalText.ReadCount != 1)
            {
                throw new InvalidOperationException(
                    $"{workload.Name}: contract was read {additionalText.ReadCount} times; "
                    + "expected exactly once per compilation");
            }

            diagnosticCount += diagnostics.Length;
        }

        return diagnosticCount;
    }

    private static ImmutableArray<CompilationWorkload> CreateWorkloads(SourceShape shape)
    {
        var builder = ImmutableArray.CreateBuilder<CompilationWorkload>(ProjectCount);
        for (var projectIndex = 0; projectIndex < ProjectCount; projectIndex++)
        {
            var trees = ImmutableArray.CreateBuilder<SyntaxTree>(FilesPerProject);
            for (var fileIndex = 0; fileIndex < FilesPerProject; fileIndex++)
            {
                var source = CreateSource(projectIndex, fileIndex, shape);
                trees.Add(CSharpSyntaxTree.ParseText(
                    source,
                    new CSharpParseOptions(LanguageVersion.Latest),
                    path: $"/benchmark/Project{projectIndex}/File{fileIndex:D3}.cs"));
            }

            var compilation = CSharpCompilation.Create(
                assemblyName: $"ArchitectureBenchmark.Project{projectIndex}",
                syntaxTrees: trees.ToImmutable(),
                references: References,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithOptimizationLevel(OptimizationLevel.Release)
                    .WithConcurrentBuild(true));

            builder.Add(new CompilationWorkload($"Project{projectIndex}", compilation));
        }

        return builder.ToImmutable();
    }

    private static string CreateSource(int projectIndex, int fileIndex, SourceShape shape)
    {
        var suffix = $"P{projectIndex}F{fileIndex:D3}";
        var dependency = shape is SourceShape.Dependency or SourceShape.Full
            ? $"public Bench.Application.AppService{suffix}? Outer {{ get; set; }}"
            : string.Empty;
        var api = shape is SourceShape.ForbiddenApi or SourceShape.Full
            ? "public void Log() { System.Console.WriteLine(\"benchmark\"); }"
            : "public int Compute() => 42;";

        return $$"""
            namespace Bench.Shared
            {
                public sealed class SharedValue{{suffix}}
                {
                }
            }

            namespace Bench.Application
            {
                public sealed class AppService{{suffix}}
                {
                    public Bench.Domain.DomainEntity{{suffix}}? Entity { get; set; }
                }
            }

            namespace Bench.Domain
            {
                public sealed class DomainEntity{{suffix}}
                {
                    public Bench.Shared.SharedValue{{suffix}}? Value { get; set; }
                    {{dependency}}
                    {{api}}
                }
            }
            """;
    }

    private static ImmutableArray<MetadataReference> CreateFrameworkReferences()
    {
        var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrWhiteSpace(trustedAssemblies))
        {
            throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES is unavailable.");
        }

        return trustedAssemblies
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }

    private const string DependencyContract = """
        {
          "schemaVersion": 5,
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Bench.Domain" ] },
            { "name": "Application", "namespaceRoots": [ "Bench.Application" ] },
            { "name": "Shared", "namespaceRoots": [ "Bench.Shared" ] }
          ],
          "forbiddenDependencies": [
            {
              "from": "Domain",
              "to": "Application",
              "reason": "Benchmark dependency violation."
            }
          ]
        }
        """;

    private const string ForbiddenApiContract = """
        {
          "schemaVersion": 5,
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Bench.Domain" ] },
            { "name": "Application", "namespaceRoots": [ "Bench.Application" ] },
            { "name": "Shared", "namespaceRoots": [ "Bench.Shared" ] }
          ],
          "forbiddenApis": [
            {
              "layer": "Domain",
              "type": "System.Console",
              "member": "WriteLine",
              "reason": "Benchmark forbidden API."
            }
          ]
        }
        """;

    private const string FullContract = """
        {
          "schemaVersion": 5,
          "unclassifiedCode": "error",
          "layers": [
            { "name": "Domain", "namespaceRoots": [ "Bench.Domain" ] },
            { "name": "Application", "namespaceRoots": [ "Bench.Application" ] },
            { "name": "Shared", "namespaceRoots": [ "Bench.Shared" ] }
          ],
          "forbiddenDependencies": [
            {
              "from": "Domain",
              "to": "Application",
              "reason": "Benchmark dependency violation."
            }
          ],
          "allowedDependencies": [
            { "from": "Application", "to": [ "Domain" ] },
            { "from": "Domain", "to": [ "Shared" ] },
            { "from": "Shared", "to": [] }
          ],
          "forbiddenApis": [
            {
              "layer": "Domain",
              "type": "System.Console",
              "member": "WriteLine",
              "reason": "Benchmark forbidden API."
            }
          ],
          "dependencyGraph": {
            "requireAcyclic": true
          },
          "exceptions": []
        }
        """;

    private sealed record CompilationWorkload(string Name, CSharpCompilation Compilation);

    private sealed class CountingAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        internal CountingAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content);
        }

        public override string Path { get; }

        internal int ReadCount { get; private set; }

        public override SourceText? GetText(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return _text;
        }
    }

    private enum SourceShape
    {
        Dependency,
        ForbiddenApi,
        Full,
    }
}
