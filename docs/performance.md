# Performance

This document defines the representative performance workload and the regression policy for
ArchitectureAnalyzer. It is intentionally separate from correctness tests: the analyzer must never
trade diagnostic correctness for a benchmark number.

## Goals

The performance guard answers four questions:

1. Is the Architecture Contract parsed/read once per compilation rather than once per syntax,
   symbol or operation callback?
2. What does dependency-direction analysis cost on a representative large source set?
3. What does forbidden-API analysis cost on the same source scale?
4. Does the complete current schema remain comfortably within a broad CI budget?

The benchmark is not intended to be a stable machine-to-machine microbenchmark. GitHub-hosted
runners vary, so CI uses broad order-of-magnitude budgets and relies on deterministic structural
assertions for the most important caching invariant.

## Representative workload

`tests/PerformanceBenchmark` generates an in-memory synthetic consumer with:

- 3 independent C# compilations, representing 3 projects;
- 100 source files per compilation;
- 300 source files total;
- 3 declared architecture layers: Domain, Application and Shared;
- roughly 900 source types total;
- direct cross-layer symbol references;
- operation blocks that exercise forbidden-API matching.

Sources are parsed and compilations are built **before** the timed analyzer section. The measured
analyzer scenarios therefore do not include source generation or syntax parsing.

The runner performs one warm-up and then records three measured iterations, reporting the median.

## Scenarios

### Contract loader

The current schema-v5 representative contract is parsed 1,000 times in one measured sample.
This isolates JSON/schema loading from Roslyn analysis.

### Dependency analysis

The 300-file workload contains one Domain -> Application violation per source file. The contract
contains a matching forbidden dependency rule and no forbidden API rule. This exercises symbol
resolution, layer resolution, pair de-duplication and AARC002 reporting.

### Forbidden-API analysis

The 300-file workload contains one `System.Console.WriteLine` usage per Domain type. The contract
contains a member-specific forbidden API rule and no dependency restriction. This exercises
operation traversal, symbol matching and AARC003 reporting.

### Full schema-v5 analyzer

The 300-file workload combines dependency and forbidden-API paths under:

- strict `unclassifiedCode=error` coverage;
- explicit denylisting;
- positive `allowedDependencies`;
- schema-v4 DAG validation;
- schema-v5 exception support.

This is the broad regression smoke test for the current analyzer shape.

## Structural regression guard

The benchmark wraps `architecture.contract.json` in a counting `AdditionalText` and fails unless
`GetText()` is called exactly once for each compilation analysis.

`PerformanceStructureTests.ContractAdditionalFile_IsReadOncePerCompilationSnapshot` makes the
same invariant part of the ordinary unit-test suite using 200 source files. It also models an IDE
edit by replacing one syntax tree and analyzing the resulting immutable compilation snapshot.

Expected behavior is:

- first compilation snapshot: one contract read;
- second snapshot after one source edit: one additional contract read;
- never one read per callback/source file.

A global cache across compilation snapshots is deliberately **not** used: an AdditionalFile may
change between snapshots, so the safe boundary is one parse per compilation.

## CI budgets

The benchmark runner accepts `--ci`. In that mode these medians are hard upper bounds:

| Metric | CI budget |
|---|---:|
| 1,000 contract parses | 500 ms |
| dependency analysis, 300 files | 2,500 ms |
| forbidden-API analysis, 300 files | 3,000 ms |
| full schema-v5 analyzer, 300 files | 3,000 ms |

These limits are intentionally much wider than the expected baseline. Their purpose is to catch a
major regression such as repeated contract parsing, a new full-compilation walk, accidental
quadratic behavior or a drastic callback explosion without making CI flaky because a shared runner
is temporarily slower.

The first green GitHub Actions run for this benchmark is recorded below. The budgets were then
tightened to retain roughly five-times-or-more headroom over that run: wide enough for shared-runner
variance, but narrow enough to catch accidental repeated scans or nonlinear behavior.

## Baseline

Baseline source: GitHub Actions CI run **36829119921**, Ubuntu hosted runner, .NET SDK 10.0.302,
2026-10-01. These are medians from three measured iterations after one warm-up.

| Metric | Baseline median | Diagnostics per analyzer run |
|---|---:|---:|
| 1,000 contract parses | 59.4 ms | n/a |
| dependency analysis, 300 files | 471.6 ms | 300 |
| forbidden-API analysis, 300 files | 534.6 ms | 300 |
| full schema-v5 analyzer, 300 files | 558.6 ms | 600 |

The absolute values are not promises for other machines. They are a reproducible CI reference for
this exact synthetic workload and a calibration point for the broad regression budgets above.

## Performance-sensitive implementation constraints

Changes to the analyzer should preserve these properties unless measurements justify a different
design:

- parse/load the Architecture Contract once in `RegisterCompilationStartAction`;
- capture immutable validated contract data in registered callbacks;
- use targeted Roslyn callbacks rather than manually walking the whole compilation;
- keep DAG/cycle work on the small contract graph, not the source graph;
- register declaration/coverage/interop callbacks only when the corresponding policy is active;
- do not add a source scan merely to inventory exceptions;
- keep deterministic de-duplication at compilation end where concurrency would otherwise make
  diagnostic locations unstable.

When a benchmark reveals a problem, optimize only after reproducing it and retain correctness tests
for the affected behavior.

## Running locally

Report metrics without enforcing budgets:

```bash
dotnet run --project tests/PerformanceBenchmark/ArchitectureAnalyzer.PerformanceBenchmark.csproj -c Release
```

Run the same guard used by CI:

```bash
dotnet run --project tests/PerformanceBenchmark/ArchitectureAnalyzer.PerformanceBenchmark.csproj -c Release -- --ci
```

Machine-to-machine numbers should not be compared as if this were BenchmarkDotNet-grade
microbenchmarking. Compare trends on the same machine or use the GitHub Actions baseline and broad
budgets above.
