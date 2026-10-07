# ArchitectureAnalyzer

**Make your architecture document compile.** ArchitectureAnalyzer is a Roslyn analyzer for C#
projects that reads a small JSON *Architecture Contract* from your repository and enforces it as
compiler errors. You declare your layers, which dependency directions are forbidden between them,
and which APIs each layer may not touch; every `dotnet build` — yours, your teammates', your
IDE's background build, and CI's — checks the code against that declaration. The analyzer itself
contains no layer names, no namespace roots and no API rules: it is a generic interpreter for
whatever contract the consuming project ships.

## What is an "architecture analyzer", and what does it prevent?

A written architecture — an ADR, a `docs/architecture.md`, a layer diagram — records intent. It
does not check anything. Nothing keeps the code and the document in sync except a human noticing
during review, which is optional, inconsistent, and the first thing skipped under deadline
pressure. So the two drift, and by the time anyone measures the gap, the "architecture" is a
document describing a system that no longer exists.

An architecture analyzer closes that gap by making the declaration executable. The class of
problems it prevents is specifically *structural erosion*:

- an inner layer quietly reaching out to an outer one ("just this once, to get the release out")
- non-determinism or I/O creeping into a Domain layer that was supposed to be pure
- a rule that everyone agreed on in a design review and nobody remembers two quarters later
- new contributors who have never read the ADR and have no way to discover the rule from the code

Compile-time enforcement was chosen over a pre-commit hook or a CI-only lint because it cannot be
skipped (`--no-verify` doesn't apply), it is immediate (the error appears in the editor, not in a
CI log ten minutes later), and it needs no new infrastructure. See
[`docs/design.md`](docs/design.md) for the full rationale.

## Pipeline

```
   Architecture Contract          Analyzer                Roslyn Diagnostic         dotnet build            CI Gate
 architecture.contract.json  ->  ArchitectureContract  ->  AARC001 .. AARC012  -> compilation fails  ->  workflow fails
 (JSON, in your repo,             Analyzer                 (Error, except          with an error           on non-zero exit
  under code review)          (generic; no rules            AARC006/008/009)      at the exact line
                               of its own)
```

The contract is the single source of truth. Changing what is enforced means editing JSON in your
own repository — never changing or rebuilding the analyzer.

**ArchitectureAnalyzer is project-agnostic.** It ships no layer names, no namespace roots, no API
list and no attribute names; every one of those comes from the consuming project's contract. It is
not built for, tuned to, or coupled with any particular codebase.
[PSXRecompStudio](https://github.com/mao2009/PSXRecompStudio) appears in these docs only as the
first consumer — an example of what a contract can express, never a requirement or an assumption
baked into the analyzer.

## Usage

### 1. Write a contract

`architecture.contract.json`, next to the code it governs:

```json
{
  "schemaVersion": 5,
  "unclassifiedCode": "error",
  "layers": [
    { "name": "Domain", "namespaceRoots": [ "MyApp.Domain" ] },
    { "name": "Application", "namespaceRoots": [ "MyApp.Application" ] }
  ],
  "forbiddenDependencies": [
    { "from": "Domain", "to": "Application", "reason": "Domain must not depend on the outer Application layer." }
  ],
  "allowedDependencies": [
    { "from": "Application", "to": [ "Domain" ], "reason": "Application may depend only on Domain." }
  ],
  "dependencyGraph": {
    "requireAcyclic": true
  },
  "forbiddenApis": [
    { "layer": "Domain", "type": "System.Console", "reason": "Console I/O must be abstracted behind an Infrastructure adapter." }
  ],
  "exceptions": [
    {
      "diagnosticId": "AARC002",
      "sourceType": "MyApp.Domain.LegacyBridge",
      "targetType": "MyApp.Application.LegacyService",
      "justification": "Temporary compatibility bridge tracked by ARCH-123."
    }
  ]
}
```

Layers are the minimum. Forbidden edges/APIs, positive dependency allowlists
(`allowedDependencies`), attribute-based layer declaration (`layerDeclaration`), interop
boundaries (`interopBoundaryRules`), strict coverage, declared-graph DAG enforcement and
schema-v5 justified exceptions are optional policies documented in [`docs/architecture.md` §2](docs/architecture.md#2-contract-schema).

New contracts should declare the current `"schemaVersion": 5`. Schema v2 added
`unclassifiedCode`; schema v3 added `allowedDependencies`; schema v4 added
`dependencyGraph.requireAcyclic`; schema v5 adds narrow, justified `exceptions` for exact
AARC002/AARC003 cases. Existing versionless/v1/v2/v3/v4 contracts remain valid; unsupported
future versions fail with AARC001 instead of being guessed.

### 2. Wire it into the project

From NuGet — the package ships the analyzer under `analyzers/dotnet/cs`, so a plain
`PackageReference` is enough:

```xml
<ItemGroup>
  <PackageReference Include="loach.ArchitectureAnalyzer" Version="0.2.1" PrivateAssets="all" />
  <AdditionalFiles Include="architecture.contract.json" />
  <!-- Optional: checked-in legacy-debt ratchet. -->
  <AdditionalFiles Include="architecture.baseline.json" Condition="Exists('architecture.baseline.json')" />
</ItemGroup>
```

Or from source, if you vendor or submodule this repository:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\..\src\ArchitectureAnalyzer\ArchitectureAnalyzer.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
  <AdditionalFiles Include="architecture.contract.json" />
</ItemGroup>
```

`OutputItemType="Analyzer"` loads the assembly as an analyzer; `ReferenceOutputAssembly="false"`
keeps it out of your runtime dependencies. Adjust the relative path for your layout.

**`<AdditionalFiles Include="architecture.contract.json" />` is what turns enforcement on.** A
project that references the analyzer but has no such entry is never analyzed: no AARC001, no
other diagnostic, a green build with zero enforcement. `contract_required` does not change this
(it only affects a contract that *is* supplied but is malformed or duplicated). If you expect
enforcement, check that the `AdditionalFiles` line exists in that project.

#### Package versions

`0.2.1` is the current stable release. `0.1.0` remains available for consumers that have not
upgraded yet.

```bash
dotnet add package loach.ArchitectureAnalyzer --version 0.2.1
dotnet tool install --global loach.ArchitectureAnalyzer.Baseline --version 0.2.1
```

| Feature | `0.1.0` | `0.2.1` |
|---|---|---|
| Contract schema | v1 / versionless | v1–v5 (versionless is v1) |
| `unclassifiedCode` (v2), AARC010 | no | yes |
| `allowedDependencies` (v3) | no | yes |
| `dependencyGraph.requireAcyclic` (v4), AARC011 | no | yes |
| `exceptions` (v5) | no | yes |
| Baseline ratcheting, AARC012/AARC013, `loach.ArchitectureAnalyzer.Baseline` tool | no | yes |
| Diagnostics | AARC001–AARC009 | AARC001–AARC013 |

A `0.1.0` consumer must stay on schema v1 and omit v2+ properties; a newer schema is rejected
with AARC001. Source/`main` references always have the current feature set.

### 3. Build

```
error AARC002: 'MyApp.Domain.Order' (Domain) must not depend on 'MyApp.Application.OrderService'
(Application): Domain must not depend on the outer Application layer.
```

That is the whole setup. A project that ships no `architecture.contract.json` in
`AdditionalFiles` is unaffected — the analyzer is opt-in and does nothing without a contract.

In a multi-project solution, this opt-in is **per compilation**. You may distribute the analyzer
reference centrally while adding `AdditionalFiles` only to governed projects. Different projects
can use different contracts without leakage; test/tooling projects with the analyzer loaded but no
contract remain no-op. See [`docs/multi-project-design.md`](docs/multi-project-design.md).

### Ratchet existing architecture debt

If enabling the contract reveals existing violations that cannot all be fixed immediately, keep
normal diagnostic severities and add a baseline instead of downgrading the rules:

```xml
<ItemGroup>
  <AdditionalFiles Include="architecture.baseline.json" />
</ItemGroup>
```

Generate or refresh it from the governed project:

```bash
dotnet tool install --global loach.ArchitectureAnalyzer.Baseline
architecture-baseline generate MyApp.csproj
```

The generator temporarily tells the analyzer to ignore the old baseline, captures the current
baselinable diagnostics, and rewrites deterministic entries keyed by symbols rather than line
numbers. Normal builds then tolerate only those exact known entries. New violations still fail;
after fixes, rerun the generator and stale entries disappear.

AARC001/AARC008/AARC009/AARC012 cannot be baselined because they indicate broken contract,
configuration or baseline infrastructure. AARC013 is generator-only transport metadata and also
cannot be baselined. See [`docs/baseline.md`](docs/baseline.md).

### Adding it to an existing project

1. Add the `loach.ArchitectureAnalyzer` package reference (or a submodule / project reference, per
   the two options above).
2. Start with **one** layer pair and **one** forbidden edge, the rule you most want to hold. A
   contract that fails the build in fifty places on day one gets deleted, not fixed.
3. Add the two item-group lines above to each project you want governed.
4. Build, fix or explicitly suppress what surfaces, then widen the contract one rule at a time.
5. Once the intended namespace roots are complete, enable `"unclassifiedCode": "error"` to
   ratchet against future coverage gaps.
6. Move selected layers to schema-v3 `allowedDependencies` when you want new dependency
   directions to be denied unless explicitly listed.
7. Move to schema v4 and set `dependencyGraph.requireAcyclic=true` when the explicitly permitted
   layer graph must remain a DAG.
8. For a deliberate long-lived AARC002/AARC003 exception, move to schema v5 and declare the exact
   source/target or source/API/member tuple with a required `justification`; do not disable the
   whole diagnostic just to permit one known exception.

`namespaceRoots` are prefixes, so `MyApp.Domain` covers `MyApp.Domain.Orders.Pricing` too.
Unclassified namespaces stay permitted for v1/versionless contracts and v2 `"ignore"`; v2
`"error"` reports AARC010 instead — see
[`docs/architecture.md`](docs/architecture.md#4-unclassified-code-and-strict-coverage).

### Wiring the CI gate in your own workflow

Because the diagnostics are compiler errors, your existing build step already gates on them:

```yaml
      - name: Build (enforces architecture.contract.json)
        run: dotnet build MySolution.sln --no-restore
```

Do not add `-p:RunAnalyzersDuringBuild=false` or downgrade the diagnostics' severity in CI — that
turns the gate off exactly where it matters most.

## Diagnostics

| ID | Title | Severity |
|---|---|---|
| [AARC001](docs/diagnostics.md#aarc001) | Architecture contract could not be loaded | Error |
| [AARC002](docs/diagnostics.md#aarc002) | Forbidden architecture dependency direction | Error |
| [AARC003](docs/diagnostics.md#aarc003) | Forbidden API usage in architecture layer | Error |
| [AARC004](docs/diagnostics.md#aarc004) | Missing required architecture layer declaration | Error |
| [AARC005](docs/diagnostics.md#aarc005) | Multiple distinct architecture layer declarations | Error |
| [AARC006](docs/diagnostics.md#aarc006) | Architecture layer declaration contradicts namespace layer | Warning |
| [AARC007](docs/diagnostics.md#aarc007) | Interop declaration outside allowed layer | Error |
| [AARC008](docs/diagnostics.md#aarc008) | Invalid architecture analyzer configuration value | Warning |
| [AARC009](docs/diagnostics.md#aarc009) | Unknown architecture analyzer configuration property | Warning |
| [AARC010](docs/diagnostics.md#aarc010) | Type is not assigned to an architecture layer | Error |
| [AARC011](docs/diagnostics.md#aarc011) | Declared architecture dependency graph contains a cycle | Error |
| [AARC012](docs/diagnostics.md#aarc012) | Architecture baseline could not be loaded | Error |
| [AARC013](docs/diagnostics.md#aarc013) | Architecture baseline capture record | Warning (generator-only) |

AARC004–AARC006 activate only when the contract declares a `layerDeclaration` section, AARC007
only when it declares `interopBoundaryRules`, AARC010 only when `unclassifiedCode=error`, and
AARC011 only when schema-v4 `dependencyGraph.requireAcyclic=true`. AARC012 is emitted only when
a supplied `architecture.baseline.json` is ambiguous or invalid. Existing projects with no
baseline keep their previous behavior. Full message formats, triggering examples and per-diagnostic
suppression options are in [`docs/diagnostics.md`](docs/diagnostics.md).

## Configuration

Severity is set with the standard `.editorconfig` keys, and a handful of `architecture_analyzer.*`
properties tune how the analyzer runs (generated-code handling, per-rule switches, whether a
malformed or duplicated contract is an error — a project with *no* `AdditionalFiles` contract is
always silent):

```ini
[*.cs]
dotnet_diagnostic.AARC002.severity = error
dotnet_diagnostic.AARC002.architecture_analyzer.validate_namespace_layer = true
```

The full property list, scope and precedence rules are in
[`docs/configuration.md`](docs/configuration.md). Configuration controls *how the analyzer runs*;
the contract controls *what the rules are*. Long-lived architecture exceptions are contract data,
not an operational toggle; see
[`docs/architecture-exceptions-design.md`](docs/architecture-exceptions-design.md).

## What this does and does not guarantee

**Does** — given a correct contract and severities left at `Error`:

- A type in a declared layer that crosses an explicit deny edge or a positive allowlist boundary
  fails the build unless that exact AARC002 source/target pair is a schema-v5 reviewed exception.
- A type in a declared layer that uses an API matched by a `forbiddenApis` rule fails the build
  unless that exact AARC003 source/API/member tuple is a schema-v5 reviewed exception.
- With a `layerDeclaration` section, a class that declares no layer, or declares two, fails the
  build; drift between a declared layer and its namespace is reported as a warning.
- With `interopBoundaryRules`, a method carrying a configured attribute outside its allowed layer
  fails the build.
- With schema-v2+ `unclassifiedCode=error`, an applicable source type that resolves through neither
  a namespace root nor a configured marker fails the build with AARC010.
- With schema-v4 `dependencyGraph.requireAcyclic=true`, cycles in the explicit
  `allowedDependencies` policy graph fail the build with deterministic AARC011 paths.
- A supplied contract file that is unreadable, malformed or duplicated fails the build (AARC001),
  rather than silently disabling enforcement. Omitting the `AdditionalFiles` entry altogether is
  not detected.
- The check runs everywhere `dotnet build` runs, with nothing extra to install or remember.

**Does not**:

- Judge whether your contract describes a *good* architecture — it enforces what you declare,
  faithfully and mechanically.
- Reject unclassified code unless strict coverage is enabled; v1/versionless and v2
  `unclassifiedCode=ignore` intentionally preserve permissive legacy behavior.
- Say anything about runtime behaviour, correctness or security beyond the declared layer graph
  and API list.
- Catch indirection that routes around the type system — reflection, `dynamic`, generated code that the
  generated-code options exclude, or `unsafe` pointer arithmetic.
- Classify types from another project through a `[Conditional]` marker attribute: such usages are
  stripped from referenced-assembly metadata, so those types are unclassified (see
  [`docs/architecture.md` §5](docs/architecture.md#5-what-each-diagnostic-inspects)).

See [`docs/design.md` §9](docs/design.md#9-what-this-analyzer-guarantees-and-what-it-does-not).

## Development

```bash
dotnet build ArchitectureAnalyzer.sln
dotnet test src/ArchitectureAnalyzer.Tests
tests/GateVerification/verify-gate.sh                 # project-reference real-build proof; needs bash
tests/MultiProjectGate/verify-multi-project.sh         # per-compilation multi-project isolation proof
tests/BaselineGate/verify-baseline.sh                  # baseline generation / ratcheting / pruning proof
bash tests/PackageConsumer/verify-package-consumer.sh # Linux packed-NuGet E2E
./tests/PackageConsumer/verify-package-consumer.ps1   # Windows packed-NuGet E2E
dotnet run --project tests/PerformanceBenchmark/ArchitectureAnalyzer.PerformanceBenchmark.csproj -c Release
```

`verify-gate.sh` builds a sample consumer project, proves one exact schema-v5 AARC002 exception
passes while an unrelated dependency on the same forbidden layer edge still fails, proves the
schema-v3 allowlist and schema-v4 DAG gates, then proves the AARC010 strict/ignore coverage cycle
before restoring a clean build. The unit tests use an in-memory compilation; this script is the evidence that
enforcement survives a genuine build.
See [`tests/GateVerification/README.md`](tests/GateVerification/README.md).

`verify-multi-project.sh` builds Producer, Consumer, Tests and Tooling projects with the analyzer
reference shared across all four compilations. Only Producer/Consumer receive contracts; injected
project-specific violations prove each governed project uses its own policy, while Tests/Tooling
remain silent without contracts. See
[`tests/MultiProjectGate/README.md`](tests/MultiProjectGate/README.md).

`verify-baseline.sh` proves the full ratchet cycle: checked-in debt is tolerated, new debt still
fails, the generator captures the reviewed current set, and regeneration prunes fixed entries.
See [`tests/BaselineGate/README.md`](tests/BaselineGate/README.md).

The representative performance benchmark generates three independent compilations with 300 source
files total, measures contract loading, dependency analysis, forbidden-API analysis and the full
schema-v5 analyzer, and asserts that the contract AdditionalFile is read exactly once per
compilation. CI enforces broad regression budgets rather than fragile microbenchmark numbers. See
[`docs/performance.md`](docs/performance.md).

`verify-package-consumer.sh` covers the distribution boundary separately: it packs the analyzer
from the current checkout into a temporary local NuGet feed, restores a standalone consumer with
an isolated package cache, verifies a clean build, injects the same kind of AARC002 violation, and
verifies that the packaged analyzer fails the real build. It never depends on a published
NuGet.org version or a project reference. See
[`tests/PackageConsumer/README.md`](tests/PackageConsumer/README.md).

Documentation map:

| Document | Scope |
|---|---|
| [`docs/design.md`](docs/design.md) | the *why* — rationale and non-goals |
| [`docs/architecture.md`](docs/architecture.md) | the *how* — pipeline and the full annotated contract schema |
| [`docs/diagnostics.md`](docs/diagnostics.md) | per-rule reference for AARC001–AARC013 |
| [`docs/configuration.md`](docs/configuration.md) | `.editorconfig` operational options: list, scope, precedence, defaults |
| [`docs/architecture-exceptions-design.md`](docs/architecture-exceptions-design.md) | justified contract exceptions and Roslyn suppression precedence |
| [`docs/platform-compatibility.md`](docs/platform-compatibility.md) | CI-validated OS, .NET SDK and Roslyn-host support envelope |
| [`docs/performance.md`](docs/performance.md) | representative workload, measured baseline and CI regression budgets |
| [`docs/multi-project-design.md`](docs/multi-project-design.md) | compilation-scoped contract routing for multi-project solutions |
| [`docs/baseline.md`](docs/baseline.md) | stable diagnostic baselines and legacy-code ratcheting workflow |
| [`docs/compatibility/`](docs/compatibility/) | consumer-specific migration material, kept out of the documents above |

## License

MIT — see [`LICENSE`](LICENSE).

## Status

Twelve normal diagnostics (AARC001–AARC012) plus generator-only AARC013, namespace **and** attribute-based layer classification,
schema-v2 strict architecture coverage, schema-v3 positive dependency allowlists, schema-v4
declared-graph DAG enforcement, schema-v5 exact justified AARC002/AARC003 exceptions,
attribute-driven interop boundaries, and `.editorconfig` operational options. The current stable
packages are `loach.ArchitectureAnalyzer` and `loach.ArchitectureAnalyzer.Baseline` **0.2.1**,
which include all of the above. `0.1.0` remains the previous stable analyzer release
(AARC001–AARC009, schema v1). See [Package versions](#package-versions).

The Architecture Contract format stays deliberately small and grows only from real consumer need —
there is still no DSL, and multi-file contracts remain unimplemented on purpose
([`docs/architecture.md`](docs/architecture.md#exactly-one-contract-per-compilation)).
[PSXRecompStudio](https://github.com/mao2009/PSXRecompStudio), whose hardcoded in-house analyzer
motivated several of these generic features, is the first consumer; its capability baseline is
tracked in [`docs/compatibility/psxrecomp-analyzer-baseline.md`](docs/compatibility/psxrecomp-analyzer-baseline.md),
with the completed cross-analyzer parity suite in
[`docs/compatibility/compatibility-suite.md`](docs/compatibility/compatibility-suite.md).
Nothing about that consumer is compiled into the analyzer.
