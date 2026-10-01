# Architecture

This document describes **how** ArchitectureAnalyzer works, and is the source of truth for the
**contract schema**. For **why** it is shaped this way — why compile-time enforcement, why JSON via
`AdditionalFiles`, why namespace-based classification, why the diagnostic set started at three —
see [`design.md`](design.md). For the `.editorconfig` properties that tune *how the analyzer runs*
(as opposed to *what the rules are*), see [`configuration.md`](configuration.md).

## 1. End-to-end pipeline

```
architecture.contract.json          (a) the consuming repo's Architecture Contract
        |  AdditionalFiles
        v
ArchitectureContractAnalyzer        (b) a generic DiagnosticAnalyzer, no rules of its own
        |  Roslyn semantic model      +  .editorconfig operational options (configuration.md)
        v
AARC001 .. AARC010                  (c) diagnostics; Error except AARC006/AARC008/AARC009 (Warning)
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
3. **(c)** With a valid contract, further actions are registered: a syntax-node action over
   `IdentifierName`/`GenericName` for dependency direction (AARC002) and an operation-block
   action for forbidden APIs (AARC003), plus — only when the corresponding contract section is
   present — a symbol action over named types for the declaration rules (AARC004/AARC005/AARC006)
   and a syntax-node action over method declarations for interop boundaries (AARC007). Schema-v2
   strict coverage adds another named-type action for AARC010. A contract that fails to load
   produces AARC001 once per compilation instead, and configuration problems produce
   AARC008/AARC009 at compilation end.
4. **(d)** The violation diagnostics are `DiagnosticSeverity.Error`, so they fail `dotnet build`
   itself rather than only a separate lint step. AARC006 (declaration/namespace drift) and
   AARC008/AARC009 (configuration problems) are `Warning`: they report drift and
   misconfiguration, not a known architecture violation.

## 2. Contract schema

The contract is a single JSON object. `schemaVersion` is optional for backward compatibility
with pre-versioning contracts and is interpreted as **1** when omitted. New contracts should
declare the current `"schemaVersion": 3`; existing v1/v2 and versionless contracts remain supported.
`layers` is required; the other rule sections are optional and activate only the analyses that
depend on them.

Unknown properties inside a **supported schema version** are ignored, which permits additive
metadata without changing rule semantics. A schema change that can affect interpretation must
increment `schemaVersion`; an analyzer that does not understand that version rejects the whole
contract with AARC001 instead of silently guessing.

| Section | Required | Activates |
|---|---|---|
| `schemaVersion` | no (v0.x compatibility) | schema compatibility check; omitted means v1 |
| `layers` | yes | namespace → layer classification (everything else depends on it) |
| `forbiddenDependencies` | no | AARC002 |
| `allowedDependencies` | no, schema v3 only | AARC002 positive allowlist for listed source layers |
| `forbiddenApis` | no | AARC003 |
| `layerDeclaration` | no | AARC004 / AARC005 / AARC006 |
| `interopBoundaryRules` | no | AARC007 |
| `unclassifiedCode` | no, schema v2 only | AARC010 when set to `"error"` |

```jsonc
{
  // RECOMMENDED. Versionless legacy contracts are interpreted as v1 during the 0.x line.
  // v2 added strict architecture coverage; v3 adds positive dependency allowlists.
  "schemaVersion": 3,

  // OPTIONAL in v2. "ignore" preserves legacy behavior; "error" requires every applicable
  // source type to resolve through a namespace root or configured marker attribute (AARC010).
  "unclassifiedCode": "error",

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

  // OPTIONAL in v3. A listed source layer may depend only on the declared target layers.
  // Source layers omitted from this section remain permissive for incremental adoption.
  "allowedDependencies": [
    {
      "from": "Application",
      "to": [ "Domain" ],
      "reason": "Application may depend only on Domain."
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
  ],

  // OPTIONAL. Attribute-based layer declaration. Present => AARC004/AARC005/AARC006 are
  // active; absent => classification stays namespace-only and those rules never register.
  "layerDeclaration": {
    // When true, every non-exempt class must carry a recognized marker attribute (AARC004).
    // Requires a non-empty markerAttributes list, otherwise the contract is invalid.
    "required": true,

    // Attribute FQN -> declared layer. FQNs are matched ordinally against
    // AttributeClass.OriginalDefinition.ToDisplayString(); each FQN may appear once, and
    // "layer" must name a layer declared above.
    "markerAttributes": [
      { "attributeFqn": "MyApp.Architecture.DomainAttribute", "layer": "Domain" },
      { "attributeFqn": "MyApp.Architecture.ApplicationAttribute", "layer": "Application" }
    ],

    // When true, a declared layer that contradicts the namespace-implied layer is reported
    // (AARC006, Warning). Default false.
    "validateNamespaceConsistency": true,

    // OPTIONAL. Types in this namespace (the marker attribute definitions themselves) are
    // exempt from AARC004.
    "markerNamespace": "MyApp.Architecture"
  },

  // OPTIONAL. Methods carrying "attribute" may only be declared inside "allowedLayer"
  // (AARC007). The generic form of "P/Invoke lives in exactly one place".
  "interopBoundaryRules": [
    {
      // Matched ordinally against AttributeClass.OriginalDefinition.ToDisplayString().
      "attribute": "System.Runtime.InteropServices.DllImportAttribute",
      // Must name a layer declared above.
      "allowedLayer": "NativeInterop",
      "reason": "P/Invoke declarations must live in the NativeInterop layer."
    }
  ]
}
```

### Positive dependency allowlists

Schema v3 adds `allowedDependencies` as an opt-in complement to the existing denylist.

Each entry makes exactly one source layer strict:

```json
"allowedDependencies": [
  { "from": "Domain", "to": [ "Shared" ], "reason": "Domain may depend only on Shared." },
  { "from": "Shared", "to": [] }
]
```

For a listed source, cross-layer references are permitted only to layers in `to`. An empty
`to` array means no cross-layer dependency is permitted. Same-layer references are always
allowed and do not need to be listed.

Source layers omitted from `allowedDependencies` remain permissive with respect to the
allowlist, so a project can migrate one layer at a time. Existing `forbiddenDependencies`
continue to apply to every source layer. If a dependency is denied by both mechanisms, an
explicit forbidden edge is evaluated first and supplies its specific reason. Declaring the exact
same edge as both explicitly allowed and forbidden is a contract error.

AARC002 still checks direct source-level symbol references only; no transitive closure is inferred.
The source/target classification uses the same marker-attribute-first, namespace-fallback
semantics as the existing dependency analyzer. Strict classification coverage remains the
separate `unclassifiedCode=error` / AARC010 policy.

The implementation rationale and migration examples are recorded in
[`allowed-dependencies-design.md`](allowed-dependencies-design.md).

The containing type's layer for `interopBoundaryRules` is resolved with the same
attribute-aware rule as AARC002/AARC003: a marker attribute on the type or any enclosing type
wins over the namespace, and unclassified code is never the allowed layer.

### Schema compatibility policy

Schema **v1** is the first explicit public Architecture Contract schema; schema **v2** adds the
optional `unclassifiedCode` coverage policy, and schema **v3** adds positive dependency
allowlists through `allowedDependencies`.

- A contract with `"schemaVersion": 1` is parsed as v1.
- A contract with `"schemaVersion": 2` is parsed as v2.
- A contract with `"schemaVersion": 3` is parsed as v3.
- A versionless contract is parsed as v1 during the 0.x release line so existing consumers do not
  break merely because versioning was introduced.
- `unclassifiedCode` is valid in v2 and later. Supplying it to a v1/versionless contract is
  rejected instead of being silently ignored.
- `allowedDependencies` is valid only in v3 and later. Supplying it to a v1/v2/versionless
  contract is rejected instead of being silently ignored.
- If `schemaVersion` is present it must appear exactly once and be an integer. Duplicate
  `schemaVersion` properties are ambiguous and rejected before either value is interpreted;
  `null`, strings and fractional numbers are invalid rather than treated as versionless.
- An unsupported version is rejected with AARC001. In particular, an older analyzer must never
  silently interpret a future schema version using old semantics.
- Additive metadata may be introduced as unknown properties without changing existing semantics;
  unknown properties are ignored. Any new property whose interpretation changes architecture
  enforcement must ship under a new schema version.
- Exact duplicate namespace roots and duplicate forbidden dependency edges are invalid. One
  namespace root therefore has exactly one owner and one forbidden edge has exactly one
  declaration.
- An interop attribute may appear only once. Repeating it for a different allowed layer is
  rejected as a contradictory rule instead of relying on declaration order.

The compatibility guarantee is intentionally asymmetric: current analyzers keep accepting
versionless/v1/v2/v3 contracts, while future-version contracts fail closed on older analyzers.

### Validation rules

A contract is rejected with AARC001 when any of the following hold. The failure reason is
included verbatim in the diagnostic message.

| Condition | Reported reason (argument `{1}` of AARC001) |
|---|---|
| The `AdditionalFiles` item exists but has no readable content | `file not found` |
| The file is empty or whitespace | `the file is empty` |
| The text is not valid JSON | the raw `System.Text.Json` parse message |
| The root is not a JSON object | `the contract root must be a JSON object` |
| `schemaVersion` appears more than once | `property 'schemaVersion' must not appear more than once` |
| `schemaVersion` is present but is not an integer | `property 'schemaVersion' must be an integer when present` |
| `schemaVersion` is not supported | `unsupported schemaVersion 'N'; supported schemaVersions are 1 through 3` |
| `allowedDependencies` is used before schema v3 | `property 'allowedDependencies' requires schemaVersion 3` |
| `allowedDependencies` appears more than once | `property 'allowedDependencies' must not appear more than once` |
| `allowedDependencies` is not an array | `property 'allowedDependencies' must be a JSON array` |
| An allowed-dependency source is declared twice | `allowed dependency source 'X' is declared more than once` |
| `allowedDependencies[].from`/target names an undeclared layer | `layer 'X' referenced in allowedDependencies is not declared in layers` |
| The same allowed target appears twice for one source | `allowed dependency 'A' -> 'B' is declared more than once` |
| An edge is declared both allowed and forbidden | `dependency 'A' -> 'B' is declared as both allowed and forbidden` |
| `unclassifiedCode` is used with schema v1/versionless | `property 'unclassifiedCode' requires schemaVersion 2` |
| `unclassifiedCode` appears more than once | `property 'unclassifiedCode' must not appear more than once` |
| `unclassifiedCode` is not `"ignore"` or `"error"` | `property 'unclassifiedCode' must be 'ignore' or 'error'` |
| `layers` is missing | `required property 'layers' is missing` |
| Two layers share a name | `layer 'X' is declared more than once in layers` |
| A namespace root is declared more than once | `namespace root 'X' is declared more than once in layers` |
| A forbidden dependency edge is declared more than once | `forbidden dependency 'A' -> 'B' is declared more than once` |
| `forbiddenDependencies[].from`/`.to` names an undeclared layer | `layer 'X' referenced in forbiddenDependencies is not declared in layers` |
| `forbiddenApis[].layer` names an undeclared layer | `layer 'X' referenced in forbiddenApis is not declared in layers` |
| `interopBoundaryRules[].allowedLayer` names an undeclared layer | `layer 'X' referenced in interopBoundaryRules is not declared in layers` |
| The same interop attribute is assigned different allowed layers | `interop attribute 'X' has conflicting allowed layers 'A' and 'B'` |
| `layerDeclaration` is not a JSON object | `property 'layerDeclaration' must be a JSON object` |
| `layerDeclaration.markerAttributes[].layer` names an undeclared layer | `marker attribute 'X' maps to undeclared layer 'Y'` |
| The same `attributeFqn` appears twice in `markerAttributes` | `duplicate marker attribute 'X' in layerDeclaration.markerAttributes` |
| `layerDeclaration.required` is `true` with no marker attributes | `layerDeclaration.required is true but no marker attributes are declared` |
| A `bool` property (`wholeType`, `required`, `validateNamespaceConsistency`) holds a non-boolean | `property 'wholeType' must be a boolean when present`, … |
| A required string property is missing or empty | `required property 'name' is missing`, `property 'type' must be a non-empty string`, … |

`reason` is optional everywhere; when omitted the corresponding diagnostic simply carries an
empty reason string.

### Exactly one contract per compilation

ArchitectureAnalyzer supports **one** contract file per compilation. File-name matching is
case-insensitive and accepts either `/` or `\\` path separators, so every `AdditionalFiles`
item whose basename is `architecture.contract.json` participates in discovery.

If more than one matching file is supplied, the analyzer **does not choose one**. It stops
architecture analysis for that compilation and, when
`dotnet_diagnostic.AARC001.architecture_analyzer.contract_required` is `true` (the default),
reports AARC001 with the matching paths sorted ordinally after normalizing separators to `/`.
When `contract_required=false`, AARC001 is suppressed and the analyzer remains a no-op for the
ambiguous compilation, matching the existing malformed-contract opt-out semantics. This makes
ambiguous configuration deterministic across supported operating systems and prevents a
path-order accident from enforcing the wrong contract. Remove all but one matching
`AdditionalFiles` item to fix the configuration.

Multiple-contract merging remains deliberately unsupported. Union/override/per-project merge
semantics require an explicit design rather than an implicit path-order rule.

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

## 4. Unclassified code and strict coverage

Legacy behavior remains permissive: a v1/versionless contract, or a v2 contract with
`"unclassifiedCode": "ignore"` (the default), allows a type whose namespace matches no root to
remain outside layer enforcement.

Schema v2 can make that boundary explicit:

```json
{
  "schemaVersion": 2,
  "unclassifiedCode": "error",
  "layers": [
    { "name": "Domain", "namespaceRoots": [ "MyApp.Domain" ] }
  ]
}
```

With `"error"`, every applicable source named type must resolve to a layer. Resolution uses the
same attribute-aware rule as the other analyzer paths: a recognized marker attribute on the type
or an enclosing type wins; otherwise namespace-root matching is used. If neither produces a layer,
AARC010 is reported on the type declaration.

Coverage deliberately follows existing operational exclusions. A file with
`architecture_analyzer.enabled = false` is excluded, `rule.AARC010.enabled = false` can stage the
rule for a subtree, and generated-path files are skipped by default but participate when
`generated_code = include` / `skip_generated_code = false` makes them eligible. Types inside
`layerDeclaration.markerNamespace` are exempt because they normally define the marker attributes
themselves.

AARC010 is not layered on top of a more specific declaration error: a class already receiving
AARC004 for a required missing marker, or a type receiving AARC005 for conflicting marker layers,
does not receive an additional coverage-gap diagnostic for the same cause.

This strict mode is the recommended way to prevent a newly introduced top-level namespace from
silently escaping an otherwise complete architecture contract. Projects adopting the analyzer
incrementally can remain on v1 or use `"ignore"` until their namespace coverage is complete.

## 5. What each diagnostic inspects

| Diagnostic | Roslyn hook | What it looks at |
|---|---|---|
| AARC001 | `RegisterCompilationStartAction` + `RegisterCompilationEndAction` | The contract file itself; reported once per compilation with `Location.None` |
| AARC002 | `RegisterSyntaxNodeAction(IdentifierName, GenericName)` | The symbol each name binds to, its declaring type, and the enclosing type declaration; attribute arguments are skipped |
| AARC003 | `RegisterOperationBlockAction` | Every `IInvocationOperation`, `IObjectCreationOperation` and `IMemberReferenceOperation` in the block |
| AARC004 / AARC005 / AARC006 | `RegisterSymbolAction(SymbolKind.NamedType)`, registered only when the contract has a `layerDeclaration` | The class's own attributes, its containing-type chain and its namespace |
| AARC007 | `RegisterSyntaxNodeAction(MethodDeclaration)`, registered only when `interopBoundaryRules` is non-empty | Each method's attributes and the layer of its containing type |
| AARC008 / AARC009 | `RegisterCompilationEndAction` | Invalid or unknown operational options; reported once per offending key, `Location.None` |
| AARC010 | `RegisterSymbolAction(SymbolKind.NamedType)`, only when `unclassifiedCode=error` | Applicable source types after marker-attribute + namespace classification and operational exclusions |

AARC002 de-duplicates on `"{sourceType}->{targetType}"` for the whole compilation, so a Domain
type that touches the same Application type in twenty places produces one error, not twenty.
AARC003 de-duplicates per rule per analyzed member, so a single chained expression that matches
one `wholeType` rule several times reports once. AARC007 de-duplicates on
`"{method}{attribute}"` so a partial method whose parts are merged into one symbol reports
once, at the declaration part that carries the matched attribute.

AARC004 is reported only when `layerDeclaration.required` is `true` **and** the operational
`require_layer_declaration` option is left at its default; nested types are exempt whenever an
enclosing type resolves to a layer, as are types inside `markerNamespace`. AARC005 looks at the
type's own attributes only. AARC006 needs `validateNamespaceConsistency` in the contract or the
`validate_namespace_layer` option, and fires only for a type that declares exactly one layer.
AARC004–AARC007 always skip generated paths, regardless of the generated-code options. AARC010
instead follows the AARC002/AARC003 generated-path operational policy so strict coverage can be
staged consistently — see [`configuration.md` §3.2](configuration.md#32-scope-precisely).

## 6. Consuming the analyzer

A consumer needs one item group and one JSON file. From NuGet — the package ships its assembly
under `analyzers/dotnet/cs`, so a plain `PackageReference` loads it as an analyzer:

```xml
<ItemGroup>
  <PackageReference Include="loach.ArchitectureAnalyzer" Version="0.0.1" PrivateAssets="all" />
  <AdditionalFiles Include="architecture.contract.json" />
</ItemGroup>
```

Or from source, referencing the analyzer **as an analyzer** — `OutputItemType="Analyzer"` with
`ReferenceOutputAssembly="false"` — so its assembly never becomes a runtime dependency of the
consuming code:

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
`dotnet build` enforces the contract. Severity is tuned through the standard `.editorconfig`
`dotnet_diagnostic.<ID>.severity` keys, and operational behavior through the
`architecture_analyzer.*` properties — see [`configuration.md`](configuration.md) for both, and
[`diagnostics.md`](diagnostics.md) for per-rule suppression guidance.

## 7. Project layout

| Path | Role |
|---|---|
| `src/ArchitectureAnalyzer/ArchitectureContractAnalyzer.cs` | The `DiagnosticAnalyzer`: contract discovery, dependency direction, forbidden APIs, declaration rules, interop boundaries |
| `src/ArchitectureAnalyzer/Diagnostics/ArchitectureDiagnostics.cs` | The `DiagnosticDescriptor`s for AARC001–AARC010 |
| `src/ArchitectureAnalyzer/Configuration/ConfigReader.cs` | Operational option keys, parsing and AARC008 reporting — the source of truth behind [`configuration.md`](configuration.md) |
| `src/ArchitectureAnalyzer/Configuration/OperationalConfig.cs` | The typed, immutable resolved option set with its hardcoded defaults |
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
