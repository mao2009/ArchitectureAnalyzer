# Declared dependency-cycle detection design

Status: implemented by #7 as Architecture Contract schema v4 / AARC011.

## Goal

Some consumers want their declared architecture dependency graph to be a DAG. ArchitectureAnalyzer
must support that constraint without inferring runtime architecture or treating every project as a
DAG by default.

Schema v4 adds an opt-in graph policy:

```jsonc
{
  "schemaVersion": 4,

  "layers": [
    { "name": "Domain", "namespaceRoots": [ "MyApp.Domain" ] },
    { "name": "Application", "namespaceRoots": [ "MyApp.Application" ] },
    { "name": "Shared", "namespaceRoots": [ "MyApp.Shared" ] }
  ],

  "allowedDependencies": [
    { "from": "Domain", "to": [ "Shared" ] },
    { "from": "Application", "to": [ "Domain" ] },
    { "from": "Shared", "to": [] }
  ],

  "dependencyGraph": {
    "requireAcyclic": true
  }
}
```

When `requireAcyclic` is absent or false, cycle analysis is disabled.

## Which graph is checked

The graph is the **explicit positive policy graph** declared by `allowedDependencies`:

- every declared architecture layer is a vertex;
- every `allowedDependencies[].from -> to[]` entry is a directed edge;
- source layers omitted from `allowedDependencies` contribute no outgoing declared edges;
- an empty `to` array contributes no outgoing edges;
- `forbiddenDependencies` are constraints that remove/deny behavior, not positive graph edges, and
  therefore do not participate in cycle construction;
- observed source-code references do not participate;
- no transitive edges are invented.

This distinction is intentional. AARC011 answers:

> Does the architecture policy explicitly permit a dependency cycle?

It does not answer:

> Does the current implementation happen to contain a runtime/source dependency cycle?

The latter would require a separate observed-dependency feature.

## Schema and compatibility

`dependencyGraph` is a schema-v4 semantic property.

- versionless remains schema v1;
- explicit v1/v2/v3 remain supported unchanged;
- `dependencyGraph` used before schema v4 is rejected with AARC001;
- an analyzer that understands only schemas up to v3 rejects a v4 contract rather than silently
  ignoring the DAG requirement.

Shape:

```json
"dependencyGraph": {
  "requireAcyclic": true
}
```

`dependencyGraph` is optional. When present:

- it must be a JSON object;
- it may appear only once at the contract root;
- `requireAcyclic`, when present, must appear only once and must be a boolean;
- `requireAcyclic` defaults to `false` when omitted.

Unknown metadata properties continue to follow the normal supported-schema compatibility policy.

## Diagnostic

A valid contract whose explicit allowed-dependency graph contains a cycle reports **AARC011**.

Proposed descriptor:

- ID: `AARC011`
- title: `Declared architecture dependency graph contains a cycle`
- severity: Error
- message:
  `Declared architecture dependency graph contains a cycle: {0}`

The path argument uses arrows and repeats the starting layer at the end:

`Application -> Domain -> Shared -> Application`

AARC011 has no C# source location because the violation belongs to the Architecture Contract graph,
not to one code reference. Standard Roslyn severity configuration remains available through
`dotnet_diagnostic.AARC011.severity`; no separate operational toggle is required.

A malformed `dependencyGraph` property remains AARC001. A well-formed but cyclic graph is AARC011.
This keeps syntax/schema validity distinct from architecture-policy validity.

## Deterministic cycle reporting

The implementation reports **one canonical cycle for each cyclic strongly connected component
(SCC)**.

This avoids both extremes:

- reporting only the first cycle and forcing repeated fix/rebuild iterations;
- enumerating every possible simple cycle, which can grow exponentially and create noisy,
  unstable diagnostics.

Determinism rules:

1. SCCs are ordered by the ordinally smallest layer name they contain.
2. A singleton SCC is cyclic only when it contains an explicit self-edge.
3. For each cyclic SCC, choose the ordinally smallest layer as the cycle start.
4. Traverse candidate outgoing edges in ordinal layer-name order.
5. Report the first deterministic path that returns to the start while staying within the SCC.
6. The start layer is repeated at the end of the rendered path.

The exact output therefore does not depend on JSON edge order, hash-table enumeration, Roslyn
callback scheduling, or operating system.

## Self-loops

Although same-layer source references are always permitted by AARC002 and do not need to be
declared, schema v3 currently allows a consumer to write an explicit positive edge such as:

```json
{ "from": "Domain", "to": [ "Domain" ] }
```

Under `requireAcyclic=true`, that explicit self-edge is a cycle and produces:

`Domain -> Domain`

When DAG enforcement is disabled, the redundant self-edge remains accepted.

## Multiple cycles

Example:

```json
"allowedDependencies": [
  { "from": "A", "to": [ "B" ] },
  { "from": "B", "to": [ "A" ] },
  { "from": "C", "to": [ "D" ] },
  { "from": "D", "to": [ "E" ] },
  { "from": "E", "to": [ "C" ] }
]
```

With `requireAcyclic=true`, two AARC011 diagnostics are reported, one for the `A/B` SCC and one
for the `C/D/E` SCC, in deterministic order.

If a single SCC contains several possible cycles, only one canonical cycle path is reported for
that SCC.

## Partial allowlists

Partial strictness remains valid:

```json
"allowedDependencies": [
  { "from": "Domain", "to": [ "Shared" ] }
],
"dependencyGraph": {
  "requireAcyclic": true
}
```

Only the explicit `Domain -> Shared` edge is in the graph. Omitted source layers are not treated as
"edges to everything" and are not inferred from current code.

This means DAG enforcement can be adopted incrementally together with positive allowlisting.

## Performance

Cycle detection is contract-graph work, not source-symbol work.

The graph is tiny relative to a compilation and is immutable after contract loading. The
implementation should compute the canonical cycle list once from the parsed contract and register a
compilation-end diagnostic only when `requireAcyclic=true`.

No syntax/symbol callback or additional source scan should be introduced.

## Implementation shape

The implementation follows this design:

- advance `CurrentSchemaVersion` to 4 while preserving versionless/v1/v2/v3 behavior;
- add a validated immutable `DependencyGraphPolicy` or equivalent contract model;
- add AARC011 as an Error diagnostic;
- compute cyclic SCCs from `allowedDependencies` only;
- make cycle selection and diagnostic ordering deterministic according to this document;
- cover self-loops, one cycle, multiple SCC cycles, multiple cycles inside one SCC, partial
  allowlists and acyclic graphs;
- prove malformed v4 graph configuration produces AARC001 rather than AARC011;
- extend the real build gate with a temporary cyclic contract that fails specifically with
  AARC011, followed by clean recovery;
- update public schema and diagnostics documentation.

## Non-goals

- no observed-code dependency graph;
- no runtime call graph;
- no transitive dependency inference;
- no requirement that all consumers use a DAG;
- no attempt to enumerate every simple cycle;
- no coupling to project-specific layer names.
