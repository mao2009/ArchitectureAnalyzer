# Diagnostics

Every diagnostic in this analyzer belongs to the `Architecture` category, ships with
`DefaultSeverity = Error` and `EnabledByDefault = true`, and carries no project-specific
knowledge of its own — the layer names, namespace roots and API rules it names in its messages
all come from the consuming project's `architecture.contract.json`.

IDs are never reused or renumbered once shipped (see [`design.md` §7](design.md#7-diagnostic-id-namespace));
a retired rule is marked obsolete here rather than having its ID reassigned.

| ID | Title | Severity | Enabled by default |
|---|---|---|---|
| [AARC001](#aarc001) | Architecture contract could not be loaded | Error | Yes |
| [AARC002](#aarc002) | Forbidden architecture dependency direction | Error | Yes |
| [AARC003](#aarc003) | Forbidden API usage in architecture layer | Error | Yes |

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
