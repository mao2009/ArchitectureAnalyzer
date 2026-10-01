# Allowed dependency policy design

Status: implemented by #6 as Architecture Contract schema v3.

## Goal

ArchitectureAnalyzer currently expresses negative dependency rules through
`forbiddenDependencies`. That is enough when a project knows a few prohibited directions, but it
cannot express a positive guarantee such as:

> Domain may depend on Shared, and on no other declared layer.

Schema v3 will add an opt-in `allowedDependencies` section for that purpose. The design is
intentionally incremental: a consumer can make one source layer strict without converting the
entire architecture graph at once.

## Schema

```jsonc
{
  "schemaVersion": 3,

  "layers": [
    { "name": "Domain", "namespaceRoots": [ "MyApp.Domain" ] },
    { "name": "Application", "namespaceRoots": [ "MyApp.Application" ] },
    { "name": "Shared", "namespaceRoots": [ "MyApp.Shared" ] }
  ],

  "allowedDependencies": [
    {
      "from": "Domain",
      "to": [ "Shared" ],
      "reason": "Domain may depend only on Shared."
    },
    {
      "from": "Shared",
      "to": [],
      "reason": "Shared is dependency-free with respect to other architecture layers."
    }
  ]
}
```

`allowedDependencies` is valid only for schema v3 or later. Supplying it to a versionless, v1,
or v2 contract is a contract error (AARC001), rather than an ignored future-semantic property.

Each entry contains:

- `from`: required declared layer name.
- `to`: required array of declared layer names. An empty array means the source layer may not
  depend on any *other* declared layer.
- `reason`: optional human-readable rationale used for AARC002 when an unlisted target is used.

A source layer may appear at most once. A target may appear at most once within that source rule.

## Enforcement semantics

### Presence is opt-in

If `allowedDependencies` is absent, behavior is unchanged from schema v1/v2.

If it is present, only source layers that have an entry are governed by the positive allowlist.
Source layers omitted from the section remain permissive with respect to allowlisting and continue
to be governed by any `forbiddenDependencies` rules.

This is deliberate. It lets a repository ratchet one layer at a time without having to enumerate
the complete architecture graph in one migration.

To make every declared layer strict, add one rule for every source layer. A rule with `to: []`
expresses "this layer may not depend on any other layer."

### Same-layer references

References within the same resolved layer are always allowed. `allowedDependencies` governs only
cross-layer dependencies, matching the existing AARC002 model.

A consumer therefore does not need to list a layer as a target of itself.

### Direct dependencies only

The analyzer checks direct symbol references exactly as AARC002 does today. No transitive closure
is inferred.

If A directly references B and B directly references C, the analyzer checks A -> B and B -> C.
A -> C is checked only if code in A directly references a symbol in C.

This keeps the rule aligned with observable source dependencies and avoids inventing an implicit
graph-analysis DSL.

### Layer resolution

Source and target layers use the existing operational layer resolution:

1. recognized marker attribute on the type or containing type,
2. otherwise namespace-root classification.

Unclassified source/target symbols are not interpreted as declared allowlist nodes. Schema-v2+
`unclassifiedCode=error` / AARC010 is the mechanism for requiring complete classification.

### Diagnostic

An observed cross-layer dependency from a governed source layer to a target not listed in its
`to` array reports **AARC002**.

A new diagnostic ID is unnecessary because the architectural failure is still the same category:
a dependency direction that the contract does not permit. Existing AARC002 severity and
operational configuration therefore apply consistently to both blacklist and allowlist
violations.

If the allowlist rule supplies `reason`, that rationale is used. Otherwise the analyzer supplies
an actionable fallback such as:

`target layer 'Application' is not listed in allowedDependencies for 'Domain'`

## Interaction with forbiddenDependencies

`forbiddenDependencies` remains supported in schema v3.

Rules are evaluated deterministically:

1. an explicit matching `forbiddenDependencies` edge denies the dependency and supplies its
   existing reason;
2. otherwise, if the source layer has an `allowedDependencies` rule, a target omitted from
   `to` is denied by the allowlist;
3. otherwise the dependency is permitted by these dependency-direction rules.

A contract that explicitly lists the *same edge* as both allowed and forbidden is contradictory
and is rejected during contract loading with AARC001.

A forbidden edge that is already absent from a source layer's allowlist is redundant but not
contradictory. It remains valid because it may preserve a specific rationale while a project
migrates from blacklist to allowlist policy.

## Validation

Contract loading rejects the following deterministically:

- `allowedDependencies` used before schema v3;
- a non-array `allowedDependencies` value;
- a non-object entry;
- missing/empty/non-string `from`;
- `from` naming an undeclared layer;
- duplicate `from` rules;
- missing or non-array `to`;
- non-string/empty target entries;
- target names that are not declared layers;
- duplicate targets within one rule;
- an exact edge declared both allowed and forbidden.

As with the existing contract model, layer names are ordinal and case-sensitive.

## Backward compatibility

- versionless contracts remain v1 and behave exactly as today;
- explicit schema v1 remains supported;
- explicit schema v2 remains supported, including `unclassifiedCode`;
- schema v3 adds `allowedDependencies`;
- older analyzers that do not understand v3 reject the contract rather than interpreting it using
  older semantics;
- new analyzers continue accepting v1/v2 contracts.

## Migration examples

### One-layer ratchet

```json
"allowedDependencies": [
  { "from": "Domain", "to": [ "Shared" ] }
]
```

Only Domain becomes strict. Application and Shared keep existing permissive behavior except for
any explicit forbidden edges.

### Fully explicit graph

```json
"allowedDependencies": [
  { "from": "Domain", "to": [ "Shared" ] },
  { "from": "Application", "to": [ "Domain", "Shared" ] },
  { "from": "Shared", "to": [] }
]
```

Every declared source layer is now positively constrained.

### Coexistence with an explicit prohibition

```json
"allowedDependencies": [
  { "from": "Application", "to": [ "Domain", "Shared" ] }
],
"forbiddenDependencies": [
  {
    "from": "Application",
    "to": "Infrastructure",
    "reason": "Application must access infrastructure only through ports."
  }
]
```

The explicit prohibition is consistent because Infrastructure is not in Application's allowed
targets; its more specific reason is used if that dependency is observed.

## Implementation shape

The implementation follows this design:

- advance `CurrentSchemaVersion` to 3 while preserving v1/v2 support;
- add a validated immutable allowed-dependency model to `ArchitectureContract`;
- integrate allowlist enforcement into the existing AARC002 dependency path rather than adding a
  second syntax scan;
- preserve explicit-forbidden precedence;
- add loader tests for every validation rule above;
- add analyzer tests for allowed, denied, omitted-source, empty-target, marker-resolved and
  blacklist/allowlist interaction cases;
- extend the real build gate with at least one allowlist violation and clean recovery;
- publish the schema and AARC002 behavior in the public architecture/diagnostic documentation.

## Non-goals

- no mandatory allowlisting for existing contracts;
- no transitive dependency inference;
- no arbitrary graph DSL;
- no project/repository-specific layer names;
- no cycle detection in schema v3 itself; declared-graph DAG enforcement is the separate
  schema-v4 `dependencyGraph.requireAcyclic` / AARC011 feature.
