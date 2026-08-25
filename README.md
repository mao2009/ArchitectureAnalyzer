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
 architecture.contract.json  ->  ArchitectureContract  ->  AARC001 / AARC002  ->  compilation fails  ->  workflow fails
 (JSON, in your repo,             Analyzer                 AARC003                 with an error           on non-zero exit
  under code review)          (generic; no rules          (severity: Error)       at the exact line
                               of its own)
```

The contract is the single source of truth. Changing what is enforced means editing JSON in your
own repository — never changing or rebuilding the analyzer.

## Usage

### 1. Write a contract

`architecture.contract.json`, next to the code it governs:

```json
{
  "layers": [
    { "name": "Domain", "namespaceRoots": [ "MyApp.Domain" ] },
    { "name": "Application", "namespaceRoots": [ "MyApp.Application" ] }
  ],
  "forbiddenDependencies": [
    { "from": "Domain", "to": "Application", "reason": "Domain must not depend on the outer Application layer." }
  ],
  "forbiddenApis": [
    { "layer": "Domain", "type": "System.Console", "reason": "Console I/O must be abstracted behind an Infrastructure adapter." }
  ]
}
```

### 2. Wire it into the project

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

### 3. Build

```
error AARC002: 'MyApp.Domain.Order' (Domain) must not depend on 'MyApp.Application.OrderService'
(Application): Domain must not depend on the outer Application layer.
```

That is the whole setup. A project that ships no `architecture.contract.json` is unaffected —
the analyzer is opt-in and does nothing without a contract.

### Adding it to an existing project

1. Add ArchitectureAnalyzer to your solution (as a submodule, a vendored copy, or a project
   reference from a sibling checkout — there is no NuGet package yet).
2. Start with **one** layer pair and **one** forbidden edge, the rule you most want to hold. A
   contract that fails the build in fifty places on day one gets deleted, not fixed.
3. Add the two item-group lines above to each project you want governed.
4. Build, fix or explicitly suppress what surfaces, then widen the contract one rule at a time.

`namespaceRoots` are prefixes, so `MyApp.Domain` covers `MyApp.Domain.Orders.Pricing` too.
Namespaces that match no root are unclassified and invisible to the analyzer — see
[`docs/architecture.md`](docs/architecture.md#4-unclassified-code).

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

Full message formats, triggering examples and per-diagnostic suppression options are in
[`docs/diagnostics.md`](docs/diagnostics.md).

## What this does and does not guarantee

**Does** — given a correct contract and severities left at `Error`:

- A type in a declared layer that references a type across a forbidden edge fails the build.
- A type in a declared layer that uses an API matched by a `forbiddenApis` rule fails the build.
- A referenced contract file that is missing or malformed fails the build, rather than silently
  disabling enforcement.
- The check runs everywhere `dotnet build` runs, with nothing extra to install or remember.

**Does not**:

- Judge whether your contract describes a *good* architecture — it enforces what you declare,
  faithfully and mechanically.
- See code outside the namespaces listed in `layers[].namespaceRoots`; unclassified code is
  invisible by design.
- Say anything about runtime behaviour, correctness or security beyond the declared layer graph
  and API list.
- Catch indirection that routes around the type system — reflection, `dynamic`, generated code on
  an excluded path, or `unsafe` pointer arithmetic.

See [`docs/design.md` §9](docs/design.md#9-what-this-analyzer-guarantees-and-what-it-does-not).

## Development

```bash
dotnet build ArchitectureAnalyzer.sln
dotnet test src/ArchitectureAnalyzer.Tests
tests/GateVerification/verify-gate.sh     # real dotnet build proof; needs bash
```

`verify-gate.sh` builds a sample consumer project, injects a violating source file, asserts the
build then fails *with AARC002*, removes it and asserts the build passes again. The unit tests use
an in-memory compilation; this script is the evidence that enforcement survives a genuine build.
See [`tests/GateVerification/README.md`](tests/GateVerification/README.md).

Documentation map: [`docs/design.md`](docs/design.md) is the *why*,
[`docs/architecture.md`](docs/architecture.md) is the *how* (including the full annotated contract
schema), [`docs/diagnostics.md`](docs/diagnostics.md) is the per-rule reference.

## License

MIT — see [`LICENSE`](LICENSE).

## Status

v0.1. The Architecture Contract format is deliberately minimal: three diagnostics, namespace-based
layer classification, no DSL. It is expected to grow — per-type attribute overrides, native-interop
boundary rules, multi-file contracts — driven by what real consumers actually need rather than by
speculation. [PSXRecompStudio](https://github.com/mao2009/PSXRecompStudio), whose hardcoded
in-house analyzer this project generalizes, is the first such consumer.
