# Cross-analyzer compatibility suite

The executable counterpart to [`psxrecomp-analyzer-baseline.md`](psxrecomp-analyzer-baseline.md).
That document says what the old `PSXRecomp.Analyzer` does; this one describes the suite that
*proves* `ArchitectureAnalyzer` still does it, by compiling the same C# through both analyzers and
comparing the results.

Related: [#27](https://github.com/mao2009/ArchitectureAnalyzer/issues/27) (migration umbrella),
[#28](https://github.com/mao2009/ArchitectureAnalyzer/issues/28) (baseline),
[#33](https://github.com/mao2009/ArchitectureAnalyzer/issues/33) (this suite).

The baseline document is the SSOT for fixture IDs and baseline behaviour. This document does not
restate it and does not re-derive the mapping; it records the harness decision, the comparison
semantics, and the measured result.

---

## 1. Harness design decision

**Chosen: a pinned in-repo source fixture, compiled into the parity test project.**

`src/PSXRecomp.Analyzer/**` at the baseline SHA is copied verbatim to
`tests/CompatibilitySuite/PSXRecompAnalyzerBaseline/` and compiled straight into
`ArchitectureAnalyzer.CompatibilitySuite`. The suite instantiates
`PSXRecompArchitectureAnalyzer` in-process and hands it a `CSharpCompilation`.

Why this works at all: the baseline analyzer has **no `ProjectReference`**. Its only dependencies
are `Microsoft.CodeAnalysis.CSharp` 4.12.0 and `Microsoft.CodeAnalysis.Analyzers` 3.3.4 — the same
two this repository already uses. Verified, not assumed: the copy builds clean with
`TreatWarningsAsErrors` on, and the suite runs it against the fixture matrix.

What it costs: two `<NoWarn>` entries (`RS2008` analyzer release tracking, `RS1036`
`EnforceExtendedAnalyzerRules`). Both are analyzer-*packaging* rules; the frozen copy is never
packaged, and satisfying them would mean editing the baseline, which would invalidate the whole
exercise.

### Alternatives considered

| Option | Why not |
|---|---|
| **git submodule on PSXRecompStudio** | Drags an entire unrelated game-recompilation project's history and tree into the repo to reuse one 800-line analyzer. Needs `--recurse-submodules` in CI, and a submodule pointer is a *live* reference: an innocent `git submodule update --remote` silently re-baselines the compatibility claim. |
| **`ProjectReference` to a PSXRecompStudio checkout** | Requires every developer and every CI machine to have the sibling repository checked out at the right SHA. Breaks a clean `git clone && dotnet test`. |
| **NuGet / reference package** | `PSXRecomp.Analyzer` is not published, and publishing an obsolete analyzer solely to test its replacement inverts the point of the migration. |
| **Separate `netstandard2.0` baseline analyzer project** | A real second project, second target framework, second restore graph, and an extra solution entry — for an assembly nothing consumes but one test project's `new`. Rejected as pure ceremony; source inclusion gives the identical analyzer object. |
| **Copy only the *fixtures*, keep expectations hardcoded** | Reduces to asserting a frozen table, not to comparing two analyzers. A change in either analyzer's behaviour would then be invisible until someone re-derived the table by hand. |
| **Compare against the baseline's recorded diagnostics only** | Same problem, plus it cannot cover the operational-toggle matrix, where the baseline has no recorded behaviour at all. |

### Consequences

- Reproducible and deterministic: the baseline is bytes in this repository, not a moving `main`.
- CI needs no access to PSXRecompStudio, and no checkout of it.
- Maintenance is a rule, not a chore: **never edit the copy, never advance the SHA.**
  `tests/CompatibilitySuite/PSXRecompAnalyzerBaseline/README.md` carries the SHA and a one-command
  byte-for-byte provenance check.

---

## 2. Layout

```
tests/CompatibilitySuite/
  ArchitectureAnalyzer.CompatibilitySuite.csproj
  PSXRecompAnalyzerBaseline/     frozen copy @ 88f5b6f2 (+ README with the provenance check)
  BaselineContract.cs            the baseline's compiled-in rules, transcribed as contract JSON
  ParityFixtures.cs              the 57 scenarios of the matrix, as compilable sources
  ParityHarness.cs               compile once, analyze twice, normalize, diff
  ParityTests.cs                 the assertions
```

It is its own project rather than a folder in `src/ArchitectureAnalyzer.Tests`, for the same
reason `tests/GateVerification` is: it pulls a foreign analyzer's source into its compilation, and
that has no business in the unit-test assembly.

---

## 3. Comparison semantics

Each fixture is parsed **once** into a single `CSharpCompilation` (`ReferenceAssemblies.Net.Net90`,
`LanguageVersion.Latest`), which is then analyzed twice. Both analyzers therefore see byte-identical
sources, file paths and symbols — differences can only come from the analyzers.

The compilation is asserted to be free of compiler errors first; a fixture that does not compile
would let both analyzers agree on nothing at all.

### 3.1 Diagnostic IDs are never compared

Every diagnostic is projected onto a shared `ParityCategory` through the mapping table of the
baseline document §2:

| PSXR | AARC | `ParityCategory` |
|---|---|---|
| PSXR001 | AARC004 | `MissingLayerDeclaration` |
| PSXR002 | AARC005 | `MultipleLayerDeclarations` |
| PSXR003 | AARC006 | `NamespaceLayerMismatch` |
| PSXR004 | AARC002 | `ForbiddenDependency` |
| PSXR005 | AARC003 | `ForbiddenApi` |
| PSXR006 | AARC007 | `InteropBoundary` |

`AARC001` and `AARC008` are AARC-only (baseline document §2.1). They are not mapped; instead the
suite asserts they **never occur** in a parity scenario, since either would mean the transcribed
contract or the operational configuration is broken rather than that parity failed.

### 3.2 A finding, not a diagnostic

Each diagnostic is reduced to a `SemanticFinding`:

```
(Category, Severity, File, Line, Character, Facts)
```

and the two sides are compared as **multisets** of findings — so a duplicate on one side is a
difference, and a fixture that produces several findings must match on all of them.

- **Category** — the mapped semantic category, per §3.1.
- **Severity** — the effective severity. Included deliberately: a rule that silently degrades from
  `error` to `warning` no longer breaks a build, so it is a parity difference, not a cosmetic one.
- **File / Line / Character** — the diagnostic's own reported location, 1-based, from
  `Location.GetLineSpan()`.
- **Facts** — the information the message must carry, extracted as message *arguments*, not as
  message text.

### 3.3 Facts, not message strings

The two analyzers word their messages differently and carry different argument counts (baseline
document D8), so comparing rendered messages would fail on every rule while proving nothing. Instead
the harness recovers each diagnostic's arguments by inverting its descriptor's `MessageFormat`
(`ParityHarness.MessageArguments`), then selects the facts both sides are required to convey:

| Category | Facts compared | Deliberately not compared |
|---|---|---|
| `MissingLayerDeclaration` | offending type | PSXR001's list of available attribute names — in the contract on the AARC side |
| `MultipleLayerDeclarations` | offending type, declared layers | wording |
| `NamespaceLayerMismatch` | declared layer, namespace, namespace-implied layer | AARC006's extra type-name argument (strictly more information) |
| `ForbiddenDependency` | source type, source layer, target type, target layer, reason | — (formats are character-identical) |
| `ForbiddenApi` | API, layer, reason | — (formats are character-identical) |
| `InteropBoundary` | method **name** | PSXR006's namespace/type qualification, which AARC007 drops (see P2 below) |

Reason strings *are* compared, which is why `BaselineContract.cs` transcribes them byte-identically
from `ForbiddenApiCatalog` and `IsForbiddenDependency`.

### 3.4 What each fixture asserts

1. No unmapped AARC diagnostic (`AARC001`/`AARC008`) was produced.
2. `Valid` scenarios are clean **on both sides**; `Violation` scenarios still make the baseline
   speak, so the fixture is proven to still reproduce the behaviour it was written for.
3. The observed difference — `PSXR-only` and `AARC-only` categories — equals the difference
   **declared and classified** on the fixture. A new difference fails. A declared difference that
   disappears also fails.
4. Separately, `EveryViolationIsStillDetectedByArchitectureAnalyzer` asserts the headline claim
   directly: the exact set of baseline violations that `ArchitectureAnalyzer` reports *nothing* for
   is `{F-M13}`, and nothing else.

Diagnostic counts are never compared on their own. Two sides can agree on a count and disagree on
every fact in it.

### 3.5 The contract and the severity pin

`BaselineContract.cs` transcribes the baseline's compiled-in knowledge into
`architecture.contract.json` form: six layers and their namespace roots, six marker attributes,
`layerDeclaration.required` + `validateNamespaceConsistency` (both unconditional in the baseline),
the four forbidden edges expanded to six rows, the per-layer forbidden-API catalogue, and the two
interop-boundary attributes. Rule order inside a layer is preserved, because both analyzers
de-duplicate per member on the rule's *index*.

The default parity run applies one operational property — `dotnet_diagnostic.AARC006.severity =
error` — mirroring the baseline's own `.editorconfig`, which pins all six PSXR rules to `error`
(baseline document §1.3, D7). Without it AARC006 is a `Warning` and F-N01 legitimately diverges;
that is asserted explicitly rather than papered over (§6.3).

---

## 4. Running the suite

```sh
dotnet test tests/CompatibilitySuite/ArchitectureAnalyzer.CompatibilitySuite.csproj
dotnet test ArchitectureAnalyzer.sln          # everything, including the unit tests
dotnet test tests/CompatibilitySuite/ArchitectureAnalyzer.CompatibilitySuite.csproj \
  --filter "ScenarioIsAccountedFor"           # the 57-scenario theory alone
```

A failing scenario prints both sides' findings and the diff, next to the classification the fixture
declared. No network access to PSXRecompStudio is needed at any point.

**When a scenario fails, do not adjust its declaration to make it green.** Either the change is a
regression to fix in `src/ArchitectureAnalyzer`, or it is a genuine behavioural decision — in which
case it belongs in §6 with evidence *before* the fixture is re-declared.

---

## 5. CI

`.github/workflows/ci.yml` runs the suite on every push to `main` and every pull request. The `Test`
step was widened from the unit-test project to the solution:

```yaml
- name: Test
  run: dotnet test ArchitectureAnalyzer.sln --no-build
```

so the parity suite is covered by the same gate as everything else, and future test projects are
picked up automatically. Nothing else in the workflow changed: the frozen baseline is in-repo, so
no submodule checkout, no extra credentials and no external repository access were added.

---

## 6. Results

**57/57 scenarios accounted for. 55 equivalent, 2 classified differences, 0 unknown.**

| Classification | Count | Fixtures |
|---|---:|---|
| Equivalent | 55 | all except the two below |
| Capability regression | 1 | F-M13 |
| Intentional improvement | 0 | — |
| Harmless presentation difference | 1 | F-I03 |
| Baseline bug | 0 | — |
| Unknown / unexplained | **0** | — |

Every other fixture produced a byte-identical multiset of findings on both sides — same category,
same severity, same file, same line and column, same facts. That includes all five reason-string
groups, the generic-definition rendering (`Scenario.AppBox<T>`, F-D05), the nested-type layer
inheritance (`Scenario.Outer.Inner` resolved to `Domain`, F-D08), the per-member de-duplication
boundary (F-A03/F-A04/F-A13), and the forbidden-API catalogue asymmetry (F-A17/F-A18).

### 6.1 F-M13 — capability regression

| | |
|---|---|
| Scenario | unattributed `partial class Split`, with the generated part (`Split.g.cs`) first in syntax-tree order |
| PSXR | `MissingLayerDeclaration[Error] /0/Split.cs(3,31) {Scenario.Split}` |
| AARC | *(nothing)* |
| Cause | `AnalyzeLayerDeclaration` bails on `IsGeneratedPath(type.Locations.FirstOrDefault())` — it inspects only the first location. `GetPrimaryDeclarationLocation` walks all `DeclaringSyntaxReferences` and reports at the first non-generated part. |
| Impact | A real missing layer declaration becomes invisible whenever a hand-written partial class also has a generated part that happens to come first. AARC004/005/006 are all affected. |
| Classification | **Capability regression.** Already recorded as D4 in the baseline document, which recommends mirroring the baseline's "first non-generated part" walk. |
| Follow-up | [#42](https://github.com/mao2009/ArchitectureAnalyzer/issues/42) |
| Status | **Not fixed here.** Fixing capability gaps is out of scope for #33; the suite pins the regression instead, so the follow-up fix will flip this fixture to `Equivalent` and the suite will demand the declaration be updated. |

This is the one fixture where `EveryViolationIsStillDetectedByArchitectureAnalyzer` records a
blind spot, and that test asserts the set is exactly `{F-M13}` — so any *second* blind spot fails
the build immediately.

### 6.2 F-I03 — harmless presentation difference

| | |
|---|---|
| Scenario | `[LibraryImport]` partial method, declaring part + implementing part |
| PSXR | two findings, `(10,34)` and `(12,34)`, identical facts |
| AARC | one finding, `(10,34)` |
| Cause | PSXR006 runs per `MethodDeclaration` with no de-duplication (baseline document D5); AARC007 de-duplicates on `(method, attribute)` and reports at the part carrying the attribute. |
| Classification | **Harmless presentation difference.** The same fault is reported, once instead of twice, at a deterministic location. Nothing becomes invisible; AARC's behaviour is preferable. |

### 6.3 Operational-toggle coverage

Parity is also verified under the `.editorconfig` knobs, not only at defaults:

| Toggle | Test | Result |
|---|---|---|
| default enforcement | `ScenarioIsAccountedFor` (57 scenarios) | as above |
| `require_layer_declaration = false` | `RequireLayerDeclarationOverride_RelaxesOnlyTheDeclarationRule` | AARC004 goes quiet on F-M02 (PSXR001 does not — the baseline has no such toggle); F-D01, F-A01 and F-I01 keep full parity, so the override is proven to relax the declaration rule **only** |
| `validate_namespace_layer = true` | `ValidateNamespaceLayerOverride_RestoresParityForAContractThatOptedOut` | with a contract whose `validateNamespaceConsistency` is `false`, F-N01 loses the AARC006 finding; the operational toggle restores exact parity without editing the contract |
| `dotnet_diagnostic.AARC006.severity = error` | `Aarc006SeverityPin_IsRequiredToReproduceBaselineStrictness` | unpinned, both sides find the fault but AARC reports `Warning` against the baseline's `Error`, and the suite reports a difference; pinned, F-N01 is exactly equivalent |

The severity result is the migration-checklist item from D7, now mechanically pinned: a migrating
project that omits that one line silently downgrades a class of build breaks to warnings.

### 6.4 Cross-check against the baseline document's divergence catalogue

Every divergence Track A recorded was exercised. Outcomes:

| Baseline divergence | Exercised by | Observed |
|---|---|---|
| D1 namespace classification order | all namespace-mapped fixtures | no observable difference, as predicted (no root is a prefix of another) |
| D2 `ContainingType` chain (CLOSED) | F-D08, F-M04, F-M12 | confirmed closed — sources and layers match exactly |
| D3 generated-file detection | F-M06, F-M14, F-M15, F-D11, F-I06 | equivalent; the only generated-path difference that surfaces is D4 (F-M13) |
| D4 partial-type location | F-M13 | **confirmed, capability regression** (§6.1) |
| D5 interop de-duplication | F-I03 | confirmed, harmless (§6.2) |
| D6 interop-boundary predicate | F-I01–F-I06 | equivalent for the baseline's contract shape, as predicted |
| D7 severity | F-N01 | confirmed; requires the pin (§6.3) |
| D8 message/argument shape | all | absorbed by fact selection (§3.3); see P2 below for the one residue |
| D9 source location | all | identical everywhere except F-M13/F-I03 |
| D10 marker-namespace exemption | F-A19, F-M07, F-D06 | **does not materialize** — see P1 below |
| D11 AARC-only capabilities | all | no AARC001/AARC008 in any scenario |
| D12 symbol-kind coverage | F-M08, F-M09 | identical |
| D13 catalogue contents | F-A17, F-A18 | transcription verified by exact reason-string match |
| D14 extension-method blind spot | F-A14 | identical on both sides, now pinned |

### 6.5 Findings of this suite not already in the baseline document

Recorded here rather than by editing Track A's document.

#### P1. D10's marker-namespace gap is unobservable for the baseline's contract shape

D10 predicts AARC003 is *stricter* than PSXR005 at F-A19, because AARC has no marker-namespace
exemption. Measured: **both analyzers report nothing.** AARC003 resolves the layer of
`PSXRecomp.Architecture.MarkerHelper` before consulting the catalogue, and the marker namespace is
mapped to no layer by the contract, so `ResolveLayer` returns `null` and the rule returns early —
reaching the same outcome as PSXR's explicit exemption, by a different route.

The gap is therefore latent, not active: it would only surface for a contract that maps the marker
namespace into a layer *and* forbids APIs in that layer. Not a parity difference; worth knowing
before someone "fixes" AARC003 by adding an exemption the suite cannot observe.

Classification: **harmless** (no behavioural difference under the baseline contract).

#### P2. AARC007 loses the qualified method name (residue of D8)

PSXR006 reports `Scenario.NativeCalls.DoWork()`; AARC007 reports `DoWork`. The suite normalizes both
to the method name, which is honest for these fixtures — every scenario has exactly one interop
method — but the loss is real for a codebase with same-named interop methods on several types, or
with overloads, where the AARC message alone cannot identify the declaration.

Classification: **harmless diagnostic presentation difference**, with a caveat. The location is
still exact, so the diagnostic is never ambiguous *in an editor* — only in a build log. Baseline
document D8 already recommends `method.ToDisplayString()` as a follow-up; this suite agrees and
would need no change if it landed (the normalizer reduces both forms to the same name).

---

## 7. Open items

| Item | Kind | Where |
|---|---|---|
| F-M13 / D4 — AARC004/005/006 skip a partial type whose first part is generated | capability regression, **fix in `src/ArchitectureAnalyzer`** — [#42](https://github.com/mao2009/ArchitectureAnalyzer/issues/42) | `ArchitectureContractAnalyzer.AnalyzeLayerDeclaration` |
| P2 / D8 — AARC007 message should carry `method.ToDisplayString()` | nice-to-have | `ArchitectureContractAnalyzer.AnalyzeInteropBoundary` |
| D7 — `dotnet_diagnostic.AARC006.severity = error` | migration checklist | consuming project's `.editorconfig` |

The first is the only one that affects enforcement capability. It was known before this suite
existed (baseline document D4); the suite's contribution is to hold it still, with evidence, so it
cannot quietly grow a sibling.
