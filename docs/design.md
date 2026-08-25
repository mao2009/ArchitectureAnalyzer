# Design

This document records *why* ArchitectureAnalyzer exists and *why* it is shaped the way it
is. It is written before the bulk of the implementation, based on analysis of
[PSXRecompStudio](https://github.com/mao2009/PSXRecompStudio)'s `PSXRecomp.Analyzer`, the
first working proof that "Architecture Contract → Roslyn Analyzer → Diagnostic → CI Gate"
is a viable enforcement chain for a C#/.NET project.

## 1. Why an Architecture Analyzer at all

A written architecture document (an ADR, a `docs/architecture.md`, a diagram) is not
self-enforcing. It documents intent; it does not check whether the code still matches that
intent. Drift between "what the docs say" and "what the code does" is not a hypothetical —
it is the default outcome of any project that changes over time, because nothing pays the
cost of keeping them in sync except a human noticing during review, which is optional,
inconsistent, and gets skipped under deadline pressure.

PSXRecompStudio's ADR-006 made this concrete: `docs/architecture-matrix.md` was the
project's SSOT for layering, dependency direction, and forbidden APIs, but "manual review
only" did not guarantee compliance. The fix was not "review harder" — it was to make a
machine check the SSOT on every build.

## 2. Why compiler-time enforcement, specifically

Enforcement can happen at several points: pre-commit hook, PR-time CI job, IDE lint, or
compile time via a Roslyn analyzer. This project picks compile time (a `DiagnosticAnalyzer`
that participates in `dotnet build`) as the primary mechanism because:

- **It cannot be skipped.** A pre-commit hook can be bypassed with `--no-verify`. A CI-only
  check can be bypassed by merging with a red check, or simply doesn't run for a local
  build. A compiler error blocks `dotnet build` itself — the same command every contributor,
  every CI job, and every IDE background-build already runs. There is no separate step to
  forget to add.
- **It is immediate.** The violation is reported at the exact line, in the IDE, before the
  author even commits — not minutes later in a CI log they have to go find.
- **It reuses infrastructure everyone already has.** No new service, no new step in the
  pipeline beyond what `dotnet build`/`dotnet test` already run in CI.

Compile-time enforcement does not replace human/AI code review — it narrows what review
needs to catch. See [§5](#5-relationship-to-other-review-layers).

## 3. Why the Analyzer must not be the SSOT

A Roslyn analyzer with the architecture rules hardcoded *inside its C# source* is itself a
new, undocumented SSOT — one that happens to be compiled and therefore even harder for a
human to audit than a markdown table. PSXRecompStudio's `PSXRecomp.Analyzer` was exactly
that: layer names (`Domain`, `Application`, `Infrastructure`, ...), namespace prefixes
(`PSXRecomp.Core`, `PSXRecompStudio`, ...), the forbidden-dependency matrix, and the
forbidden-API catalog were all `switch` statements and `Dictionary` literals baked into the
analyzer assembly. That was the correct call for a single-consumer proof of concept, but it
means the analyzer *is* PSXRecompStudio's architecture, not a general tool that *enforces
whatever architecture a project declares*.

ArchitectureAnalyzer inverts that: the analyzer is a generic interpreter for a project-
supplied **Architecture Contract** file (`architecture.contract.json`, consumed as a Roslyn
`AdditionalFiles` item — see [`architecture.md`](architecture.md)). The contract is
human-editable JSON that lives in the consuming repository, next to the code it governs,
under normal code review and version control. The analyzer's own source code carries zero
project-specific layer names, namespace roots, or API rules. Changing what is enforced never
requires changing or rebuilding the analyzer — only editing the contract file. This is the
"Architecture Contract → Analyzer → Enforcement" direction the project brief asks for, and
the specific thing this project must not regress into being "AnalyzerがSSOTになる".

## 4. Why JSON + `AdditionalFiles`, not YAML / MSBuild items / attributes / a custom DSL

Considered options and why each was rejected or accepted for v0.1:

| Option | Verdict | Reason |
|---|---|---|
| **JSON via `AdditionalFiles`** | **Chosen** | Standard, well-documented Roslyn mechanism (the same one StyleCop.Analyzers uses for `stylecop.json`). No custom MSBuild task, no source generator, no extra build step. `System.Text.Json` ships in the BCL/analyzer-safe surface and, with `EnforceExtendedAnalyzerRules` + the modern isolated `AssemblyLoadContext` analyzers load into, bundling it privately does not collide with the host IDE/compiler's own copy. Diffable, greppable, trivially validated in a unit test by just deserializing it. |
| YAML | Rejected (for now) | No YAML parser in the BCL; would add a third-party dependency to an analyzer assembly (higher bar — it loads into every consumer's build and IDE). Marginal readability gain over JSON does not justify that. Revisit only if a real consumer asks for it. |
| MSBuild properties/items | Rejected | Workable for a handful of flat values, but a layer graph + a dependency matrix + an API rule list do not fit MSBuild's item/property model without turning the `.csproj` into the DSL. Also couples the contract to MSBuild specifically, when the same JSON should be readable/lintable by tooling outside the build (a future `architecture-lint` CLI, a pre-commit check, etc.). |
| C# Attributes | Rejected as the *primary* mechanism | This is what PSXRecompStudio used for layer marking, distributed via a linked `Compile` item in `Directory.Build.props`. It works, but it requires every consumer project to opt in to a source-linking mechanism, and it conflates "which layer is this type in" (a per-type fact, arguably attribute-shaped) with "what are the layers and their allowed dependencies" (a project-wide graph, not type-shaped at all). v0.1 avoids needing this entirely — see §6. It remains a plausible *future* input alongside the JSON contract for per-type overrides, not a replacement for it. |
| `.editorconfig` | Rejected | Good fit for per-diagnostic severity/suppression (and this project does respect the standard `dotnet_diagnostic.AARCxxx.severity` keys for that reason), but its flat key-value shape cannot express a layer graph or an API rule list without inventing a miniature DSL inside `.editorconfig` values — worse than just writing JSON. |
| Custom DSL | Rejected | Explicitly out of scope per the project brief ("最初から複雑なDSLを作らない"). JSON is already a DSL-free, tool-supported serialization; inventing another syntax on top would be unjustified complexity for v0.1. |

## 5. Why namespace-based layer classification, not attributes, for v0.1

PSXRecompStudio classified a type's layer two ways — an explicit `[Domain]`/`[Application]`/
`[Infrastructure]`/... attribute (primary), falling back to namespace-prefix matching
(secondary, for defense in depth). Both were driven by hardcoded attribute types distributed
via a linked source file, requiring every consumer project to add
`<CompileArchitectureAttributes>true</CompileArchitectureAttributes>` and a matching
`Directory.Build.props` entry.

For v0.1, ArchitectureAnalyzer uses **namespace-prefix classification only**: the contract's
`layers[].namespaceRoots` maps a namespace prefix to a layer name, and a type's layer is
resolved from `ITypeSymbol.ContainingNamespace`. This is a deliberate scope cut, not an
oversight:

- It requires zero source-distribution glue (no linked `Compile` item, no opt-in MSBuild
  property, no attribute assembly to keep in sync across projects). A consumer only adds an
  `AdditionalFiles` reference to their contract file and a `ProjectReference` to the
  analyzer.
- It is still 100% symbol-based (`INamespaceSymbol`, not string search over source text),
  so it satisfies "文字列検索や正規表現だけに依存しない" without adding attribute-resolution
  machinery.
- Most real projects already encode their layer in their namespace (`MyApp.Domain.*`,
  `MyApp.Infrastructure.*`), so this covers the common case with the least code.
- Attribute-based *per-type* overrides (for the cases where namespace and intended layer
  genuinely diverge) are a natural, additive extension for a later version — the contract
  schema can grow an `attributeOverrides` section without a breaking change — but are not
  needed to prove the core "contract → diagnostic → build failure" chain, so they are left
  out of v0.1 rather than built speculatively.

## 6. Diagnostic scope for v0.1

Three diagnostics, not six. PSXRecompStudio's `PSXR001`–`PSXR006` mixed truly generic
capabilities (dependency-direction checking, forbidden-API checking) with one that is
inherently project-shaped (`PSXR006`, P/Invoke-must-live-in-`PSXRecomp.Core` — a rule about
*that* project's native-interop boundary, not a general architecture concept) and two that
exist only because of the attribute-marking mechanism this project deliberately doesn't use
in v0.1 (`PSXR001`/`PSXR002`, missing/multiple attribute). ArchitectureAnalyzer ships:

- **AARC001** — Architecture contract could not be loaded (missing/malformed
  `architecture.contract.json` when one was referenced). Without this, a broken contract
  would silently disable enforcement, which would make the analyzer *less* trustworthy than
  the hand-maintained doc it replaces. See [`diagnostics.md`](diagnostics.md).
- **AARC002** — Forbidden layer dependency direction. This is the flagship rule and the one
  [§8 of the project brief](../README.md) asks to prove end-to-end (good build → violation →
  build failure → fix → good build again).
- **AARC003** — Forbidden API usage inside a layer.

A generic P/Invoke/native-boundary check is a plausible future addition (any project with an
interop boundary can express it as "layer X may not contain `[DllImport]`/`LibraryImport`
declarations", which is a shape the contract schema can grow into) but is left for a later
version, per the brief's explicit "最初から全機能を実装する必要はない".

## 7. Diagnostic ID namespace

`AARC###`, Error severity, `EnabledByDefault = true`. IDs are never reused or renumbered
once shipped (see [`diagnostics.md`](diagnostics.md)); a retired rule is marked obsolete in
the docs rather than having its ID reassigned.

## 8. Relationship to other review layers

ArchitectureAnalyzer is one layer in a stack, not a replacement for the others:

```
AI / human code review   (judgment: "is this a good design")
        +
Compiler-time Analyzer   (mechanical: "does this match the declared contract")
        +
Tests                    (behavior: "does this do what it should")
        +
CI                       (gate: "did all of the above actually run")
```

It is deliberately not coupled to any specific AI review vendor (CodeRabbit or otherwise) —
it is a `DiagnosticAnalyzer` that participates in `dotnet build`, so it has no dependency on,
or opinion about, what else sits in a project's review pipeline.

## 9. What this analyzer guarantees, and what it does not

**Guarantees**, given a correct contract file and `EnabledByDefault`/severity left at
`Error`:

- A type whose namespace matches a declared layer, and that references a type in another
  declared layer across a `forbiddenDependencies` edge, fails the build.
- A type in a declared layer that calls a member matched by a `forbiddenApis` rule fails the
  build.
- A referenced-but-missing-or-malformed contract file fails the build rather than silently
  no-op'ing.

**Does not guarantee**:

- That the contract itself is a *good* architecture — the analyzer enforces whatever the
  contract says, faithfully and mechanically. Garbage in, garbage enforced.
- Anything about code that lives outside a namespace listed in `layers[].namespaceRoots`
  (unclassified code is invisible to the analyzer by design — see §5).
- Runtime behavior, correctness, or security properties unrelated to the declared layer
  graph and API list.
- Detection of indirection that routes around the type system (reflection, dynamic,
  source-generated code matching an excluded path, `unsafe` pointer arithmetic to reach
  otherwise-forbidden state).

## 10. Relationship to PSXRecompStudio

PSXRecompStudio is this project's first *reference consumer*, not its origin of truth going
forward. PSXRecompStudio-specific rules (its six-layer model, its `PSXRecomp.Core` interop
boundary, its exact forbidden-API list) stay in PSXRecompStudio's own
`architecture.contract.json` once it migrates to depend on this analyzer; none of that is
hardcoded here. See the project brief's §12 for the intended dependency shape.
