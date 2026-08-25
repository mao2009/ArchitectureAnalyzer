# Architecture

This document describes **how** ArchitectureAnalyzer works. For **why** it is shaped this way —
why compile-time enforcement, why JSON via `AdditionalFiles`, why namespace-based classification,
why only three diagnostics in v0.1 — see [`design.md`](design.md).

## 1. End-to-end pipeline

```
architecture.contract.json          (a) the consuming repo's Architecture Contract
        |  AdditionalFiles
        v
ArchitectureContractAnalyzer        (b) a generic DiagnosticAnalyzer, no rules of its own
        |  Roslyn semantic model
        v
AARC001 / AARC002 / AARC003         (c) diagnostics, severity Error
        |
        v
dotnet build fails                  (d) locally, in the IDE, and in CI - the same command
```

1. **(a)** The consuming project ships `architecture.contract.json` next to its code and declares
   it as an `AdditionalFiles` item. This file is the single source of truth for the project's
   layering. Nothing about it is compiled into the analyzer.
2. **(b)** On `RegisterCompilationStartAction`, the analyzer looks through
   `AnalyzerOptions.AdditionalFiles` for a file whose *file name* (case-insensitive, directory
   ignored) is `architecture.contract.json`, parses and validates it once, and caches the result
   for the whole compilation. If the file is absent, the analyzer registers nothing further and
   is a complete no-op — enforcement is opt-in per project.
3. **(c)** With a valid contract, two further actions are registered: a syntax-node action over
   `IdentifierName`/`GenericName` for dependency direction (AARC002) and an operation-block
   action for forbidden APIs (AARC003). A contract that fails to load produces AARC001 once per
   compilation instead.
4. **(d)** All three diagnostics are `DiagnosticSeverity.Error`, so they fail `dotnet build`
   itself rather than only a separate lint step.

## 2. Contract schema

The contract is a single JSON object with three optional-but-usually-present sections. Unknown
properties anywhere in the document are **ignored**, so a contract written for a newer version of
this analyzer stays loadable by an older one.

```jsonc
{
  // REQUIRED. The layers this project declares. Names are matched case-sensitively
  // everywhere else in the document, and must be unique.
  "layers": [
    {
      "name": "Domain",
      // Namespace prefixes owned by this layer. A type belongs to the layer when its
      // containing namespace equals a root exactly, or starts with root + ".".
      "namespaceRoots": [ "MyApp.Domain" ]
    },
    {
      "name": "Application",
      "namespaceRoots": [ "MyApp.Application", "MyApp.UseCases" ]
    }
  ],

  // OPTIONAL. Directed edges that must not exist. "from" and "to" must both name a
  // layer declared above, otherwise the contract is invalid (AARC001).
  "forbiddenDependencies": [
    {
      "from": "Domain",
      "to": "Application",
      // Surfaced verbatim as the last argument of the AARC002 message.
      "reason": "Domain must not depend on the outer Application layer."
    }
  ],

  // OPTIONAL. APIs that code inside a given layer must not use. "layer" must name a
  // layer declared above, otherwise the contract is invalid (AARC001).
  "forbiddenApis": [
    {
      "layer": "Domain",
      // Fully qualified declaring type, compared with an ordinal string match against
      // ISymbol.ContainingType.OriginalDefinition.ToDisplayString().
      "type": "System.Console",
      // No "member" and no "wholeType": every member of the type is forbidden.
      "reason": "Console I/O must be abstracted behind an Infrastructure adapter."
    },
    {
      "layer": "Domain",
      "type": "System.Random",
      // wholeType also forbids the type's own constructors (new Random()).
      "wholeType": true,
      "reason": "Non-deterministic randomness is forbidden in the Domain layer."
    },
    {
      "layer": "Domain",
      "type": "System.DateTime",
      // With "member", only that one member is forbidden; DateTime.UnixEpoch stays legal.
      "member": "Now",
      "reason": "Non-deterministic time source is forbidden in the Domain layer."
    }
  ]
}
```

### Validation rules

A contract is rejected with AARC001 when any of the following hold. The failure reason is
included verbatim in the diagnostic message.

| Condition | Reported reason (argument `{1}` of AARC001) |
|---|---|
| The `AdditionalFiles` item exists but has no readable content | `file not found` |
| The file is empty or whitespace | `the file is empty` |
| The text is not valid JSON | the raw `System.Text.Json` parse message |
| The root is not a JSON object | `the contract root must be a JSON object` |
| `layers` is missing | `required property 'layers' is missing` |
| Two layers share a name | `layer 'X' is declared more than once in layers` |
| `forbiddenDependencies[].from`/`.to` names an undeclared layer | `layer 'X' referenced in forbiddenDependencies is not declared in layers` |
| `forbiddenApis[].layer` names an undeclared layer | `layer 'X' referenced in forbiddenApis is not declared in layers` |
| A required string property is missing or empty | `required property 'name' is missing`, `property 'type' must be a non-empty string`, … |

`reason` is optional everywhere; when omitted the corresponding diagnostic simply carries an
empty reason string.

### Known limitation: exactly one contract per compilation

v0.1 supports **one** contract file. If several `AdditionalFiles` items happen to be named
`architecture.contract.json`, the analyzer takes the first by ordinal path sort and does not
report an error. Merging multiple contract files is deliberately not implemented — no real
consumer has asked for it yet, and a merge semantics (union? override? per-project scoping?)
should be designed against a concrete need rather than guessed at.

## 3. Namespace classification and the longest-prefix rule

A type's layer is resolved purely from `ITypeSymbol.ContainingNamespace` — symbol-based, not a
text search. A namespace `N` belongs to root `R` when `N == R` or `N` starts with `R + "."`, so
`MyApp.Domain` and `MyApp.Domain.Orders.Pricing` both match the root `MyApp.Domain`, while
`MyApp.DomainServices` does not.

When a namespace matches roots declared by **two different layers**, the **longest root wins**.
Given:

```json
"layers": [
  { "name": "Outer", "namespaceRoots": [ "MyApp" ] },
  { "name": "Inner", "namespaceRoots": [ "MyApp.Domain.Orders" ] }
]
```

| Namespace | Resolved layer |
|---|---|
| `MyApp.Domain` | `Outer` (only `MyApp` matches) |
| `MyApp.Domain.Orders` | `Inner` (longer root wins over `MyApp`) |
| `MyApp.Domain.Orders.Pricing` | `Inner` |
| `MyAppOther` | *(unclassified)* |

Ties between equal-length roots are broken by ordinal comparison of the root string, purely so
that the result is deterministic; declaring the same root under two layers is a contract smell,
not a supported feature.

## 4. Unclassified code

A type whose namespace matches **no** declared root has no layer, and the analyzer therefore has
nothing to say about it: it is neither a valid source nor a valid target for AARC002, and
AARC003 never applies inside it. This is intentional, not a gap to be fixed — see
[`design.md` §5](design.md#5-why-namespace-based-layer-classification-not-attributes-for-v01)
for why v0.1 classifies by namespace only, and
[`design.md` §9](design.md#9-what-this-analyzer-guarantees-and-what-it-does-not) for the exact
boundary of what enforcement does and does not guarantee.

The practical consequence: **a contract only governs the namespaces it lists.** Adding a new
top-level namespace to a project silently adds unenforced code. Keeping `namespaceRoots` broad
(one root per top-level layer namespace rather than per feature folder) is the cheapest way to
avoid that.

Generated code is also skipped: the analyzer sets
`ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None)` and additionally ignores paths
ending in `.g.cs`, `.g.i.cs`, `.designer.cs` or `.generated.cs`, and anything under an `obj/` or
`bin/` directory.

## 5. What each diagnostic inspects

| Diagnostic | Roslyn hook | What it looks at |
|---|---|---|
| AARC001 | `RegisterCompilationStartAction` + `RegisterCompilationEndAction` | The contract file itself; reported once per compilation with `Location.None` |
| AARC002 | `RegisterSyntaxNodeAction(IdentifierName, GenericName)` | The symbol each name binds to, its declaring type, and the enclosing type declaration; attribute arguments are skipped |
| AARC003 | `RegisterOperationBlockAction` | Every `IInvocationOperation`, `IObjectCreationOperation` and `IMemberReferenceOperation` in the block |

AARC002 de-duplicates on `"{sourceType}->{targetType}"` for the whole compilation, so a Domain
type that touches the same Application type in twenty places produces one error, not twenty.
AARC003 de-duplicates per rule per analyzed member, so a single chained expression that matches
one `wholeType` rule several times reports once.

## 6. Consuming the analyzer

A consumer needs two item groups and one JSON file. Reference the analyzer **as an analyzer** —
`OutputItemType="Analyzer"` with `ReferenceOutputAssembly="false"` — so its assembly never
becomes a runtime dependency of the consuming code:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\..\src\ArchitectureAnalyzer\ArchitectureAnalyzer.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
  <AdditionalFiles Include="architecture.contract.json" />
</ItemGroup>
```

(Adjust the relative path for your own layout. A working end-to-end example lives in
[`../tests/GateVerification/SampleConsumer`](../tests/GateVerification/SampleConsumer).)

Because the diagnostics are `Error` and `EnabledByDefault`, no further wiring is needed: the next
`dotnet build` enforces the contract. Per-diagnostic severity can still be tuned through the
standard `.editorconfig` keys — see [`diagnostics.md`](diagnostics.md).

## 7. Project layout

| Path | Role |
|---|---|
| `src/ArchitectureAnalyzer/ArchitectureContractAnalyzer.cs` | The `DiagnosticAnalyzer`: contract discovery, dependency direction, forbidden APIs |
| `src/ArchitectureAnalyzer/Diagnostics/ArchitectureDiagnostics.cs` | The three `DiagnosticDescriptor`s |
| `src/ArchitectureAnalyzer/Contract/ArchitectureContract.cs` | Immutable contract model and layer resolution |
| `src/ArchitectureAnalyzer/Contract/ArchitectureContractLoader.cs` | JSON parsing and schema validation — deliberately free of Roslyn types so it is unit-testable, and reusable by future non-compiler tooling |
| `src/ArchitectureAnalyzer.Tests` | `Microsoft.CodeAnalysis.Testing`-based analyzer tests plus direct loader unit tests |
| `tests/GateVerification` | A real `dotnet build` proof that a violation fails a genuine build |

The analyzer targets `netstandard2.0` (the compatible surface for a Roslyn component) with
`EnforceExtendedAnalyzerRules` on. `System.Text.Json` is referenced with `PrivateAssets="all"`
so it never flows to consumers; at analysis time the compiler host resolves it from the shared
framework.

`tests/GateVerification/SampleConsumer` is intentionally **not** a member of
`ArchitectureAnalyzer.sln`, because `verify-gate.sh` deliberately drives it into a failing build.
A solution-wide `dotnet build` must never be affected by that.
