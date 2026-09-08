# Diagnostics

Every diagnostic in this analyzer belongs to the `Architecture` category, ships with
`EnabledByDefault = true`, and carries no project-specific knowledge of its own — the layer
names, namespace roots, API rules and interop attributes it names in its messages all come from
the consuming project's `architecture.contract.json`. Most diagnostics are `Error`; AARC006 and
AARC008 are `Warning` because they describe drift or misconfiguration rather than a known
violation.

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
raw `System.Text.Json` parse error, or a schema-validation message such as
`layer 'Application' referenced in forbiddenDependencies is not declared in layers`.

### Description

Raised when a project declares an `architecture.contract.json` in `AdditionalFiles` but that file
cannot be read, is not valid JSON, or is internally inconsistent. It exists so that a broken
contract fails loudly instead of silently switching enforcement off — a silently disabled
analyzer would be less trustworthy than the hand-maintained document it replaces.

A project that declares **no** contract file at all is not an error: the analyzer is opt-in and
does nothing at all in that case. The diagnostic is reported once per compilation, without a
source location, because the failure is a property of the compilation rather than of any one
line of code.

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
and the `reason` string from the matching `forbiddenDependencies` entry.

### Description

Raised when a type whose namespace maps to one declared layer references a type whose namespace
maps to another declared layer, and the contract lists that `from` → `to` pair under
`forbiddenDependencies`. This is the flagship rule: it turns "Domain must not know about
Application" from a sentence in a document into a build error. The check is directional — the
reverse edge is allowed unless the contract forbids it separately — and each distinct
source-type → target-type pair is reported once per compilation rather than once per reference.

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
| **Message format** | `The value '{1}' for property '{0}' is not valid; falling back to the default` |
| **Category** | `Architecture` |
| **Severity** | `Warning` |
| **Enabled by default** | Yes |

Arguments are the property name and the offending value.

### Description

Raised when an `architecture_analyzer.*` operational property in `.editorconfig` or
`.globalconfig` has a value that cannot be parsed (for example `require_layer_declaration = yes`
instead of `true`/`false`). The property is ignored, the hardcoded default is used, and the build
keeps working — a warning, because silently guessing would be worse. Reported once per invalid
property per compilation, without a source location.

Supported properties:

| Property | Type | Default | Meaning |
|---|---|---|---|
| `dotnet_diagnostic.AARC002.architecture_analyzer.require_layer_declaration` | bool | `true` | Gate AARC004 (see [AARC004](#aarc004)) |
| `dotnet_diagnostic.AARC002.architecture_analyzer.validate_namespace_layer` | bool | `false` | Gate AARC006 (see [AARC006](#aarc006)) |

### Suppressing it

Fix the typo in the configuration file. A suppressed AARC008 hides a misconfiguration that will
re-appear every build until corrected.
