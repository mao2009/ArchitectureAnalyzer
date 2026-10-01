# Diagnostics

Every diagnostic in this analyzer belongs to the `Architecture` category, ships with
`EnabledByDefault = true`, and carries no project-specific knowledge of its own — the layer
names, namespace roots, API rules and interop attributes it names in its messages all come from
the consuming project's `architecture.contract.json`. Most diagnostics are `Error`; AARC006,
AARC008 and AARC009 are `Warning` because they describe drift or misconfiguration rather than a
known violation. A configuration warning never means enforcement was relaxed: see the
[fail-closed decision table](#fail-closed-configuration-policy).

IDs are never reused or renumbered once shipped (see [`design.md` §7](design.md#7-diagnostic-id-namespace));
a retired rule is marked obsolete here rather than having its ID reassigned.

See also: [`configuration.md`](configuration.md) for the `.editorconfig` properties referenced by
the "Operational override" sections below, [`architecture.md` §2](architecture.md#2-contract-schema)
for the contract sections each rule depends on, and
[`compatibility/psxrecomp-analyzer-baseline.md`](compatibility/psxrecomp-analyzer-baseline.md) for
the PSXRecomp.Analyzer rule-to-AARC mapping. Compatibility and migration detail lives under
`docs/compatibility/` only; this file documents the rules as they are, independently of any
consumer.

| ID | Title | Severity | Enabled by default |
|---|---|---|---|
| [AARC001](#aarc001) | Architecture contract could not be loaded | Error | Yes |
| [AARC002](#aarc002) | Forbidden architecture dependency direction | Error | Yes |
| [AARC003](#aarc003) | Forbidden API usage in architecture layer | Error | Yes |
| [AARC004](#aarc004) | Missing required architecture layer declaration | Error | Yes |
| [AARC005](#aarc005) | Multiple distinct architecture layer declarations | Error | Yes |
| [AARC006](#aarc006) | Architecture layer declaration contradicts namespace layer | Warning | Yes |
| [AARC007](#aarc007) | Interop declaration outside allowed layer | Error | Yes |
| [AARC008](#aarc008) | Invalid architecture analyzer configuration value | Warning | Yes |
| [AARC009](#aarc009) | Unknown architecture analyzer configuration property | Warning | Yes |
| [AARC010](#aarc010) | Type is not assigned to an architecture layer | Error | Yes |
| [AARC011](#aarc011) | Declared architecture dependency graph contains a cycle | Error | Yes |

---

## AARC001

**Architecture contract could not be loaded**

| | |
|---|---|
| **ID** | `AARC001` |
| **Title** | Architecture contract could not be loaded |
| **Message format** | `Architecture contract '{0}' could not be loaded: {1}` |
| **Category** | `Architecture` |
| **Severity** | `Error` |
| **Enabled by default** | Yes |

Argument `{0}` is the contract file name; `{1}` is the specific reason — `file not found`, the
raw `System.Text.Json` parse error, a schema-validation message such as
`layer 'Application' referenced in forbiddenDependencies is not declared in layers`, or an
ambiguous-discovery message listing multiple matching `architecture.contract.json` files.

### Description

Raised when a project declares `architecture.contract.json` in `AdditionalFiles` but discovery or
loading is unsafe: more than one matching contract is supplied, the selected file cannot be read,
the JSON is invalid, or the contract is internally inconsistent. When discovery is ambiguous the
analyzer reports the matching paths and enforces **none** of them; it never silently chooses one.
This keeps a configuration mistake from applying an unintended architecture policy.

A project that declares **no** contract file at all is not an error: the analyzer is opt-in and
does nothing at all in that case. Likewise, setting
`dotnet_diagnostic.AARC001.architecture_analyzer.contract_required = false` suppresses AARC001 for
both malformed/unreadable contracts and ambiguous duplicate-contract discovery; in either case no
contract is enforced for that compilation. The diagnostic is otherwise reported once per
compilation, without a source location, because the failure is a property of the compilation
rather than of any one line of code.

### Minimal triggering example

`architecture.contract.json`:

```json
{
  "layers": [
    { "name": "Domain", "namespaceRoots": [ "MyApp.Domain" ] }
  ],
  "forbiddenDependencies": [
    { "from": "Domain", "to": "Application", "reason": "..." }
  ]
}
```

`Application` is used as an edge endpoint but never declared under `layers`, so the build fails
with:

```
error AARC001: Architecture contract 'architecture.contract.json' could not be loaded:
layer 'Application' referenced in forbiddenDependencies is not declared in layers
```

### Suppressing it

There is rarely a good reason to suppress this one — a suppressed AARC001 means the whole
contract is silently not being enforced. If you must:

```csharp
#pragma warning disable AARC001
#pragma warning restore AARC001
```

`#pragma` is awkward here because the diagnostic has no source location, so `.editorconfig` is
the practical mechanism:

```ini
[*.cs]
dotnet_diagnostic.AARC001.severity = none
```

---

## AARC002

**Forbidden architecture dependency direction**

| | |
|---|---|
| **ID** | `AARC002` |
| **Title** | Forbidden architecture dependency direction |
| **Message format** | `'{0}' ({1}) must not depend on '{2}' ({3}): {4}` |
| **Category** | `Architecture` |
| **Severity** | `Error` |
| **Enabled by default** | Yes |

Arguments are the source type display name, its layer, the target type display name, its layer,
and the effective rationale: either the matching `forbiddenDependencies` reason or the
schema-v3 `allowedDependencies` rule/fallback that rejected an unlisted target.

### Description

Raised when a type in one resolved layer directly references a type in another resolved layer
and the dependency is denied by the contract. A dependency is denied either because the exact
`from` → `to` edge is listed under `forbiddenDependencies`, or because schema-v3
`allowedDependencies` governs the source layer and omits the target layer.

The check is directional. Source layers omitted from `allowedDependencies` stay permissive for
incremental adoption, while an entry with `to: []` rejects every cross-layer dependency from
that source. Same-layer references are always allowed. Explicit forbidden edges are evaluated
before the allowlist so their specific reason wins when both mechanisms deny an observed edge.
Each distinct source-type → target-type pair is reported once per compilation rather than once
per reference.

### Minimal triggering example

`architecture.contract.json`:

```json
{
  "layers": [
    { "name": "Domain", "namespaceRoots": [ "MyApp.Domain" ] },
    { "name": "Application", "namespaceRoots": [ "MyApp.Application" ] }
  ],
  "forbiddenDependencies": [
    { "from": "Domain", "to": "Application", "reason": "Domain must not depend on the outer Application layer." }
  ]
}
```

```csharp
namespace MyApp.Domain;

public sealed class Order
{
    // AARC002: 'MyApp.Domain.Order' (Domain) must not depend on
    // 'MyApp.Application.OrderService' (Application): Domain must not depend on the outer
    // Application layer.
    public MyApp.Application.OrderService Service { get; set; }
}
```

### Positive allowlist example

```json
{
  "schemaVersion": 3,
  "layers": [
    { "name": "Domain", "namespaceRoots": [ "MyApp.Domain" ] },
    { "name": "Application", "namespaceRoots": [ "MyApp.Application" ] },
    { "name": "Shared", "namespaceRoots": [ "MyApp.Shared" ] }
  ],
  "allowedDependencies": [
    { "from": "Domain", "to": [ "Shared" ], "reason": "Domain may depend only on Shared." }
  ]
}
```

Here `Domain -> Shared` is allowed, `Domain -> Application` reports AARC002, and source layers
without an allowlist entry remain permissive unless an explicit forbidden edge applies.

### Suppressing it

For a genuinely justified single exception, suppress at the narrowest scope and leave the
justification next to it:

```csharp
#pragma warning disable AARC002 // Justification: temporary shim, tracked by #123.
    public MyApp.Application.OrderService Service { get; set; }
#pragma warning restore AARC002
```

To relax or disable the rule for a directory or the whole project:

```ini
[*.cs]
dotnet_diagnostic.AARC002.severity = warning   # or: none
```

Prefer changing the contract over suppressing the diagnostic. A suppression hides one violation;
editing the contract states the architecture you actually intend, in a file that gets reviewed.

---

## AARC003

**Forbidden API usage in architecture layer**

| | |
|---|---|
| **ID** | `AARC003` |
| **Title** | Forbidden API usage in architecture layer |
| **Message format** | `'{0}' is forbidden in the {1} layer: {2}` |
| **Category** | `Architecture` |
| **Severity** | `Error` |
| **Enabled by default** | Yes |

Arguments are a short display string for the API, the layer name, and the `reason` string from
the matching `forbiddenApis` entry.

### Description

Raised when code inside a declared layer invokes, constructs or references a member matched by
one of that layer's `forbiddenApis` entries. It is how a contract expresses rules such as "the
Domain layer must stay deterministic and free of I/O" without listing every offending call site
by hand. Matching is symbol-based: the referenced member's declaring type is compared against the
rule's fully qualified `type`, so aliases, `using static` and fully qualified spellings are all
caught identically.

### Minimal triggering example

`architecture.contract.json`:

```json
{
  "layers": [
    { "name": "Domain", "namespaceRoots": [ "MyApp.Domain" ] }
  ],
  "forbiddenApis": [
    { "layer": "Domain", "type": "System.Console", "reason": "Console I/O must be abstracted behind an Infrastructure adapter." }
  ]
}
```

```csharp
namespace MyApp.Domain;

public sealed class Order
{
    public void Dump()
    {
        // AARC003: 'Console.WriteLine' is forbidden in the Domain layer: Console I/O must be
        // abstracted behind an Infrastructure adapter.
        System.Console.WriteLine("total");
    }
}
```

### Suppressing it

```csharp
#pragma warning disable AARC003 // Justification: diagnostic-only bootstrap path, see ADR-014.
        System.Console.WriteLine("total");
#pragma warning restore AARC003
```

or, per project/directory:

```ini
[*.cs]
dotnet_diagnostic.AARC003.severity = none
```

As with AARC002, narrowing the rule in the contract (for example by adding a `member` so only
one member is forbidden) is usually better than suppressing the diagnostic at a call site.

---

## AARC004

**Missing required architecture layer declaration**

| | |
|---|---|
| **ID** | `AARC004` |
| **Title** | Missing required architecture layer declaration |
| **Message format** | `Type '{0}' must declare an architecture layer via one of the marker attributes configured in layerDeclaration` |
| **Category** | `Architecture` |
| **Severity** | `Error` |
| **Enabled by default** | Yes |

Argument `{0}` is the type's display name.

### Description

Raised when the contract's `layerDeclaration.required` is `true` and a class carries no marker
attribute that maps it to a declared layer. The marker attributes are configured per type:

```json
{
  "layerDeclaration": {
    "required": true,
    "markerAttributes": [
      { "attributeFqn": "MyApp.Architecture.PresentationAttribute", "layer": "Presentation" },
      { "attributeFqn": "MyApp.Architecture.DomainAttribute", "layer": "Domain" }
    ],
    "markerNamespace": "MyApp.Architecture"
  }
}
```

A nested type is exempt whenever an enclosing type resolves to a layer — by marker attribute or by
namespace — since it then belongs to that layer by definition. Types in the `markerNamespace`
itself and types in generated files are exempt too.

### Operational override

AARC004 requires **both** `layerDeclaration.required: true` in the contract and the operational
toggle below, so a namespace-based project can drop the declaration requirement without editing
the contract. The toggle defaults to `true`, meaning it never relaxes anything on its own.

```ini
[*.cs]
dotnet_diagnostic.AARC002.architecture_analyzer.require_layer_declaration = false
```

Setting it to `false` means clean namespace layering replaces attribute declarations as the
source of truth: unclassified types fall back to the `Unclassified` layer and are then subject
to the namespace-based dependency and API rules (AARC002/AARC003).

### Suppressing it

The intended fix is to add the appropriate marker attribute to the type. To exempt a single type
from the declarative mode entirely:

```csharp
#pragma warning disable AARC004 // Justification: bootstrap type in the marker namespace.
    public sealed class Registry { }
#pragma warning restore AARC004
```

---

## AARC005

**Multiple distinct architecture layer declarations**

| | |
|---|---|
| **ID** | `AARC005` |
| **Title** | Multiple distinct architecture layer declarations |
| **Message format** | `Type '{0}' declares more than one architecture layer: {1}` |
| **Category** | `Architecture` |
| **Severity** | `Error` |
| **Enabled by default** | Yes |

Argument `{0}` is the type's display name and `{1}` is the comma-separated list of distinct
layers its marker attributes map to.

### Description

Raised when a type's **own** marker attributes map to more than one distinct layer. The
declaration is ambiguous and must be reduced to a single layer; `{1}` lists the competing layers
in contract order so the fix is visible at a glance. Attributes on enclosing types are not
considered: a nested type that declares its own single layer is unambiguous, even when the
container declares another one.

### Suppressing it

Resolve the ambiguity in the source. If a genuinely legacy type is unfortunately annotated, a
narrow suppression is possible:

```ini
[*.cs]
dotnet_diagnostic.AARC005.severity = warning   # or: none
```

Prefer editing the attributes over suppressing: a suppressed AARC005 silently loses the
declaration guarantee for that type.

---

## AARC006

**Architecture layer declaration contradicts namespace layer**

| | |
|---|---|
| **ID** | `AARC006` |
| **Title** | Architecture layer declaration contradicts namespace layer |
| **Message format** | `Type '{0}' declares layer '{1}' but its namespace '{2}' implies layer '{3}'` |
| **Category** | `Architecture` |
| **Severity** | `Warning` |
| **Enabled by default** | Yes |

Arguments are the type's display name, the layer it declares, its namespace, and the layer that
namespace implies.

### Description

Raised when a type's declared (attribute) layer differs from the layer its namespace implies and
the difference is checked. Attribute overrides are intentional — the marker is the stronger
signal — so this diagnostic is informational (`Warning`) rather than blocking. Only one case is
reported per type: when the type declares exactly one layer (that is, AARC004/AARC005 are not
already in play).

### Operational override

AARC006 is checked when either the contract entry sets `validateNamespaceConsistency: true`, or
the operational toggle is enabled. Default is `false`:

```ini
[*.cs]
dotnet_diagnostic.AARC002.architecture_analyzer.validate_namespace_layer = true
```

Enabling it turns namespace/declaration drift into a build warning for every type in the
compilation.

### Suppressing it

```ini
[*.cs]
dotnet_diagnostic.AARC006.severity = none
```

---

## AARC007

**Interop declaration outside allowed layer**

| | |
|---|---|
| **ID** | `AARC007` |
| **Title** | Interop declaration outside allowed layer |
| **Message format** | `Interop declaration '{0}' must be declared inside the '{1}' layer: {2}` |
| **Category** | `Architecture` |
| **Severity** | `Error` |
| **Enabled by default** | Yes |

Arguments are the method's display name, the allowed layer name, and the `reason` string from the
matching `interopBoundaryRules` entry.

### Description

Raised when a method carrying an attribute listed under `interopBoundaryRules` is declared
outside the layer the rule allows. The canonical use case is P/Invoke and
LibraryImport declarations staying in a single `NativeInterop` layer:

```json
{
  "interopBoundaryRules": [
    { "attribute": "System.Runtime.InteropServices.DllImportAttribute", "allowedLayer": "NativeInterop", "reason": "P/Invoke declarations must live in the NativeInterop layer." }
  ]
}
```

The classification is attribute-aware: a type whose marker attribute maps it to the allowed layer
satisfies the rule even when its namespace implies a different layer. Matching is symbol-based, so
aliases and fully-qualified attribute spellings are caught identically.

### Suppressing it

Move the declaration into the allowed layer — the rule exists so interop surfaces are
discoverable in one place. For a documented exception:

```ini
[*.cs]
dotnet_diagnostic.AARC007.severity = none
```

---

## AARC008

**Invalid architecture analyzer configuration value**

| | |
|---|---|
| **ID** | `AARC008` |
| **Title** | Invalid architecture analyzer configuration value |
| **Message format** | `The value '{1}' for property '{0}' is not valid; the fail-closed fallback is applied` |
| **Category** | `Architecture` |
| **Severity** | `Warning` |
| **Enabled by default** | Yes |

Arguments are the property name and the offending value.

### Description

Raised when an `architecture_analyzer.*` operational property in `.editorconfig` or
`.globalconfig` has a value that cannot be parsed (for example `require_layer_declaration = yes`
instead of `true`/`false`). The value is ignored and the **fail-closed fallback** from the table
below applies, so a typo can never switch a check off. Reported once per invalid property per
compilation, without a source location.

The diagnostic's own severity is deliberately not the safety mechanism — the fallback value is.
Downgrading or suppressing AARC008 therefore never relaxes enforcement.

### Suppressing it

Fix the typo in the configuration file. A suppressed AARC008 hides a misconfiguration that will
re-appear every build until corrected.

---

## AARC009

**Unknown architecture analyzer configuration property**

| | |
|---|---|
| **ID** | `AARC009` |
| **Title** | Unknown architecture analyzer configuration property |
| **Message format** | `'{0}' is not a recognized architecture_analyzer property; the value '{1}' has no effect` |
| **Category** | `Architecture` |
| **Severity** | `Warning` |
| **Enabled by default** | Yes |

Arguments are the property name and the configured value.

### Description

Raised when an `.editorconfig` / `.globalconfig` entry contains `.architecture_analyzer.` but does
not match any supported property — a typo in the property name
(`require_layer_declration`), in the diagnostic-id segment (`AARC0002`), or a rule toggle for a
rule that has none (`rule.AARC004.enabled`). Such an entry is invisible to a key lookup, so
without this diagnostic the setting the author intended would silently never apply. Reported once
per unknown key per compilation, without a source location.

The property name is echoed in lower case because Roslyn stores `.editorconfig` keys lower-cased.

Detection uses `AnalyzerConfigOptions.Keys`, available since Roslyn 4.4. On an older host the base
implementation throws and unknown-key detection is skipped; the fail-closed value handling for
recognized properties is unaffected.

### Suppressing it

Fix the property name. `dotnet_diagnostic.AARC009.severity = none` silences it for projects that
deliberately keep foreign `architecture_analyzer.*` entries in a shared `.editorconfig`.

---

## AARC010

**Type is not assigned to an architecture layer**

| | |
|---|---|
| **ID** | `AARC010` |
| **Title** | Type is not assigned to an architecture layer |
| **Message format** | `Type '{0}' in namespace '{1}' could not be assigned to any architecture layer` |
| **Category** | `Architecture` |
| **Severity** | `Error` |
| **Enabled by default** | Yes |

Arguments are the source type display name and its containing namespace (or
`<global namespace>`).

### Description

Raised only for schema-v2 contracts that opt into strict coverage with
`"unclassifiedCode": "error"`. The analyzer first applies the same classification semantics used
elsewhere: a recognized marker attribute on the type or an enclosing type wins, otherwise
`layers[].namespaceRoots` are matched. AARC010 is emitted only when neither route assigns the
type to a declared layer.

The rule applies to source classes, structs, interfaces, enums and delegates. It follows
operational exclusions: `architecture_analyzer.enabled = false` excludes a file,
`rule.AARC010.enabled = false` can stage rollout, and generated-path files are skipped by default
but can be included through the existing generated-code options. Types inside
`layerDeclaration.markerNamespace` are exempt.

AARC010 intentionally does not duplicate a more specific classification failure. A class already
reported by AARC004 for a required missing declaration, or a type with conflicting marker layers
reported by AARC005, does not receive a second coverage-gap diagnostic for the same root cause.

### Minimal triggering example

`architecture.contract.json`:

```json
{
  "schemaVersion": 2,
  "unclassifiedCode": "error",
  "layers": [
    { "name": "Domain", "namespaceRoots": [ "MyApp.Domain" ] }
  ]
}
```

```csharp
namespace MyApp.Tools;

public sealed class Helper
{
}
```

`Helper` is outside every configured namespace root and carries no recognized layer marker, so
the declaration fails with AARC010.

### Suppressing or staging it

Prefer fixing the coverage gap by widening the appropriate namespace root, moving the type, or
applying a configured marker attribute. For staged adoption, use the operational rule toggle on a
narrow `.editorconfig` scope:

```ini
[src/Legacy/**.cs]
dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC010.enabled = false
```

Standard severity control also works:

```ini
[*.cs]
dotnet_diagnostic.AARC010.severity = warning
```

---

## AARC011

**Declared architecture dependency graph contains a cycle**

| | |
|---|---|
| **ID** | `AARC011` |
| **Title** | Declared architecture dependency graph contains a cycle |
| **Message format** | `Declared architecture dependency graph contains a cycle: {0}` |
| **Category** | `Architecture` |
| **Severity** | `Error` |
| **Enabled by default** | Yes |

Argument `{0}` is a canonical cycle path such as
`Application -> Domain -> Shared -> Application`.

### Description

Raised only for schema-v4 contracts that opt into DAG enforcement:

```json
"dependencyGraph": {
  "requireAcyclic": true
}
```

The graph contains the explicit positive edges from `allowedDependencies` only.
`forbiddenDependencies` do not create graph edges, and current source-code references are not
used to infer architecture edges. The rule therefore validates the declared architecture policy,
not the implementation's observed dependency graph.

The analyzer computes strongly connected components once from the parsed contract. Each cyclic
component produces one deterministic representative cycle. Components and candidate edges are
ordered ordinally, and a self-edge such as `Domain -> Domain` is reported as a cycle. This keeps
the output stable across JSON ordering, concurrent analyzer execution and operating systems while
avoiding exponential enumeration of every possible simple cycle.

AARC011 has no C# source location because the violation belongs to the Architecture Contract
itself. A malformed `dependencyGraph` property is AARC001 instead.

### Minimal triggering example

```json
{
  "schemaVersion": 4,
  "layers": [
    { "name": "A", "namespaceRoots": [ "MyApp.A" ] },
    { "name": "B", "namespaceRoots": [ "MyApp.B" ] }
  ],
  "allowedDependencies": [
    { "from": "A", "to": [ "B" ] },
    { "from": "B", "to": [ "A" ] }
  ],
  "dependencyGraph": {
    "requireAcyclic": true
  }
}
```

The build fails with a cycle path such as:

```text
error AARC011: Declared architecture dependency graph contains a cycle: A -> B -> A
```

### Suppressing it

Prefer changing the declared graph. If the architecture intentionally permits cycles, remove the
DAG requirement or set `requireAcyclic` to `false`.

Standard severity control is available when a staged migration is unavoidable:

```ini
[*.cs]
dotnet_diagnostic.AARC011.severity = warning
```

There is no separate `architecture_analyzer.*` operational toggle for AARC011 because the
contract itself explicitly opts into the graph invariant.

---

## Fail-closed configuration policy

Every operational property is read with two distinct values: the **default** used when the
property is absent, and the **invalid fallback** used when it is present but unparseable. They
differ wherever the default is the permissive interpretation, so that a typo can only ever make
the analyzer stricter, never weaker.

| Property | Type | Accepted values | Default (absent) | Fallback (invalid) | Safety class |
|---|---|---|---|---|---|
| `dotnet_diagnostic.AARC001.architecture_analyzer.contract_required` | bool | `true` / `false` | `true` | `true` | fail-closed |
| `dotnet_diagnostic.AARC002.architecture_analyzer.enabled` | bool | `true` / `false` | `true` | `true` | fail-closed |
| `dotnet_diagnostic.AARC002.architecture_analyzer.require_layer_declaration` | bool | `true` / `false` | `true` | `true` | fail-closed (gates [AARC004](#aarc004)) |
| `dotnet_diagnostic.AARC002.architecture_analyzer.validate_namespace_layer` | bool | `true` / `false` | `false` | **`true`** | fail-closed (gates [AARC006](#aarc006)) |
| `dotnet_diagnostic.AARC002.architecture_analyzer.generated_code` | enum | `exclude` / `include` | `exclude` | **`include`** | fail-closed (skipping narrows coverage) |
| `dotnet_diagnostic.AARC002.architecture_analyzer.skip_generated_code` | bool | `true` / `false` | `true` | **`false`** | fail-closed |
| `dotnet_diagnostic.AARC003.architecture_analyzer.skip_generated_code` | bool | `true` / `false` | `true` | **`false`** | fail-closed |
| `dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC002.enabled` | bool | `true` / `false` | `true` | `true` | fail-closed |
| `dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC003.enabled` | bool | `true` / `false` | `true` | `true` | fail-closed |
| `dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC010.enabled` | bool | `true` / `false` | `true` | `true` | fail-closed |

Any other key containing `.architecture_analyzer.` is unknown and reported as
[AARC009](#aarc009). This table is the single source of truth for "what happens if I typo this";
it is mirrored in the XML documentation on `ConfigReader`.
