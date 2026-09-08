# Configuration

This document is the single source of truth for **how the analyzer runs**: the `.editorconfig` /
AnalyzerConfig properties that tune its operation, their scope, precedence, defaults and
invalid-value behavior.

It is deliberately *not* about **what the rules are**. That is the Architecture Contract's job.

## 1. Two configuration surfaces, one responsibility each

| | Architecture Contract | Analyzer configuration |
|---|---|---|
| File | `architecture.contract.json` (via `AdditionalFiles`) | `.editorconfig` / `.globalconfig` |
| Answers | *What is the architecture?* | *How does the analyzer run here?* |
| Owns | layers, forbidden edges, forbidden APIs, marker attributes, interop boundaries | severity, on/off switches, generated-code handling |
| Schema reference | [`architecture.md` §2](architecture.md#2-contract-schema) | this document |
| Reviewed as | an architectural decision | a build/tooling decision |

The split matters because the contract is the reviewable declaration of intent — it belongs in
pull requests and design discussions. Configuration is operational plumbing: a team may need to
silence a rule in one legacy folder without anybody claiming the architecture changed. Encoding a
rule in `.editorconfig` instead of the contract hides it from the document that is supposed to
describe the system; encoding an operational exception in the contract corrupts the declaration
with local build concerns. Keep each on its own side of the line.

## 2. Severity: the standard Roslyn mechanism

Every diagnostic responds to the ordinary `dotnet_diagnostic` severity key. Nothing analyzer-specific
is involved, and no ArchitectureAnalyzer property is needed to change a severity:

```ini
[*.cs]
dotnet_diagnostic.AARC002.severity = error     # error | warning | suggestion | silent | none

[legacy/**.cs]
dotnet_diagnostic.AARC002.severity = warning   # narrower scope wins
```

Per-line suppression uses the usual `#pragma warning disable AARC002` /
`[SuppressMessage("Architecture", "AARC002")]`. Per-diagnostic guidance lives in
[`diagnostics.md`](diagnostics.md).

## 3. Operational properties

Operational properties are read by the analyzer itself and are named:

```
dotnet_diagnostic.<AARCxxx>.architecture_analyzer.<property>
```

The `dotnet_diagnostic.<ID>.` prefix is a naming convention, not a lookup dimension: it exists so
the keys inherit `.editorconfig` file-scope semantics that Roslyn already applies to the
`dotnet_diagnostic.*` namespace. **The ID segment is part of the literal key**, so a property must
be written with exactly the ID listed below — most properties are carried under `AARC002`
regardless of which rule they affect. `dotnet_diagnostic.AARC004.architecture_analyzer.require_layer_declaration`
is not a synonym for the AARC002-prefixed key; it is simply an unrecognized key that does nothing.

### 3.1 Property list

Source of truth: [`src/ArchitectureAnalyzer/Configuration/ConfigReader.cs`](../src/ArchitectureAnalyzer/Configuration/ConfigReader.cs).

| Property key | Type | Default | Scope | Effect |
|---|---|---|---|---|
| `dotnet_diagnostic.AARC001.architecture_analyzer.contract_required` | bool | `true` | compilation | When `false`, a missing or malformed contract stops producing [AARC001](diagnostics.md#aarc001) and the analyzer silently no-ops |
| `dotnet_diagnostic.AARC002.architecture_analyzer.enabled` | bool | `true` | per file | When `false`, AARC002/AARC003 are skipped for that file (see §3.2 for what it does *not* cover) |
| `dotnet_diagnostic.AARC002.architecture_analyzer.require_layer_declaration` | bool | `true` | per file | When `false`, code in an unclassified namespace joins a synthetic `Unclassified` layer instead of being invisible, and [AARC004](diagnostics.md#aarc004) is not enforced |
| `dotnet_diagnostic.AARC002.architecture_analyzer.validate_namespace_layer` | bool | `false` | per file | When `true`, [AARC006](diagnostics.md#aarc006) is checked even if the contract leaves `validateNamespaceConsistency` off |
| `dotnet_diagnostic.AARC002.architecture_analyzer.generated_code` | `exclude` / `include` | `exclude` | per file | `include` analyzes generated-path files for AARC002/AARC003 |
| `dotnet_diagnostic.AARC002.architecture_analyzer.skip_generated_code` | bool | `true` | per file | Boolean form of the above; `false` on *either* this or the AARC003 key analyzes generated-path files |
| `dotnet_diagnostic.AARC003.architecture_analyzer.skip_generated_code` | bool | `true` | per file | See above |
| `dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC002.enabled` | bool | `true` | per file | Turns AARC002 off for that file without touching its severity |
| `dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC003.enabled` | bool | `true` | per file | Turns AARC003 off for that file without touching its severity |

Only `rule.AARC002.enabled` and `rule.AARC003.enabled` exist; there is no `rule.AARC004.enabled`
and the like. AARC004/AARC005/AARC006 are gated by `require_layer_declaration` /
`validate_namespace_layer` plus their contract settings, and AARC007 reads no operational property
at all — use the standard severity key to turn those off.

### 3.2 Scope, precisely

- **Per file** means the value is resolved for the syntax tree being analyzed, so ordinary
  `.editorconfig` section matching applies (`[*.cs]`, `[src/Legacy/**.cs]`, per-directory files).
- **Compilation** applies to `contract_required` only: it is evaluated across every syntax tree in
  the compilation, and `false` anywhere wins — a single file opting out disables AARC001 for the
  whole compilation. AARC001 has no source location, so there is no narrower scope available.
- `enabled = false` suppresses **AARC002 and AARC003 only**. AARC004/AARC005/AARC006 (symbol-based
  declaration analysis) and AARC007 (interop boundaries) do not consult it, and AARC001 and AARC008
  remain reportable. Turning a file off completely therefore also needs severity keys or a
  contract change.
- `enabled = false` short-circuits reading every other property for that file, so the remaining
  values stay at their defaults there.
- Generated-code exclusion for AARC004–AARC007 is unconditional — those rules always skip
  generated paths, whatever `generated_code` / `skip_generated_code` say. Those two properties only
  affect AARC002/AARC003.

Generated paths are recognized by file path: anything ending in `.g.cs`, `.g.i.cs`, `.designer.cs`
or `.generated.cs`, or living under an `obj/` or `bin/` directory. Roslyn's own
`GeneratedCodeAnalysisFlags.None` exclusion applies on top of that and is not configurable.

### 3.3 Precedence

Ordinary AnalyzerConfig precedence, nothing custom:

1. The **most specific** `.editorconfig` section wins. A deeper `.editorconfig` file overrides a
   shallower one, and within one file a narrower glob overrides a broader one.
2. `.globalconfig` entries act as a compilation-wide fallback surfaced through the same per-tree
   lookup.
3. When no entry matches, the hardcoded default from the table above applies.

```ini
# repo root .editorconfig
root = true

[*.cs]
dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC002.enabled = true
```

```ini
# src/Legacy/.editorconfig — deeper file wins for the files it covers
[*.cs]
dotnet_diagnostic.AARC002.architecture_analyzer.rule.AARC002.enabled = false
```

## 4. Invalid values

A property that is present but unparseable (`require_layer_declaration = yes`, or a
`generated_code` value that is neither `exclude` nor `include`) does **not** fail the build and
does **not** silently guess. The analyzer:

1. ignores the value and uses the default from §3.1,
2. reports [AARC008](diagnostics.md#aarc008) — `Warning` — once per offending property per
   compilation, with no source location.

An absent property, an empty value, and a whitespace-only value are all treated as "not set" and
take the default without a diagnostic. Booleans are parsed by `bool.TryParse` (`true`/`false`,
case-insensitive; `1`/`0`/`yes`/`no` are *not* accepted), and `generated_code` is parsed
case-insensitively against its two names.

Unknown property names — a typo in the property or in the ID segment — are currently invisible:
the analyzer looks up exact keys, so a misspelled key is simply never read and the intended
setting silently never applies.

> **Forward reference — configuration validation (issue [#31](https://github.com/mao2009/ArchitectureAnalyzer/issues/31),
> PR [#39](https://github.com/mao2009/ArchitectureAnalyzer/pull/39)).** That work adds **AARC009**
> (unknown `architecture_analyzer.*` property) and a fail-closed policy in which an *invalid* value
> falls back to the safer interpretation rather than to the default. Both are defined in
> `docs/diagnostics.md` by that PR and are deliberately not restated here. Until it merges, the
> behavior documented above — warn and fall back to the default, unknown keys undetected — is what
> ships.

## 5. What configuration cannot do

Configuration cannot add, remove or reinterpret a rule. It cannot declare a layer, forbid an edge,
forbid an API, register a marker attribute or move an interop boundary — all of that requires
editing `architecture.contract.json`, where it is reviewable as an architectural change. The
strongest thing `.editorconfig` can do is stop the analyzer from looking, which is visible in the
configuration file itself rather than hidden in a rule definition.

The reverse also holds: the contract carries no operational settings. There is no way to pin a
severity, skip generated code or disable a rule from inside the contract document.

## 6. Worked example

```ini
root = true

[*.cs]
# Enforcement level: standard Roslyn keys.
dotnet_diagnostic.AARC002.severity = error
dotnet_diagnostic.AARC003.severity = error
dotnet_diagnostic.AARC006.severity = warning

# Operational: check declaration/namespace drift repo-wide.
dotnet_diagnostic.AARC002.architecture_analyzer.validate_namespace_layer = true

# A vendored tree that must build but is not part of the architecture yet.
[src/Vendor/**.cs]
dotnet_diagnostic.AARC002.architecture_analyzer.enabled = false
dotnet_diagnostic.AARC004.severity = none
```

## 7. See also

- [`architecture.md`](architecture.md) — the contract schema and the analysis pipeline
- [`diagnostics.md`](diagnostics.md) — per-diagnostic reference, including AARC008
- [`design.md`](design.md) — why the contract, not the analyzer or its configuration, is the SSOT
- [`compatibility/psxrecomp-analyzer-baseline.md`](compatibility/psxrecomp-analyzer-baseline.md) —
  capability baseline for the PSXRecomp.Analyzer consumer
