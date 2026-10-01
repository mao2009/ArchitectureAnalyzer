# Baseline / ratcheting design

Status: implemented by issue #48.

## Goal

A legacy project may already contain known architecture violations. Baseline mode freezes that
existing debt while continuing to reject newly introduced violations.

Architecture policy does not change: the Architecture Contract still describes what is forbidden.
The baseline records which *currently known diagnostic instances* are temporarily tolerated.

## Files and ownership

A governed compilation may optionally receive one additional file named:

```text
architecture.baseline.json
```

Like the Architecture Contract, it is routed through MSBuild `AdditionalFiles` and is scoped to
one Roslyn compilation. There is no solution-wide baseline cache.

Baseline schema v1:

```json
{
  "version": 1,
  "entries": [
    {
      "diagnosticId": "AARC002",
      "key": "MyApp.Domain.LegacyBridge -> MyApp.Application.LegacyService",
      "message": "'MyApp.Domain.LegacyBridge' (Domain) must not depend on ..."
    }
  ]
}
```

Only `diagnosticId` + `key` participate in matching. `message` is generated review metadata and
may change without changing the baseline identity.

## Stable identity

Baseline keys deliberately avoid line/column numbers.

Supported architecture-policy diagnostics use these identities:

- AARC002: original source type -> original target type;
- AARC003: original owning member -> forbidden-API rule identity. Member-specific rules use
  `Type.Member`; whole-type / any-member rules use `Type.*`;
- AARC004: original type;
- AARC005: original type;
- AARC006: original type;
- AARC007: original method + matched interop attribute;
- AARC010: original type;
- AARC011: canonical cycle path.

This keeps baseline entries stable when code moves inside a file while making symbol renames,
different target types, different API members and different cycles appear as new violations.

## Diagnostics that cannot be baselined

Integrity/configuration diagnostics are intentionally excluded:

- AARC001 — invalid/missing architecture contract;
- AARC008 — invalid operational configuration value;
- AARC009 — unknown operational configuration property;
- AARC012 — invalid architecture baseline;
- AARC013 — generator-only baseline capture record.

Suppressing these through the baseline could make enforcement itself disappear or conceal broken
tool configuration. Standard Roslyn severity controls still exist, but the supported ratcheting
workflow does not generate or accept baseline entries for them.

## Invalid baseline behavior

A malformed, unsupported or ambiguous baseline never weakens enforcement.

- no baseline file: normal analyzer behavior;
- exactly one valid baseline file: matching entries are tolerated;
- more than one baseline file: AARC012 and no baseline suppression;
- malformed/unsupported baseline: AARC012 and no baseline suppression.

Duplicate entry keys are rejected to keep review diffs deterministic.

## Enforcement order

For each baselinable diagnostic:

1. normal Architecture Contract analysis determines whether the violation exists;
2. the analyzer computes the stable baseline key;
3. if `diagnosticId + key` exists in the loaded baseline, that diagnostic is not reported;
4. otherwise the diagnostic is reported normally.

The baseline does not alter layer classification, dependency/API policy, severity or contract
exception semantics.

Schema-v5 exact `exceptions` and baselines serve different purposes:

- contract exception: architecture intentionally permits one exact case;
- baseline entry: architecture still forbids the case, but existing debt is temporarily tolerated.

A contract exception is checked first because no violation exists from the analyzer's perspective.
The baseline only sees diagnostics that would otherwise be emitted.

## Baseline generation

The supported generator is a .NET tool:

```text
loach.ArchitectureAnalyzer.Baseline
command: architecture-baseline
```

Run from a governed project:

```bash
architecture-baseline generate MyApp.csproj
```

The tool:

1. invokes `dotnet build` with a temporary SARIF error log and
   `ArchitectureAnalyzerBaselineMode=ignore`;
2. the analyzer reports all current architecture-policy diagnostics and, only in capture mode,
   emits companion AARC013 warnings containing `diagnosticId + stable key`;
3. the tool reads those explicit capture records from compiler SARIF and refuses to update the
   baseline when the build contains compiler errors or ArchitectureAnalyzer
   integrity/configuration diagnostics;
4. it writes deterministic, sorted baseline entries for all current baselinable diagnostics;
5. fixed diagnostics disappear on the next generation, naturally shrinking the baseline.

The analyzer NuGet package exposes `ArchitectureAnalyzerBaselineMode` to Roslyn through a
`buildTransitive` `CompilerVisibleProperty`. Normal mode is `enforce`; only the generator uses
`ignore` while capturing the complete diagnostic set.

AARC013 exists because compiler SARIF does not consistently preserve arbitrary
`Diagnostic.Properties` across hosts. It is never emitted in normal `enforce` mode, so ordinary
developer/CI builds do not gain an extra warning. The generator treats AARC013 as transport
metadata, not architecture debt.

## Review and safety

Baseline generation is an explicit command. Normal builds never add baseline entries.

A newly introduced diagnostic has a new stable key and fails the build until a developer either:

- fixes the violation; or
- deliberately regenerates and reviews the baseline diff.

The generated JSON is deterministic and source-control friendly: entries are ordered first by
diagnostic ID, then ordinally by key.

## Incremental adoption workflow

For an existing project:

1. add `architecture.baseline.json` to `AdditionalFiles`;
2. install/run the baseline tool once;
3. review and commit the generated debt list;
4. keep normal AARC severities unchanged;
5. fix baseline entries over time;
6. rerun the generator after fixes to prune stale entries;
7. treat any newly reported diagnostic as newly introduced architecture debt.

Once the baseline reaches an empty entry set, the file can be removed entirely.

## Performance

Baseline parsing happens once per compilation. Matching uses an immutable hash set keyed by
`(diagnosticId, key)`.

No additional syntax, symbol or operation callback is introduced.

## Non-goals

- no fuzzy matching by line number;
- no wildcard baseline entries;
- no automatic baseline mutation during normal builds or IDE analysis;
- no baselining of contract/configuration integrity diagnostics;
- no solution-global baseline shared implicitly across compilations;
- no weakening of normal diagnostic severities.
