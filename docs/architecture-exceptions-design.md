# Architecture exception and suppression policy

Status: accepted design target for issue #24; implementation is tracked by #8.

## Goal

ArchitectureAnalyzer must allow deliberate exceptions without turning broad suppression into the
normal way architecture rules are maintained.

The policy therefore distinguishes two cases:

1. **Long-lived architecture exceptions** belong in the Architecture Contract and require an
   explicit human-readable justification.
2. **Operational/compiler suppression** remains available through normal Roslyn mechanisms, but it
   is not the recommended representation of architecture intent.

No global analyzer bypass is added.

## Why contract-owned exceptions

A long-lived exception changes the architecture that the repository intentionally permits. Keeping
that exception in `architecture.contract.json` makes it:

- visible in the same review as the rule it weakens;
- diffable and searchable in source control;
- independent of source formatting or file movement;
- explicit about exactly which diagnostic, source type and target/API are exempt;
- able to require justification as schema data rather than relying on an unenforced comment.

A custom source attribute is deliberately not introduced. Requiring consumers to define or
reference an analyzer-specific runtime attribute would leak tooling concepts into production code.

## Schema v5

Schema v5 adds an optional `exceptions` array.

The first implementation intentionally supports only AARC002 and AARC003. Those are concrete
source-code architecture violations with stable symbol identities and are the cases where
long-lived, narrowly scoped exceptions are useful.

Contract integrity and architecture-shape diagnostics remain non-exceptionable through this
mechanism:

- AARC001 contract loading/schema failures;
- AARC004/AARC005/AARC006 declaration consistency;
- AARC007 interop boundary policy;
- AARC010 layer coverage;
- AARC011 declared graph cycles.

Standard Roslyn suppression can still affect those diagnostics because the compiler owns final
diagnostic severity/suppression, but the Architecture Contract does not provide a special bypass.

### AARC002 dependency exception

```jsonc
{
  "diagnosticId": "AARC002",
  "sourceType": "MyApp.Domain.LegacyBridge",
  "targetType": "MyApp.Application.LegacyService",
  "justification": "Temporary compatibility bridge while ticket ARCH-123 removes the legacy dependency."
}
```

The exception matches one exact resolved source type -> target type pair.

Layer names are intentionally not part of the key: moving either type to a different layer should
not silently create a new exception for another pair of types, while renaming/changing the actual
types makes the stale exception stop matching.

### AARC003 forbidden-API exception

```jsonc
{
  "diagnosticId": "AARC003",
  "sourceType": "MyApp.Domain.LegacyClock",
  "apiType": "System.DateTime",
  "member": "Now",
  "justification": "Legacy serialization timestamp; replacement is tracked by ARCH-456."
}
```

The exception matches one exact source type + API declaring type + member name.

`member` is required so the exception cannot silently suppress every forbidden member of a type.
The member name uses the same normalized symbol name the analyzer already uses for forbidden API
matching:

- properties match by property name, e.g. `Now`;
- ordinary methods/fields/events match by symbol name;
- constructors use `.ctor` (and static constructors `.cctor`).

If multiple API members must be exempted, declare multiple exception entries so each reviewable
exception is explicit.

## Required justification

Every contract exception must contain a non-empty `justification`.

The analyzer does not prescribe a ticket format, expiry date or prose style. Teams may include an
issue/ADR identifier in the text, but the schema only requires that a human-readable reason exist.

No time-based expiry is built into v5. Analyzer behavior must remain deterministic and independent
of wall-clock time. Expiry/ratcheting can be handled by a later dedicated feature rather than
making normal builds date-sensitive.

## Exact matching

All exception identity strings are ordinal and case-sensitive.

Type names are compared against the Roslyn symbol's fully qualified display name used by the
analyzer's existing architecture checks, normalized to the original generic definition. This keeps
matching semantic rather than textual: aliases and using directives do not change the key.

Exceptions are intentionally exact:

- no wildcard source types;
- no namespace-prefix exemptions;
- no layer-wide exceptions;
- no regex/glob matching;
- no "all AARC002" / "all AARC003" contract switch.

This is the core guardrail against a legitimate exception becoming an undeclared global bypass.

## Validation

A contract fails with AARC001 when:

- `exceptions` is used before schema v5;
- `exceptions` appears more than once;
- `exceptions` is not an array;
- an entry is not an object;
- `diagnosticId` is missing/empty or is not exactly `AARC002` or `AARC003`;
- `sourceType` is missing/empty;
- `justification` is missing/empty;
- AARC002 is missing `targetType`;
- AARC002 supplies AARC003-only `apiType` or `member`;
- AARC003 is missing `apiType` or `member`;
- AARC003 supplies AARC002-only `targetType`;
- two exception entries describe the same exact exception key.

Unknown additive metadata continues to follow the normal supported-schema compatibility policy.

The loader does not attempt to prove that a referenced source/target/API type currently exists.
Contracts are reusable across conditional builds and partial source sets; a stale exception simply
matches nothing and weakens no rule.

## Enforcement order

For AARC002/AARC003:

1. resolve the architecture rule normally;
2. determine that the observed dependency/API usage violates the rule;
3. check for an exact contract exception;
4. if matched, do not report that diagnostic;
5. otherwise report the existing AARC002/AARC003 diagnostic unchanged.

Exception matching therefore never converts an allowed operation into a violation and never changes
layer/API resolution.

## Relationship to standard Roslyn suppression

ArchitectureAnalyzer cannot and should not override the compiler's normal diagnostic controls.

After the analyzer reports a diagnostic, normal Roslyn behavior still applies:

- `dotnet_diagnostic.AARC002.severity = none`;
- scoped `.editorconfig` severity changes;
- `#pragma warning disable AARC002`;
- `SuppressMessageAttribute`;
- project-level `NoWarn`.

Precedence is therefore:

```text
Architecture Contract rule
        ->
exact contract exception (if any)
        ->
analyzer emits diagnostic
        ->
Roslyn severity / pragma / SuppressMessage / NoWarn
```

Operational `architecture_analyzer.enabled=false` or per-rule enable switches can stop analysis
before a contract exception is even relevant; those remain rollout/tooling controls, not
architecture intent.

## Recommended usage

Use a schema-v5 contract exception when:

- the violation is deliberate and expected to survive more than one local edit;
- the exact source/target pair or API use is understood;
- reviewers should see the exception next to the architecture policy;
- a justification can explain why the exception exists.

Use a local `#pragma` or `SuppressMessageAttribute` only for genuinely source-local or temporary
situations where changing the central contract would be more misleading.

Use narrowly scoped `.editorconfig` suppression for migration/legacy-tree rollout.

Avoid:

- solution-wide `NoWarn`;
- root-level `dotnet_diagnostic.AARCxxx.severity = none`;
- `architecture_analyzer.enabled=false` across normal production source;
- suppressing AARC001 merely to make a malformed contract build.

Those mechanisms remain explicit and possible because they are standard compiler controls, but they
are intentionally documented as broad operational overrides rather than architecture exceptions.

## Audit properties

A contract exception is auditable because every entry contains:

- the exact diagnostic ID;
- exact source type;
- exact target type or exact API type/member;
- required justification.

Reviewers can answer "what rule is bypassed, where, and why?" from a single diff.

The implementation should expose the parsed exception list through the immutable contract model so
future tooling can inventory exceptions without re-parsing ad hoc JSON.

## Gate verification

The real-build gate should prove both sides of the policy in the same contract:

1. an AARC002 violation with an exact contract exception builds successfully;
2. another AARC002 violation on the same forbidden layer edge but from a different source type still
   fails with AARC002.

This demonstrates that the exception does not accidentally suppress the rule, layer pair or entire
diagnostic.

Unit tests should provide the equivalent proof for AARC003: one exact source/API/member exception is
silent while another member or source type still reports.

## Implementation requirements for #8

The implementation PR should:

- advance `CurrentSchemaVersion` to 5 while preserving versionless/v1/v2/v3/v4 behavior;
- add immutable typed exception models for AARC002 and AARC003;
- validate `exceptions` according to this document;
- perform exception matching only after an actual AARC002/AARC003 violation has been identified;
- add no new source scan or callback;
- add loader/analyzer tests for exact matches, near misses and unrelated violations;
- extend the real build gate with exact dependency-exception success plus unrelated failure;
- document the contract-vs-Roslyn suppression precedence and recommended/discouraged patterns.

## Non-goals

- no wildcard exceptions;
- no layer-wide exception;
- no global analyzer bypass;
- no analyzer-specific runtime attribute;
- no date-sensitive expiry;
- no contract exception for AARC001/AARC004–AARC011 in schema v5;
- no attempt to prevent developers from using standard compiler suppression mechanisms.
