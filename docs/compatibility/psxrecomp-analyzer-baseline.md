# PSXRecomp.Analyzer Compatibility Baseline

This document is the single source of truth for what `PSXRecomp.Analyzer` does, so that the
generic `ArchitectureAnalyzer` replacement can be shown to have **equal or greater
architecture-enforcement capability**. It is written to be consumed mechanically by the
compatibility suite tracked in [#33](https://github.com/mao2009/ArchitectureAnalyzer/issues/33);
fixture IDs and expected conditions are normative, prose is not.

Related: [#27](https://github.com/mao2009/ArchitectureAnalyzer/issues/27) (migration umbrella),
[#28](https://github.com/mao2009/ArchitectureAnalyzer/issues/28) (this baseline).

---

## 1. Pinned baseline

| | |
|---|---|
| Baseline repository | `mao2009/PSXRecompStudio` |
| **Baseline SHA** | **`88f5b6f2c209b0980fd96d241f28dce1f840673a`** |
| Baseline paths | `src/PSXRecomp.Analyzer/**`, `src/PSXRecomp.Analyzer.Tests/**` |
| ArchitectureAnalyzer side | `src/ArchitectureAnalyzer/**` at `33c8ea6` (AARC001–AARC008 all merged) |

**This SHA is pinned.** It is *not* "current PSXRecompStudio `main`", and it must not be advanced
when PSXRecompStudio moves on. Everything in sections 2–5 describes the analyzer *at this commit*.
Later PSXRecompStudio behavior changes are recorded in [§7 Post-baseline drift](#7-post-baseline-drift),
never by editing the SHA above.

### 1.1 Reading the baseline source

The pinned tree is not checked out anywhere; read it out of a PSXRecompStudio clone:

```sh
git show 88f5b6f2c209b0980fd96d241f28dce1f840673a:src/PSXRecomp.Analyzer/PSXRecompArchitectureAnalyzer.cs
git archive 88f5b6f2c209b0980fd96d241f28dce1f840673a src/PSXRecomp.Analyzer src/PSXRecomp.Analyzer.Tests | tar -x -C <dir>
```

### 1.2 Baseline files

| File | Lines | Role |
|---|---:|---|
| `PSXRecompArchitectureAnalyzer.cs` | 390 | The single `DiagnosticAnalyzer`; all five callbacks |
| `Architecture/ArchitectureFacts.cs` | 160 | Layer enum, attribute map, namespace map, generated-path test, forbidden edges |
| `Architecture/ArchitectureDiagnostics.cs` | 69 | The six `DiagnosticDescriptor`s |
| `Architecture/ForbiddenApiCatalog.cs` | 126 | Per-layer forbidden API rules and the matching predicate |
| `Architecture/PSXRecompArchitectureAttributes.cs` | 34 | The six marker attributes |
| `PSXRecomp.Analyzer.Tests/**` | 727 | 28 `[Fact]`s across six test classes |

### 1.3 Severity pins at the baseline

`.editorconfig` at the pinned SHA (lines 39–44) pins **all six** rules to `error`:

```ini
dotnet_diagnostic.PSXR001.severity = error
dotnet_diagnostic.PSXR002.severity = error
dotnet_diagnostic.PSXR003.severity = error
dotnet_diagnostic.PSXR004.severity = error
dotnet_diagnostic.PSXR005.severity = error
dotnet_diagnostic.PSXR006.severity = error
```

Each descriptor's `defaultSeverity` is already `Error`, so the pins are belt-and-braces, not a
behavior change.

### 1.4 Evidence provenance

Every expected value in [§4](#4-fixture-matrix) is tagged:

| Tag | Meaning |
|---|---|
| **T** | Asserted by a `[Fact]` in `src/PSXRecomp.Analyzer.Tests/**` at the pinned SHA. The cited line is the test method's declaration. |
| **E** | Not test-covered at the pinned SHA. Obtained by compiling the fixture against the pinned analyzer (`CSharpCompilation.WithAnalyzers(…).GetAnalyzerDiagnosticsAsync()`, `ReferenceAssemblies.Net.Net90`, `LanguageVersion.Latest`) and recording the actual output. Not inferred from reading code. |

Line/column pairs are 1-based and refer to the fixture source shown in the *Scenario* column,
counted from its own first line.

---

## 2. Diagnostic mapping

Each PSXR rule maps to exactly one AARC rule. There is no range mapping: the offsets are a
coincidence of numbering order, not a rule.

| PSXR | PSXR meaning | AARC | Behavioral divergence (one line) |
|---|---|---|---|
| **PSXR001** | A `class` carries no architecture marker attribute | **AARC004** | AARC004 suppresses the whole type when the *first* declaring part is a generated path; PSXR001 falls through to the first non-generated part ([D4](#d4-partial-type-location-selection)). AARC004 is additionally gated by `layerDeclaration.required` + `require_layer_declaration`; PSXR001 is always on. |
| **PSXR002** | A type carries marker attributes for more than one distinct layer | **AARC005** | None behaviorally. Both report once and both `return`, suppressing the namespace check (F-X01 / AARC `PartialConflictingAttributes_ReportsAarc005`). |
| **PSXR003** | The declared layer contradicts the namespace-implied layer | **AARC006** | **Severity: PSXR003 is `Error`, AARC006 is `Warning`** ([D7](#d7-severity)). AARC006's message adds the type name; PSXR003's does not ([D8](#d8-message-and-argument-shape)). AARC006 also requires `validateNamespaceConsistency` (or the `validate_namespace_layer` toggle) to be on. |
| **PSXR004** | Reference across a forbidden layer edge | **AARC002** | None on the enforcement path — AARC's `ResolveLayer` now walks the `ContainingType` chain exactly as PSXR's does ([D2](#d2-attribute-aware-layer-resolution-containingtype-chain)). Remaining nits: AARC002 has no marker-namespace exemption for the *target* ([D10](#d10-marker-namespace-exemption-scope)) and uses longest-prefix rather than first-match namespace resolution ([D1](#d1-namespace-classification-order)). |
| **PSXR005** | Use of an API forbidden in the resolved layer | **AARC003** | None. Identical operation walk, identical `Matches` predicate, identical per-member rule de-duplication, identical message shape. The forbidden-API *catalog* moves from C# to contract JSON ([§6](#6-hardcoded-vs-contract-configurable)). |
| **PSXR006** | `[DllImport]` / `[LibraryImport]` outside `PSXRecomp.Core` | **AARC007** | Three real differences: PSXR006 tests **namespace containment**, AARC007 tests **resolved-layer equality** ([D6](#d6-interop-boundary-predicate)); PSXR006 does not de-duplicate and fires **once per declaring part** (F-I03 → 2), AARC007 de-duplicates to 1 ([D5](#d5-interop-de-duplication-and-count)); AARC007's message carries only `method.Name`, PSXR006's carries the fully qualified signature ([D8](#d8-message-and-argument-shape)). |

### 2.1 Diagnostics with no PSXR counterpart

These two are **ArchitectureAnalyzer-only**. They are *not* mapped to any PSXR diagnostic, they
have no baseline behavior to be compatible with, and the compatibility suite must not expect them
in any PSXR-derived fixture:

| AARC | Title | Why there is no PSXR equivalent |
|---|---|---|
| **AARC001** | Architecture contract could not be loaded | PSXR.Analyzer has no external contract — its rules are compiled-in C#, so "the contract failed to load" is not a reachable state. |
| **AARC008** | Invalid architecture analyzer configuration value | PSXR.Analyzer reads no `architecture_analyzer.*` operational properties; its only configuration surface is the standard `dotnet_diagnostic.*.severity` pins in §1.3. |

Conversely there is no PSXR rule left unmapped: PSXR001–006 is the complete set
(`SupportedDiagnostics`, `PSXRecompArchitectureAnalyzer.cs:19-25`).

---

## 3. Baseline behavior reference

Everything the fixture matrix asserts follows from this section.

### 3.1 Layers, namespaces and markers

Six real layers plus a sentinel (`ArchitectureLayer`, `ArchitectureFacts.cs:7-16`): `Domain`,
`Application`, `Infrastructure`, `Analyzer`, `Test`, `Generated`, and `Unknown = 0` for
"unclassified", which every rule treats as "return, do nothing".

Marker attributes (`ArchitectureFacts.cs:26-34`), matched by
`AttributeClass.OriginalDefinition.ToDisplayString()`, ordinal:

| Attribute FQN | Layer |
|---|---|
| `PSXRecomp.Architecture.DomainAttribute` | `Domain` |
| `PSXRecomp.Architecture.ApplicationAttribute` | `Application` |
| `PSXRecomp.Architecture.InfrastructureAttribute` | `Infrastructure` |
| `PSXRecomp.Architecture.AnalyzerAttribute` | `Analyzer` |
| `PSXRecomp.Architecture.TestAttribute` | `Test` |
| `PSXRecomp.Architecture.GeneratedAttribute` | `Generated` |

All six are `AllowMultiple = false, Inherited = false` and target
`Class | Struct | Interface | Enum | Delegate` (`PSXRecompArchitectureAttributes.cs`). Because
each attribute type maps to exactly one layer and none may repeat, "two attributes, same layer"
is unreachable in PSXR — unlike AARC, where two distinct marker FQNs may map to one layer
(`TwoAttributesMappingToSameLayer_IsNotMultiple`).

Namespace map, evaluated **in this order, first match wins** (`ArchitectureFacts.cs:70-103`):

| # | Namespace root | Layer |
|---:|---|---|
| 1 | `PSXRecomp.Core` | `Domain` |
| 2 | `PSXRecompStudio` | `Application` |
| 3 | `PSXRecomp.Infrastructure` | `Infrastructure` |
| 4 | `PSXRecomp.Tests` | `Test` |
| 5 | `PSXRecomp.Analyzer` | `Analyzer` |
| 6 | `PSXRecomp.Generated` | `Generated` |
| — | anything else | `Unknown` |

Containment is `name == root || name.StartsWith(root + ".")`, ordinal
(`ArchitectureFacts.IsWithin`, `:156-159`). None of the six roots is a prefix of another, so
first-match and longest-prefix agree for this contract.

Marker namespace: `PSXRecomp.Architecture` (`:20`). Interop namespace root: `PSXRecomp.Core` (`:22`).

### 3.2 Layer resolution

`ArchitectureFacts.ResolveLayer` (`:51-63`):

1. Walk `type.OriginalDefinition`, then `.ContainingType`, then its `.ContainingType`, …
2. At each step take `GetAppliedAttributes(current)`; if non-empty return `applied[0].Layer`.
3. If no enclosing type is attributed, return `FromNamespace(type.ContainingNamespace)`.

So a nested type inherits its enclosing type's declared layer, and an attribute anywhere on the
containing chain beats the namespace.

### 3.3 Forbidden dependency edges

`ArchitectureFacts.IsForbiddenDependency` (`:133-154`) — four edges, hardcoded:

| From | To | Reason string |
|---|---|---|
| `Domain` | `Application` | `the Domain layer must not depend on the outer Application layer` |
| `Application` | `Infrastructure` | `the Application layer must reach Infrastructure only through the Domain interop boundary` |
| `Infrastructure` | `Application` | `the Infrastructure layer must not depend on the Application layer` |
| `Domain` \| `Application` \| `Infrastructure` | `Test` | `production code must not depend on test code` |

Everything else is allowed, including `Test → *`, `Analyzer → *`, `Generated → *`, and
`Domain → Infrastructure`.

### 3.4 Forbidden API catalog

`ForbiddenApiCatalog.CreateRules` (`:52-110`). Rule kinds: `AnyMember(type)` — any member, but
**not** the constructor; `NamedMember(type, member)` — that one member only; `WholeType(type)` —
any member *and* the constructor (`ForbiddenApiRule.Matches`, `:25-40`).

| Layer | Rules |
|---|---|
| `Domain` | AnyMember `System.Console`, `System.IO.File`, `System.IO.Directory`, `System.Environment`, `System.Diagnostics.Process`, `System.DateTimeOffset`; NamedMember `System.DateTime.Now`, `System.DateTime.UtcNow`, `System.Guid.NewGuid`; WholeType `System.Random`, `System.Net.Http.HttpClient`, `System.Net.Sockets.Socket` |
| `Application` | AnyMember `System.Console`, `System.IO.File`, `System.IO.Directory` |
| `Infrastructure` | AnyMember `System.Console`, `System.IO.File`, `System.IO.Directory` (Console's reason string differs from Domain's: `…behind an adapter interface`) |
| `Test`, `Analyzer`, `Generated` (one shared array) | AnyMember `System.Console`, `System.IO.File`, `System.IO.Directory`, `System.Environment`, `System.Diagnostics.Process`, `System.Threading.Thread`; NamedMember `System.DateTime.Now`, `System.DateTime.UtcNow`, `System.Guid.NewGuid`, `System.Threading.Tasks.Task.Delay`; WholeType `System.Random` |
| `Unknown` | *(none — the analyzer returns before consulting the catalog)* |

Note the asymmetry: the shared `Test`/`Analyzer`/`Generated` array **adds** `Thread` and
`Task.Delay` but **omits** `DateTimeOffset`, `HttpClient` and `Socket`. F-A17 and F-A18 pin this.

### 3.5 Callback registration

`PSXRecompArchitectureAnalyzer.Initialize` (`:27-43`):

| Callback | Registration | Produces |
|---|---|---|
| `AnalyzeNamedType` | `RegisterSymbolAction(SymbolKind.NamedType)` | PSXR001, PSXR002, PSXR003 |
| `AnalyzeDependencyDirection` | `RegisterCompilationStartAction` → `RegisterSyntaxNodeAction(IdentifierName, GenericName)` | PSXR004 |
| `AnalyzeForbiddenApiUsage` | `RegisterOperationBlockAction` | PSXR005 |
| `AnalyzePInvokeDeclaration` | `RegisterSyntaxNodeAction(MethodDeclaration)` | PSXR006 |

`ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None)` and
`EnableConcurrentExecution()` are both set, so Roslyn's own generated-code exclusion applies on
top of the explicit path checks below.

### 3.6 Per-rule algorithm

**PSXR001 / PSXR002 / PSXR003** (`AnalyzeNamedType`, `:45-107`), in order:

1. Skip unless `TypeKind == Class`; skip if `IsImplicitlyDeclared`. (Records that are classes are
   `TypeKind.Class`; `record struct`, `struct`, `interface`, `enum`, `delegate` are not — F-M08.)
2. Skip if the containing namespace is within `PSXRecomp.Architecture` — F-M07.
3. `GetPrimaryDeclarationLocation`: walk `DeclaringSyntaxReferences` and take the
   `Identifier` location of the **first part whose file is not a generated path**. If every part
   is generated, return — no diagnostic — F-M06, F-M15.
4. `applied = GetAppliedAttributes(type)` (merged across partial parts — F-M03, F-M14).
   If empty: report **PSXR001** unless `ContainingType is not null && ResolveLayer(ContainingType) != Unknown`
   (F-M04, F-M12), then return.
5. If `applied` maps to more than one distinct layer: report **PSXR002** and **return** —
   PSXR003 is suppressed (F-X01).
6. Otherwise, if `FromNamespace(containing namespace) != Unknown` and differs from the declared
   layer: report **PSXR003** (F-N01). An unmapped namespace never mismatches (F-N04).

**PSXR004** (`AnalyzeDependencyDirection`, `:109-164`):

1. Return if the syntax tree's path is a generated path (F-D11).
2. Return if the `SimpleNameSyntax` has an `AttributeSyntax` ancestor — this excludes the
   attribute name *and everything inside its argument list* (F-D06).
3. Resolve the target: `GetSymbolInfo` (falling back to `GetDeclaredSymbol`), then take the
   `OriginalDefinition` of the named type, or of the containing type for a method / property /
   field / event symbol. A `GenericName` therefore resolves to the open generic definition —
   `Scenario.AppBox<T>`, not `AppBox<int>` (F-D05).
4. Return if the target is in the marker namespace, or if `ResolveLayer(target) == Unknown`.
5. Resolve the source: nearest enclosing `TypeDeclarationSyntax` → `ResolveLayer`, which walks the
   `ContainingType` chain (F-D08).
6. Return if the source is unresolved, `Unknown`, or the same layer as the target.
7. Return unless the edge is in §3.3.
8. De-duplicate on `"{source.ToDisplayString()}->{target.ToDisplayString()}"` in a
   **compilation-wide** `ConcurrentDictionary` (F-D07). Report at `name.GetLocation()`.

**PSXR005** (`AnalyzeForbiddenApiUsage`, `:166-252`):

1. Source type = `OwningSymbol`'s containing type for a method / field / property / event; else return.
2. Return if the source type is in the marker namespace (F-A19), or if `ResolveLayer` is `Unknown`
   (F-A15) — note `ResolveLayer` here means attribute-or-namespace, so an unattributed class in
   `PSXRecomp.Core` is still `Domain` (F-A16).
3. Return if the layer has no rules.
4. Depth-first walk every operation in every block, skipping `OperationKind.None`. Consider only
   `IInvocationOperation` (→ `TargetMethod`), `IObjectCreationOperation` (→ `Constructor`) and
   `IMemberReferenceOperation` (→ `Member`) — F-A12 covers the member-reference path.
5. `matchName` = the member name, except property accessors resolve to the property name and
   constructors set `isConstructor`. `ownerFullName` = `member.ContainingType.OriginalDefinition.ToDisplayString()`.
   Because the owner is the *declaring* type, an extension method never matches a rule aimed at
   its receiver type (F-A14).
6. Report each matching rule index **at most once per operation block** — i.e. per member
   (F-A03, F-A13). Location = the matched operation's syntax.

**PSXR006** (`AnalyzePInvokeDeclaration`, `:274-308`):

1. Return if the tree's path is a generated path (F-I06).
2. `GetDeclaredSymbol(MethodDeclaration)`; return unless it is an `IMethodSymbol`.
3. Return unless some attribute's `AttributeClass.OriginalDefinition.ToDisplayString()` is exactly
   `System.Runtime.InteropServices.DllImportAttribute` or
   `System.Runtime.InteropServices.LibraryImportAttribute` — a same-short-name attribute in
   another namespace does not match (F-I05).
4. Return if `method.ContainingNamespace` is within `PSXRecomp.Core` (F-I02, F-I04).
5. Report at `method.Locations.FirstOrDefault()`. **No de-duplication** — the callback runs per
   `MethodDeclaration` node, so a partial method written as a declaring part plus an implementing
   part is reported twice (F-I03).

### 3.7 Generated-path detection

`ArchitectureFacts.IsGeneratedPath` (`:111-131`). Backslashes are normalized to `/` first.

| Test | Applied to | Comparison |
|---|---|---|
| ends with `.g.cs` | file name | OrdinalIgnoreCase |
| ends with `.g.i.cs` | file name | OrdinalIgnoreCase |
| ends with `.designer.cs` | file name | OrdinalIgnoreCase |
| ends with `.generated.cs` | file name | OrdinalIgnoreCase |
| starts with `TemporaryGeneratedFile` | file name | **Ordinal** |
| contains `/obj/` | full path | **Ordinal** |
| contains `/bin/` | full path | **Ordinal** |

A leading relative `obj/…` or `bin/…` (no leading slash) is **not** matched.

---

## 4. Fixture matrix

**57 scenarios.** IDs are stable and normative — #33 should reference these, not re-number.

Columns: *Ev.* is the provenance tag from [§1.4](#14-evidence-provenance); *Count* is the total
number of analyzer diagnostics the fixture produces (not just the rule under test); *Exemption*
names the guard that fires when the count is 0 by design.

### 4.1 PSXR001 — missing layer declaration (15)

| ID | Kind | Scenario | Count | Expected semantic condition | Exemption | AARC | Ev. | Divergence |
|---|---|---|---:|---|---|---|---|---|
| F-M01 | valid | `namespace Scenario; [Domain] internal sealed class Tagged {}` | 0 | one recognized marker present | — | AARC004 | T `MissingArchitectureAttributeTests.cs:9` | — |
| F-M02 | violation | `namespace Scenario; internal sealed class Untagged {}` | 1 | PSXR001 @ (3,23), args `Scenario.Untagged`, `Domain, Application, Infrastructure, Analyzer, Test, Generated` | — | AARC004 | T `:26` | AARC004 message has no attribute-name list ([D8](#d8-message-and-argument-shape)) |
| F-M03 | valid | `[Domain] partial class Split {}` + a second unattributed `partial class Split {}` in the same file | 0 | attributes merge across partial parts | partial qualified by any part | AARC004 | T `:47` | — |
| F-M04 | valid | `[Domain] class Outer { class Inner {} }` | 0 | `ResolveLayer(ContainingType) != Unknown` | nested under a resolved layer | AARC004 | T `:68` | — |
| F-M05 | violation | `namespace Scenario; class OuterUntagged { class InnerUntagged {} }` (namespace unmapped) | 2 | PSXR001 @ (3,14) `Scenario.OuterUntagged`; PSXR001 @ (5,18) `Scenario.OuterUntagged.InnerUntagged` | — | AARC004 | T `:88` | — |
| F-M06 | valid | the F-M05 outer class alone, in a file named `Generated.g.cs` | 0 | every declaring part is a generated path | generated path | AARC004 | T `:121` | AARC uses `Locations[0]` not "all parts" ([D4](#d4-partial-type-location-selection)) |
| F-M07 | valid | `namespace PSXRecomp.Architecture; class NotAMarker {}` | 0 | containing namespace within the marker namespace | marker namespace | AARC004 | T `:138` | — |
| F-M08 | valid | `namespace Scenario;` + `struct BareStruct`, `interface IBare`, `enum BareEnum`, `delegate void BareDelegate()`, `record struct BareRecordStruct(int)` — all unattributed | 0 | `TypeKind != Class` for all five | non-class kind | AARC004 | E | AARC004 applies the identical `TypeKind != Class` guard |
| F-M09 | violation | `namespace Scenario; public record BareRecord(int Value);` | 1 | PSXR001 @ (3,15) `Scenario.BareRecord` — a `record class` is `TypeKind.Class` | — | AARC004 | E | — |
| F-M10 | violation | `namespace Scenario; public sealed class Box<T> {}` | 1 | PSXR001 @ (3,21), name rendered `Scenario.Box<T>` | — | AARC004 | E | — |
| F-M11 | violation | `namespace Scenario; public static class Helpers {}` | 1 | PSXR001 @ (3,21) — static classes are not exempt | — | AARC004 | E | — |
| F-M12 | violation | `namespace PSXRecomp.Core; class OuterUntagged { class InnerUntagged {} }` | 1 | PSXR001 @ (3,14) on the outer **only**; the inner is exempt because `ResolveLayer(Outer)` falls back to the namespace → `Domain` | nested under a namespace-resolved layer | AARC004 | E | AARC's `IsNestedInDeclaredLayer` uses the same namespace fallback |
| F-M13 | violation | unattributed `partial class Split` split across `Split.g.cs` (added first) and `Split.cs` | 1 | PSXR001 reported in **`Split.cs`** @ (3,29) — the first non-generated part | — | AARC004 | E | **AARC004 reports 0 here** ([D4](#d4-partial-type-location-selection)) |
| F-M14 | valid | `partial class Split` with `[Domain]` on the `Split.g.cs` part and nothing on `Split.cs` | 0 | attributes merge even from generated parts | partial qualified by any part | AARC004 | E | — |
| F-M15 | valid | unattributed `partial class Split` split across `Split.g.cs` and `Split.designer.cs` | 0 | `GetPrimaryDeclarationLocation` returns null | all parts generated | AARC004 | E | — |

### 4.2 PSXR002 — multiple layer declarations (2)

| ID | Kind | Scenario | Count | Expected semantic condition | Exemption | AARC | Ev. | Divergence |
|---|---|---|---:|---|---|---|---|---|
| F-P01 | violation | `namespace Scenario; [Domain] [Application] internal sealed class Conflicted {}` | 1 | PSXR002 @ (7,23), args `Scenario.Conflicted`, `Domain, Application` (layer order = attribute order) | — | AARC005 | T `MultipleArchitectureAttributesTests.cs:9` | — |
| F-X01 | violation | `namespace PSXRecompStudio.ViewModels; [Domain] [Infrastructure] class Conflicted {}` | 1 | PSXR002 @ (7,21) only — **PSXR003 is suppressed** by the early `return`, even though `Domain` also contradicts the `Application` namespace | — | AARC005 | E | AARC005 `return`s identically |

"Two marker attributes mapping to the *same* layer" has no PSXR fixture: each PSXR marker type
maps to a distinct layer and all are `AllowMultiple = false`, so the state is unreachable. AARC
covers it separately (`TwoAttributesMappingToSameLayer_IsNotMultiple`); it is an AARC superset,
not a parity gap.

### 4.3 PSXR003 — namespace/layer mismatch (4)

| ID | Kind | Scenario | Count | Expected semantic condition | Exemption | AARC | Ev. | Divergence |
|---|---|---|---:|---|---|---|---|---|
| F-N01 | violation | `namespace PSXRecompStudio.ViewModels; [Domain] internal sealed class Misplaced {}` | 1 | PSXR003 @ (6,23), args `Domain`, `PSXRecompStudio.ViewModels`, `Application` — **three** args, no type name | — | AARC006 | T `NamespaceLayerMismatchTests.cs:9` | AARC006 is `Warning` and takes four args ([D7](#d7-severity), [D8](#d8-message-and-argument-shape)) |
| F-N02 | valid | `namespace PSXRecomp.Core; [Domain] internal sealed class Consistent {}` | 0 | declared layer == namespace layer | — | AARC006 | T `:34` | — |
| F-N03 | valid | `namespace PSXRecomp.Infrastructure; [Infrastructure] internal sealed class NativeAdapter {}` | 0 | declared layer == namespace layer | — | AARC006 | T `:51` | — |
| F-N04 | valid | `namespace Scenario; [Infrastructure] public sealed class Anywhere {}` | 0 | `FromNamespace("Scenario") == Unknown`, so no contradiction is possible | unmapped namespace | AARC006 | E | AARC's `ResolveLayer` returns `null` here; same outcome |

### 4.4 PSXR004 — forbidden dependency (11)

| ID | Kind | Scenario | Count | Expected semantic condition | Exemption | AARC | Ev. | Divergence |
|---|---|---|---:|---|---|---|---|---|
| F-D01 | violation | `[Application] class AppService {}` + `[Domain] class DomainService { AppService Dependency = new(); }` | 1 | PSXR004 @ (13,23), args `Scenario.DomainService`, `Domain`, `Scenario.AppService`, `Application`, `the Domain layer must not depend on the outer Application layer` | — | AARC002 | T `DependencyDirectionTests.cs:9` | — |
| F-D02 | violation | `[Test] class TestHelper {}` + `[Domain] class ProductionType { TestHelper Helper { get; set; } }` | 1 | PSXR004 @ (13,12), reason `production code must not depend on test code` | — | AARC002 | T `:42` | — |
| F-D03 | valid | `[Domain] class DomainEntity {}` + `[Application] class AppFacade { DomainEntity Create() => new DomainEntity(); }` | 0 | `Application → Domain` is not a forbidden edge | — | AARC002 | T `:75` | — |
| F-D04 | valid | `[Domain] class DomainEntity {}` + `[Test] class DomainEntityTests { … }` | 0 | `Test → Domain` is not a forbidden edge | — | AARC002 | T `:98` | — |
| F-D05 | violation | `[Application] class AppBox<T> {}` + `[Domain] class DomainHolder { AppBox<int> Box = new(); }` | 1 | PSXR004 @ (13,23) with target rendered **`Scenario.AppBox<T>`** — `GenericName` resolves to the open definition | — | AARC002 | E | AARC's `ResolveReferencedType` is byte-identical |
| F-D06 | valid | `[Application] class AppMarkerAttribute : Attribute {}` + `[Domain] class DomainConsumer { [AppMarker] void Tagged() {} }` | 0 | the identifier has an `AttributeSyntax` ancestor | attribute syntax | AARC002 | E | AARC applies the identical guard |
| F-D07 | violation | `[Domain] class DomainService` referencing `[Application] AppService` **three** times (two fields + one `new`) | 1 | PSXR004 @ (14,23) only — de-duplicated per source→target pair, compilation-wide | duplicate suppression | AARC002 | E | AARC reports once, deterministically at the **earliest** site (`RepeatedReferenceToSamePair_IsReportedOnceAtEarliestSite`). The baseline reports at whatever site won the concurrent de-dup race — measured at lines 13/14/16 across runs — so this category is compared without its position ([D9](#d9-source-location)). |
| F-D08 | violation | `[Domain] class Outer { class Inner { AppService Dependency = new(); } }` with `[Application] class AppService` | 1 | PSXR004 @ (15,27), source rendered `Scenario.Outer.Inner` resolved to **`Domain`** via the `ContainingType` chain | — | AARC002 | E | **Previously reported as the biggest gap; now closed** ([D2](#d2-attribute-aware-layer-resolution-containingtype-chain)) |
| F-D09 | violation | `[Infrastructure] class NativeAdapter {}` + `[Application] class Facade { NativeAdapter Adapter = new(); }` | 1 | PSXR004 @ (13,23), reason `the Application layer must reach Infrastructure only through the Domain interop boundary` | — | AARC002 | E | — |
| F-D10 | violation | `[Application] class AppService {}` + `[Infrastructure] class NativeAdapter { AppService Service = new(); }` | 1 | PSXR004 @ (13,23), reason `the Infrastructure layer must not depend on the Application layer` | — | AARC002 | E | — |
| F-D11 | valid | the F-D01 source placed in a file named `Wired.g.cs` | 0 | `IsGeneratedPath` short-circuits the callback | generated path | AARC002 | E | AARC gates this behind `skip_generated_code` (default `true` → same result) |

### 4.5 PSXR005 — forbidden API (19)

| ID | Kind | Scenario | Count | Expected semantic condition | Exemption | AARC | Ev. | Divergence |
|---|---|---|---:|---|---|---|---|---|
| F-A01 | violation | `[Domain] class Greeter { void Greet() => Console.WriteLine("hello"); }` | 1 | PSXR005 @ (9,28), args `Console.WriteLine`, `Domain`, `standard output must be abstracted behind an Infrastructure adapter` — invocation path | — | AARC003 | T `ForbiddenApiTests.cs:9` | — |
| F-A02 | violation | `[Domain] class Clock { long Stamp() => DateTime.Now.Ticks; }` | 1 | PSXR005 @ (9,28), args `DateTime.Now`, … — property member-reference path, accessor resolved to the property name | — | AARC003 | T `:36` | — |
| F-A03 | violation | `[Domain] class Picker { int Pick() => Random.Shared.Next(); }` | 1 | PSXR005 @ (9,26) args `Random.Next` — the chain matches the `WholeType("System.Random")` rule at both `Random.Shared` and `.Next()`, and the per-member rule set collapses it to **one** | duplicate suppression | AARC003 | T `:63` | — |
| F-A04 | violation | `[Domain] class Roller { Random _random = new Random(42); int Roll() => _random.Next(); }` | 2 | PSXR005 @ (9,39) args `new Random()`; PSXR005 @ (11,26) args `Random.Next` — separate operation blocks (field initializer vs method), so the rule fires once in each | — | AARC003 | T `:90` | — |
| F-A05 | violation | `[Domain] class IdFactory { Guid NewId() => Guid.NewGuid(); }` | 1 | PSXR005 @ (9,28) args `Guid.NewGuid` — `NamedMember` match | — | AARC003 | T `:129` | — |
| F-A06 | violation | `[Application] class StorageProbe { bool Exists(string p) => File.Exists(p); }` | 1 | PSXR005 @ (9,40) args `File.Exists`, `Application`, `external I/O is an Infrastructure responsibility` | — | AARC003 | T `:156` | — |
| F-A07 | violation | `[Domain] class NetworkProbe { HttpClient Client = new System.Net.Http.HttpClient(); }` | 1 | PSXR005 @ (9,9) args `new HttpClient()` — `WholeType` matches the constructor | — | AARC003 | T `:183` | — |
| F-A08 | violation | `[Test] class AsyncProbe { void Wait() => Task.Delay(1); }` | 1 | PSXR005 @ (9,27) args `Task.Delay`, `Test`, `asynchronous timing must use controlled schedulers` — the shared Test/Analyzer/Generated array | — | AARC003 | T `:210` | — |
| F-A09 | violation | `[Domain] class Runner { Process Child = new Process(); }` | 1 | PSXR005 @ (10,39) args `new Process()` — `AnyMember` still matches a constructor because `MemberName is null` | — | AARC003 | T `:237` | — |
| F-A10 | violation | `[Test] class Worker { Thread WorkerThread = new Thread(() => {}); }` | 1 | PSXR005 @ (10,45) args `new Thread()`, `Test` | — | AARC003 | T `:265` | — |
| F-A11 | valid | `[Domain] class Calculator { int Max(...) => Math.Max(...); long Elapsed(...) => after - before; }` | 0 | no catalog entry matches `System.Math` | — | AARC003 | T `:293` | — |
| F-A12 | violation | `[Domain] class Liner { string Break() => Environment.NewLine; }` | 1 | PSXR005 @ (9,30) args `Environment.NewLine`, `Domain`, `execution environment dependencies break determinism` — pure `IMemberReferenceOperation`, no invocation | — | AARC003 | E | — |
| F-A13 | violation | `[Domain] class Chatty { void One() { Console.WriteLine("a"); Console.WriteLine("b"); } void Two() { Console.WriteLine("c"); } }` | 2 | PSXR005 @ (12,9) and @ (17,9) — the rule collapses **within** a member but not **across** members | duplicate suppression (per member) | AARC003 | E | AARC's `reportedRules` has the same per-block lifetime |
| F-A14 | valid | `[Domain] static class RandomExtensions { static int Next2(this Random s) => 0; }` + `[Domain] class Roller { int Roll(Random s) => s.Next2(); }` | 0 | `TargetMethod.ContainingType` is `Scenario.RandomExtensions`, not `System.Random`, so no rule matches | — | AARC003 | E | **Shared blind spot**, identical in AARC ([D14](#d14-extension-method-blind-spot-shared)) |
| F-A15 | violation | `namespace Unmapped.Space; class Loud { void Say() => Console.WriteLine("x"); }` (no attribute) | 1 | **0 × PSXR005** — `ResolveLayer` is `Unknown`, so the catalog is never consulted. The single diagnostic is PSXR001 @ (5,21), unrelated to this rule | unclassified layer | AARC003 | E | AARC003 matches at the default `require_layer_declaration = true`; setting it to `false` makes AARC003 *stricter* than the baseline |
| F-A16 | violation | `namespace PSXRecomp.Core; [Domain] class Loud { void Say() => Console.WriteLine("x"); }` | 1 | PSXR005 @ (9,26) — layer resolution reaches `Domain` and the rule applies | — | AARC003 | E | — |
| F-A17 | violation | `[Domain] class DomainStamp { DateTimeOffset Now() => DateTimeOffset.UtcNow; }` + `[Test] class TestStamp { … same body … }` | 1 | PSXR005 @ (9,36) on the **Domain** type only — the shared Test/Analyzer/Generated array has no `System.DateTimeOffset` rule | catalog asymmetry | AARC003 | E | Catalog data, reproduced verbatim in the contract JSON |
| F-A18 | valid | `[Test] class NetProbe { HttpClient Client = new System.Net.Http.HttpClient(); }` | 0 | the shared Test/Analyzer/Generated array has no `HttpClient` rule | catalog asymmetry | AARC003 | E | Catalog data, reproduced verbatim in the contract JSON |
| F-A19 | valid | `namespace PSXRecomp.Architecture; class MarkerHelper { void Say() => Console.WriteLine("x"); }` | 0 | source type is in the marker namespace | marker namespace | AARC003 | E | **AARC003 has no marker-namespace exemption** ([D10](#d10-marker-namespace-exemption-scope)) |

### 4.6 PSXR006 — interop boundary (6)

| ID | Kind | Scenario | Count | Expected semantic condition | Exemption | AARC | Ev. | Divergence |
|---|---|---|---:|---|---|---|---|---|
| F-I01 | violation | `namespace Scenario; [Infrastructure] static class NativeCalls { [DllImport("psx")] static extern void DoWork(); }` | 1 | PSXR006 @ (10,33), arg `Scenario.NativeCalls.DoWork()` | — | AARC007 | T `InteropBoundaryTests.cs:9` | AARC007's message carries only `DoWork` ([D8](#d8-message-and-argument-shape)) |
| F-I02 | valid | the same declaration in `namespace PSXRecomp.Core` with `[Domain]` | 0 | containing namespace within `PSXRecomp.Core` | interop namespace | AARC007 | T `:35` | AARC007 tests layer equality, not namespace containment ([D6](#d6-interop-boundary-predicate)) |
| F-I03 | violation | `namespace Scenario; [Infrastructure] static partial class NativeCalls { [LibraryImport("psx")] static partial void DoWork(); static partial void DoWork() {} }` | **2** | PSXR006 @ (10,34) **and** @ (12,34) — same message both times; the callback runs per `MethodDeclaration` and there is no de-duplication | — | AARC007 | E | **AARC007 reports 1** ([D5](#d5-interop-de-duplication-and-count)) |
| F-I04 | valid | the F-I03 declaration in `namespace PSXRecomp.Core` with `[Domain]` | 0 | containing namespace within `PSXRecomp.Core` | interop namespace | AARC007 | E | — |
| F-I05 | valid | a locally declared `Scenario.DllImportAttribute` applied to a non-`extern` method in `namespace Scenario` | 0 | attribute FQN is `Scenario.DllImportAttribute`, not the `System.Runtime.InteropServices` one | short-name collision | AARC007 | E | AARC `FakeDllImportWithSameShortName_DifferentNamespace_IsSilent` matches |
| F-I06 | valid | the F-I01 declaration in a file named `Native.g.cs` | 0 | `IsGeneratedPath` short-circuits the callback | generated path | AARC007 | E | AARC007 skips generated paths unconditionally (not gated by `skip_generated_code`) |

### 4.7 Coverage checklist

| Required topic | Fixtures |
|---|---|
| missing declaration | F-M01 … F-M15 |
| multiple declaration | F-P01, F-X01 |
| namespace mismatch | F-N01 … F-N04 |
| nested inheritance | F-M04, F-M12, F-D08 |
| partial type | F-M03, F-M13, F-M14, F-M15, F-I03 |
| generated code | F-M06, F-M13, F-M14, F-M15, F-D11, F-I06 |
| forbidden dependency | F-D01 … F-D11 |
| generic name | F-D05, F-M10 |
| attributes excluded from dependency detection | F-D06 |
| forbidden API — invocation | F-A01, F-A03, F-A05, F-A06, F-A08, F-A13 |
| forbidden API — object creation | F-A04, F-A07, F-A09, F-A10, F-A18 |
| forbidden API — member access | F-A02, F-A12, F-A17 |
| extension methods | F-A14 |
| duplicate suppression | F-D07, F-A03, F-A13, F-I03 |
| DllImport | F-I01, F-I02, F-I06 |
| LibraryImport | F-I03, F-I04 |
| interop partial method | F-I03, F-I04 |
| fake short-name attribute | F-I05 |
| clean / valid scenarios | F-M01, F-M03, F-M04, F-M07, F-M08, F-M14, F-M15, F-N02, F-N03, F-N04, F-D03, F-D04, F-D06, F-D11, F-A11, F-A14, F-A18, F-A19, F-I02, F-I04, F-I05, F-I06 |

### 4.8 Notes for #33 fixture authors

- **`LibraryImport` needs a hand-written implementing part.** Roslyn's analyzer test harness does
  not run `LibraryImportGenerator`, so `static partial void DoWork();` alone fails to compile
  (CS8795). F-I03/F-I04 supply the implementing part explicitly. This is also what the existing
  AARC test `LibraryImport_ForbiddenLayer_ReportsAarc007` does.
- **Two-part partial methods double the PSXR006 count.** Any parity assertion on F-I03 must expect
  2 for PSXR and 1 for AARC.
- **Syntax-tree order matters for F-M13.** The generated part must be added to the compilation
  *first* for the divergence to be observable.
- **Fixtures need the marker attribute source.** Every `[Domain]`-style fixture must compile
  `PSXRecompArchitectureAttributes.cs` (or an AARC-side equivalent) into the test compilation.

---

## 5. Behavioral divergences

Evaluated against `src/ArchitectureAnalyzer/**` at `33c8ea6` — i.e. **after** #30, #32, #37 and
#38 merged. Several divergences recorded in the earlier investigation comment on #28 predate
those merges and are no longer true; they are marked **CLOSED** below rather than deleted, so the
record of what changed survives.

> **This is not a byte-for-byte compatibility requirement.** The acceptance question is whether
> ArchitectureAnalyzer has **equal or greater architecture-enforcement capability** than the
> baseline — i.e. whether every architectural fault PSXR.Analyzer would have caught is still
> caught. Diagnostic IDs, severities, message wording, argument counts and de-duplication
> granularity may differ freely as long as no violation becomes invisible. Where AARC catches
> *more* than the baseline, that is an improvement, not a divergence to fix.

### D1. Namespace classification order

| | |
|---|---|
| PSXR | `ArchitectureFacts.FromNamespace` (`:70-103`) — a hardcoded `if` cascade, **first match wins**. |
| AARC | `ArchitectureContract.ResolveLayer` (`ArchitectureContract.cs:308-324`) — `_namespaceRootsLongestFirst`, **longest matching prefix**, ties broken ordinally. |
| Impact | **None for this contract.** No PSXR namespace root is a prefix of another (§3.1), so both algorithms return the same layer for every input. AARC's is order-independent, therefore strictly safer for contracts that do nest roots. |
| Status | Open by construction; capability-equal. The #28 comment's assessment ("impact is minimal") is confirmed. |

### D2. Attribute-aware layer resolution (`ContainingType` chain)

| | |
|---|---|
| PSXR | `ArchitectureFacts.ResolveLayer` (`:51-63`) walks the `ContainingType` chain, first attributed ancestor wins, namespace as fallback. |
| AARC | `ArchitectureContractAnalyzer.ResolveLayer` (`:438-453`) walks `ContainingTypeChain(type.OriginalDefinition)` via `GetAppliedMarkerLayers`, first attributed ancestor wins, `contract.ResolveLayer(namespace)` as fallback. |
| Impact | The two algorithms now have the same shape. Used by AARC002 (`ResolveEnclosingType`, `ResolveOperationalLayer`), AARC003 and AARC007. |
| Status | **CLOSED.** The #28 comment called this "the most significant gap" and said AARC "uses namespace-only". That was true when the comment was written; #32 landed the marker-attribute model and #37 wired it into AARC007. F-D08 pins the PSXR side; AARC's `NestedType_OwnAttributeOverridesEnclosingNamespace` and `MarkerOverride_ToAllowedLayer_IsSilent` pin the AARC side. |

### D3. Generated-file detection

| | |
|---|---|
| PSXR | `ArchitectureFacts.IsGeneratedPath` (`:111-131`) — see §3.7. Suffixes on the file name; `TemporaryGeneratedFile` prefix; `/obj/` and `/bin/` substring, **case-sensitive**. |
| AARC | `ArchitectureContractAnalyzer.IsGeneratedPath` (`:723-740`) — the same four suffixes (applied to the whole normalized path, equivalent for suffix tests); `/obj/` and `/bin/` **case-insensitive**; **plus** leading `obj/` and `bin/`; **no** `TemporaryGeneratedFile` prefix test. |
| Impact | AARC's set is a strict superset except for `TemporaryGeneratedFile*`. That prefix is a legacy WinForms/MSBuild artifact; a file so named that also lives under `obj/` is still caught. A file named `TemporaryGeneratedFile_*.cs` outside `obj/` would be analyzed by AARC and skipped by PSXR — AARC is *stricter*, which cannot make a violation invisible. |
| Additional | AARC exposes `architecture_analyzer.skip_generated_code` / `generated_code` for AARC002 and AARC003 (default: skip, matching the baseline). AARC004–007 skip generated paths unconditionally. PSXR has no toggle. |
| Status | Open, capability-superset. The #28 comment's description is confirmed accurate. |

### D4. Partial-type location selection

| | |
|---|---|
| PSXR | `GetPrimaryDeclarationLocation` (`:310-323`) iterates **all** `DeclaringSyntaxReferences` and returns the `Identifier` location of the first part in a non-generated file; returns `null` (suppressing PSXR001–003) only when *every* part is generated. |
| AARC | `AnalyzeLayerDeclaration` (`:338-344`) bails when `IsGeneratedPath(type.Locations.FirstOrDefault()?.SourceTree?.FilePath)` — it inspects **only the first** location and reports at `type.Locations.FirstOrDefault()`. |
| Impact | For a partial class split across a generated and a hand-written part, AARC004/005/006 go silent whenever the generated part happens to be first in syntax-tree order. **AARC is weaker here** — a real missing declaration becomes invisible. |
| Fixture | F-M13 (PSXR: 1 diagnostic in `Split.cs`; AARC: expected 0). |
| Status | Open. **Not recorded in the #28 comment.** Small fix: mirror PSXR's "first non-generated part" walk. Recommend filing as a follow-up against the AARC004 implementation rather than blocking the baseline. |

### D5. Interop de-duplication and count

| | |
|---|---|
| PSXR | `AnalyzePInvokeDeclaration` runs per `MethodDeclaration` syntax node with no de-duplication (`:274-308`). |
| AARC | `AnalyzeInteropBoundary` de-duplicates on `method.ToDisplayString()`, a `U+001F` unit separator, and the attribute FQN (`:548`). |
| Impact | A partial method written as declaring part + implementing part yields **2** PSXR006 and **1** AARC007 (F-I03, empirically confirmed on the PSXR side). Fewer duplicate reports for the same fault; no fault becomes invisible. |
| Status | Open, AARC preferable. **Not recorded in the #28 comment.** Parity assertions must not compare raw counts across the two analyzers for partial interop methods. |

### D6. Interop-boundary predicate

| | |
|---|---|
| PSXR | Allowed iff `IsWithin(method.ContainingNamespace, "PSXRecomp.Core")` (`:298-302`) — pure **namespace containment**. |
| AARC | Allowed iff `ResolveLayer(method.ContainingType) == rule.AllowedLayer` (`:538-543`) — **resolved-layer equality**, attribute-aware. |
| Impact | Two asymmetric cases. (a) A type inside `PSXRecomp.Core` bearing a non-interop marker (e.g. `[Infrastructure]`) is allowed by PSXR006 but flagged by AARC007 — AARC stricter. (b) A type *outside* the interop namespace bearing the interop layer's marker is flagged by PSXR006 but allowed by AARC007 — AARC laxer, but only for code that has explicitly declared itself part of the interop layer, which is the intended override. A contract that maps the interop layer to exactly the `PSXRecomp.Core` namespace root and declares no marker overrides reproduces the baseline exactly. |
| Status | Open by design; capability-equal for the baseline's contract shape. **Not recorded in the #28 comment.** |

### D7. Severity

| PSXR | Baseline severity | AARC | AARC severity |
|---|---|---|---|
| PSXR001 | Error | AARC004 | Error |
| PSXR002 | Error | AARC005 | Error |
| PSXR003 | Error | AARC006 | **Warning** |
| PSXR004 | Error | AARC002 | Error |
| PSXR005 | Error | AARC003 | Error |
| PSXR006 | Error | AARC007 | Error |
| — | — | AARC001 | Error |
| — | — | AARC008 | Warning |

AARC006 is deliberately a `Warning`: an attribute that overrides the namespace-implied layer is a
legitimate, intentional act in the generic model, whereas in PSXR the namespace map was
authoritative. A migrating project restores baseline strictness with one `.editorconfig` line:

```ini
dotnet_diagnostic.AARC006.severity = error
```

Status: open by design. **Not recorded in the #28 comment.** The migration checklist must include
this pin, otherwise a class of build breaks silently degrades to a warning.

### D8. Message and argument shape

| PSXR | Args | AARC | Args | Note |
|---|---|---|---|---|
| PSXR001 | `{typeName}`, `{attributeNameList}` | AARC004 | `{typeName}` | AARC drops the "here are your options" list; the marker set is in the contract instead. |
| PSXR002 | `{typeName}`, `{layers}` | AARC005 | `{typeName}`, `{layers}` | Same shape. |
| PSXR003 | `{declaredLayer}`, `{namespace}`, `{namespaceLayer}` | AARC006 | `{typeName}`, `{declaredLayer}`, `{namespace}`, `{namespaceLayer}` | AARC **adds** the type name — strictly more useful. |
| PSXR004 | `{src}`, `{srcLayer}`, `{dst}`, `{dstLayer}`, `{reason}` | AARC002 | identical | Message formats are character-identical. |
| PSXR005 | `{api}`, `{layer}`, `{reason}` | AARC003 | identical | Message formats are character-identical. |
| PSXR006 | `{method.ToDisplayString()}` — e.g. `Scenario.NativeCalls.DoWork()` | AARC007 | `{method.Name}` — e.g. `DoWork`, plus `{allowedLayer}`, `{reason}` | **AARC loses the qualified name.** Worth a follow-up: `method.ToDisplayString()` costs nothing and disambiguates overloads. |

Status: open; cosmetic except the PSXR006 → AARC007 name loss. **Not recorded in the #28 comment.**

### D9. Source location

| Rule pair | PSXR location | AARC location | Divergence |
|---|---|---|---|
| PSXR001/002/003 → AARC004/005/006 | `TypeDeclarationSyntax.Identifier` of the first **non-generated** part | `type.Locations.FirstOrDefault()` (also the identifier token, but of the first part regardless of generated status) | Identical for single-part types; see [D4](#d4-partial-type-location-selection) for partials. |
| PSXR004 → AARC002 | `name.GetLocation()` of the **de-dup winner** — a concurrent syntax-node action race, so an arbitrary reference site (F-D07, measured at lines 13/14/16 across runs) | `name.GetLocation()` of the **earliest** reference site, deferred to compilation end — deterministic (<code>MinLocation</code> in `AnalyzeDependencyDirection`) | AARC more deterministic. A deduplicated pair's site carries no information its facts lack, so the parity suite compares this category without position. |
| PSXR005 → AARC003 | `operation.Syntax.GetLocation()` | `operation.Syntax.GetLocation()` | None. |
| PSXR006 → AARC007 | `method.Locations.FirstOrDefault()` — order-dependent for partials, and reported at every part (D5) | the `Identifier` of the part that actually carries the matched attribute (`:529-533`), falling back to `Locations[0]` | AARC is deterministic; PSXR is not. AARC preferable. |

### D10. Marker-namespace exemption scope

| | |
|---|---|
| PSXR | The `PSXRecomp.Architecture` namespace is exempt in three places: `AnalyzeNamedType` (`:53`, covers PSXR001–003), as a PSXR004 **target** (`:126`), and as a PSXR005 **source** (`:182`). |
| AARC | Only `AnalyzeLayerDeclaration` (`:358`) honours `layerDeclaration.markerNamespace`. AARC002 and AARC003 have no marker-namespace exemption. |
| Impact | Marker attribute types are empty and are normally referenced only inside an `AttributeSyntax`, which AARC002 already skips. The observable gap is a `typeof(DomainAttribute)` / `nameof` reference outside attribute syntax, and forbidden-API use inside a marker type's own body (F-A19). In both cases AARC is *stricter*. |
| Status | Open, capability-superset. **Not recorded in the #28 comment.** |

### D11. AARC-only capabilities with no baseline counterpart

Not divergences — additions. Listed so the parity suite does not mistake them for regressions.

| Capability | Where |
|---|---|
| AARC001 — contract load failure is itself a diagnostic | `ArchitectureContractAnalyzer.cs:77-94` |
| AARC008 — invalid operational config value | `Configuration/ConfigReader.cs` |
| Operational config: `enabled`, `rule.<ID>.enabled`, `require_layer_declaration`, `validate_namespace_layer`, `skip_generated_code` / `generated_code`, `contract_required` | `Configuration/OperationalConfig.cs` |
| Synthetic `Unclassified` layer, so unclassified code can participate in edge checks | `ConfigReader.UnclassifiedLayerName`, `ArchitectureContractAnalyzer.cs:695-698` |
| Multiple marker attribute FQNs mapping to one layer | `GetAppliedMarkerLayers`, `:413-432` |
| Contract-level validation (undeclared layer references, duplicate marker FQNs, `required` without markers) | `Contract/ArchitectureContractLoader.cs` |
| Opt-in semantics: no `architecture.contract.json` → the analyzer is a complete no-op | `ArchitectureContractAnalyzer.cs:63-71` |

All operational defaults (`require_layer_declaration = true`, `skip_generated_code = true`,
`validate_namespace_layer = false`, `contract_required = true`, `enabled = true`) reproduce
baseline semantics, so a migrating project needs no `.editorconfig` operational entries at all.

### D12. Symbol-kind coverage

Both analyzers restrict declaration rules to `TypeKind.Class` and skip `IsImplicitlyDeclared`
(`PSXRecompArchitectureAnalyzer.cs:48`, `ArchitectureContractAnalyzer.cs:338-344`). `record class`
is in scope, `record struct` / `struct` / `interface` / `enum` / `delegate` are not (F-M08, F-M09).
Dependency and forbidden-API analysis are kind-agnostic in both. **No divergence.**

### D13. Forbidden-API catalog contents

The Test/Analyzer/Generated array's asymmetry (§3.4 — adds `Thread` and `Task.Delay`, omits
`DateTimeOffset`, `HttpClient`, `Socket`) is **contract data, not analyzer behavior**. It must be
transcribed verbatim into `architecture.contract.json`; getting it wrong shows up as F-A17 /
F-A18 failures, not as an analyzer bug. **No divergence** in the analyzer.

### D14. Extension-method blind spot (shared)

Both analyzers match a forbidden-API rule against `member.ContainingType.OriginalDefinition`. For
an extension method the containing type is the **static declaring class**, never the receiver
type, so `someRandom.MyExtension()` matches no `System.Random` rule (F-A14 → 0). This is
identical on both sides and therefore **not a divergence**, but the parity suite should pin it so
that a future change to either analyzer is caught.

### D15. Divergence summary

| ID | Direction | Blocks parity? |
|---|---|---|
| D1 namespace order | AARC safer | No |
| D2 `ContainingType` chain | **CLOSED** | No |
| D3 generated-file detection | AARC superset (minus `TemporaryGeneratedFile*`) | No |
| D4 partial-type location | **AARC weaker — a real missing declaration can go unreported** | **Follow-up** |
| D5 interop de-duplication | AARC fewer duplicates, same faults | No |
| D6 interop predicate | Equal for the baseline contract shape | No |
| D7 AARC006 severity | AARC laxer unless pinned | **Migration checklist item** |
| D8 message shape | Cosmetic, except AARC007 losing the qualified method name | Follow-up (nice-to-have) |
| D9 source location | AARC more deterministic | No |
| D10 marker-namespace exemption | AARC stricter | No |
| D11 AARC-only capabilities | AARC superset | No |
| D12 symbol kinds | Identical | No |
| D13 catalog contents | Contract data | No |
| D14 extension methods | Identical shared limitation | No |

**Verdict:** with D4 fixed and D7 pinned in the consuming project's `.editorconfig`,
ArchitectureAnalyzer has equal-or-greater architecture-enforcement capability than the pinned
baseline for every fixture in §4.

---

## 6. Hardcoded vs contract-configurable

Which parts of the baseline are compiled-in C# and where each lands in the generic model.

| Baseline knowledge | Baseline location | Generic model destination |
|---|---|---|
| Six layer names | `ArchitectureLayer` enum | `layers[].name` |
| Six namespace roots and their layers | `FromNamespace` | `layers[].namespaceRoots` |
| Six marker attribute FQNs → layers | `AttributeFullNameToLayer` | `layerDeclaration.markerAttributes[]` |
| "Every class must declare a layer" | unconditional in `AnalyzeNamedType` | `layerDeclaration.required` (+ `require_layer_declaration`) |
| "Attribute must not contradict namespace" | unconditional in `AnalyzeNamedType` | `layerDeclaration.validateNamespaceConsistency` (+ `validate_namespace_layer`) |
| Marker namespace `PSXRecomp.Architecture` | `ArchitectureFacts.MarkerNamespace` | `layerDeclaration.markerNamespace` |
| Four forbidden edges and their reason strings | `IsForbiddenDependency` | `forbiddenDependencies[]` |
| Per-layer forbidden API catalog | `ForbiddenApiCatalog` | `forbiddenApis[]` |
| `[DllImport]`/`[LibraryImport]` → `PSXRecomp.Core` | `AnalyzePInvokeDeclaration` + `InteropNamespaceRoot` | `interopBoundaryRules[]` (attribute FQN + `allowedLayer`) |
| Generated-path patterns | `IsGeneratedPath` | still hardcoded in AARC; tunable only via `skip_generated_code` / `generated_code` |
| `ContainingType`-chain layer inheritance | `ResolveLayer` | still hardcoded in AARC (`ResolveLayer`), not contract-tunable |
| Per-member forbidden-API de-duplication | `reportedRules` | still hardcoded in AARC, not contract-tunable |
| Nested-type exemption from "must declare" | `AnalyzeNamedType` step 4 | still hardcoded in AARC (`IsNestedInDeclaredLayer`) |
| Severity of each rule | descriptor + `.editorconfig` pins | descriptor + standard `dotnet_diagnostic.*.severity` |

Everything in the top block is data in the generic model; everything in the bottom block remains
analyzer behavior. That split is the reason the baseline's `PSXRecomp.Core` interop rule can move
into JSON while its "nested types inherit their outer type's layer" rule cannot.

---

## 7. Post-baseline drift

`git diff 88f5b6f2c209b0980fd96d241f28dce1f840673a..HEAD -- src/PSXRecomp.Analyzer src/PSXRecomp.Analyzer.Tests`
against PSXRecompStudio `9fbaf4fd` produces an **empty diff**.

| | |
|---|---|
| Pinned baseline | `88f5b6f2c209b0980fd96d241f28dce1f840673a` |
| PSXRecompStudio `main` at time of writing | `9fbaf4fdc5ecc6886eb183f4676e95da1d2e52c8` |
| Changes under `src/PSXRecomp.Analyzer/**` | none |
| Changes under `src/PSXRecomp.Analyzer.Tests/**` | none |

`main` has advanced in other areas of PSXRecompStudio, but the analyzer and its tests are
byte-identical to the pinned baseline. **There is no post-baseline drift to account for.**

If a future PSXRecompStudio change does touch these paths, record it here as a dated row — do not
re-pin §1.
