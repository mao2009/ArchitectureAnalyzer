# Multi-project contract scoping design

Status: accepted design target for issue #23; integration coverage is tracked by #4.

## Goal

ArchitectureAnalyzer must behave predictably in a solution that contains multiple C# projects with
different architecture responsibilities.

The design keeps the analyzer's existing unit of work:

> **one Roslyn compilation -> zero or one Architecture Contract**

No solution-wide contract coordinator is introduced.

## Why project identity is not added to the contract

A Roslyn analyzer executes independently for each compilation. MSBuild already decides which
`AdditionalFiles` belong to that compilation.

ArchitectureAnalyzer therefore does **not** add a project-name / project-path selector to the
Architecture Contract. Project identity would be a second, partially redundant routing system on
top of MSBuild and would create unstable policy keys when projects or assembly names are renamed.

The routing rule is instead explicit and simple:

- a governed project explicitly includes exactly one `architecture.contract.json` as
  `AdditionalFiles`;
- a project with no matching contract is intentionally out of scope and the analyzer no-ops;
- a project supplied more than one matching contract keeps the existing fail-safe AARC001 behavior.

The analyzer package/reference may be present in every project. **Contract presence, not analyzer
presence, opts a compilation into architecture enforcement.**

## Recommended MSBuild shape

A repository may distribute the analyzer centrally:

```xml
<!-- Directory.Build.props -->
<Project>
  <ItemGroup>
    <ProjectReference
      Include="$(MSBuildThisFileDirectory)tools/ArchitectureAnalyzer/ArchitectureAnalyzer.csproj"
      OutputItemType="Analyzer"
      ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
```

Governed projects then opt in independently:

```xml
<!-- Core/Core.csproj -->
<ItemGroup>
  <AdditionalFiles Include="architecture.contract.json" />
</ItemGroup>
```

```xml
<!-- App/App.csproj -->
<ItemGroup>
  <AdditionalFiles Include="architecture.contract.json" />
</ItemGroup>
```

A test/tooling project that should not be governed simply omits the `AdditionalFiles` item even
though the analyzer binary is loaded.

This avoids repository-specific path logic inside the analyzer.

## Contract discovery in a solution

Contracts are never discovered by walking the solution, repository or parent directories.

For each compilation independently:

1. inspect only `AnalyzerOptions.AdditionalFiles`;
2. select files whose basename is exactly `architecture.contract.json`;
3. zero matches -> intentional no-op;
4. one match -> parse and enforce that contract;
5. more than one match -> fail safe with AARC001.

Two projects may each reference different files with the same basename. They are in separate
compilations, so they do not conflict and cannot see each other's AdditionalFiles.

## Cross-project type references

A contract belongs to the **source compilation**, not to the target assembly.

If App references a public type from Core, App's contract classifies both:

- the App source type that owns the reference;
- the referenced Core metadata type.

Layer classification remains marker-first / namespace-fallback exactly as today.

This means an App contract can express rules about direct references into Core namespaces without
requiring Core to reuse App's contract.

Core is separately governed by Core's own contract while Core itself is compiled.

## Tests, analyzer projects and tooling projects

There is no magic project-kind detection.

ArchitectureAnalyzer does not inspect project filenames, SDK names, test-framework references or
assembly suffixes such as `.Tests`.

Instead:

- **governed test project**: explicitly provide a contract;
- **excluded test project**: do not provide a contract;
- **governed tooling/analyzer project**: explicitly provide a contract;
- **excluded tooling/analyzer project**: do not provide a contract.

This is deterministic and avoids heuristics that could silently skip a production project whose
name happens to resemble a test/tool project.

## Generated code

Generated-code handling remains source-tree operational configuration and is independent of
multi-project scoping.

A project being governed does not imply that generated paths are analyzed. Existing
`generated_code` / `skip_generated_code` behavior continues to control that.

## Shared contract files

Multiple projects may intentionally point at the **same physical contract file**:

```xml
<AdditionalFiles Include="$(MSBuildThisFileDirectory)..\architecture.contract.json" />
```

That is valid. Each compilation receives its own AdditionalText instance and parses the contract
once for that compilation.

Sharing is an MSBuild/repository decision; the analyzer does not infer or require it.

## Directory.Build.props caution

A repository can accidentally include a contract globally in `Directory.Build.props`:

```xml
<AdditionalFiles Include="$(MSBuildThisFileDirectory)architecture.contract.json" />
```

If it does, MSBuild has explicitly supplied that contract to every importing compilation and the
analyzer will correctly enforce it there.

ArchitectureAnalyzer does not attempt to guess that this was accidental.

The recommended pattern is:

- centralize the **analyzer reference** if convenient;
- keep **contract AdditionalFiles inclusion explicit per governed project**, or use a clearly named
  opt-in MSBuild property/condition owned by the repository.

For example:

```xml
<PropertyGroup>
  <UseArchitectureContract>true</UseArchitectureContract>
</PropertyGroup>

<ItemGroup Condition="'$(UseArchitectureContract)' == 'true'">
  <AdditionalFiles Include="architecture.contract.json" />
</ItemGroup>
```

That keeps routing reviewable in normal MSBuild rather than duplicating project scoping inside the
contract schema.

## Failure isolation

One project's invalid contract must not affect another compilation.

Examples:

- Core has malformed contract -> Core reports AARC001; App's separately valid contract is still
  analyzed normally when App compiles.
- Tests have no contract -> Tests no-op even if App/Core contracts exist elsewhere in the solution.
- App accidentally includes two contracts -> App reports AARC001; Core remains unaffected.

No static/global mutable contract cache may cross compilation boundaries.

## Integration fixture required by #4

The implementation/integration PR should add a real multi-project fixture with at least:

```text
MultiProjectGate/
├── Directory.Build.props          # analyzer reference available to all projects
├── Producer/
│   ├── Producer.csproj
│   └── architecture.contract.json # Producer-specific contract
├── Consumer/
│   ├── Consumer.csproj            # ProjectReference -> Producer
│   └── architecture.contract.json # Consumer-specific cross-project contract
├── Tests/
│   └── Tests.csproj               # analyzer loaded, no contract
└── Tooling/
    └── Tooling.csproj             # analyzer loaded, no contract
```

The fixture must prove:

1. clean solution build succeeds;
2. Producer-specific violation fails Producer with the expected diagnostic;
3. Consumer direct reference to a Producer type can be governed by Consumer's contract and fails
   with the expected diagnostic when a violation is injected;
4. Tests and Tooling contain code that would violate a governed project's policy but remain silent
   because they have no contract;
5. after injected violations are removed, the whole solution builds cleanly again.

The fixture may use project references to the analyzer because packaged-consumer behavior is already
covered by the separate Linux/Windows NuGet E2E.

## Documentation contract

Public documentation should state clearly:

- ArchitectureAnalyzer is **compilation scoped**, not solution scoped;
- contract routing is owned by MSBuild `AdditionalFiles`;
- analyzer presence alone does not enable architecture rules;
- no-contract compilations intentionally no-op;
- there is no automatic test/tooling exclusion heuristic;
- cross-project referenced types can still be classified by the source compilation's contract.

## Non-goals

- no repository-wide contract discovery;
- no solution-wide orchestration service;
- no project-name/assembly-name selector in schema v6;
- no automatic test/analyzer/tooling detection;
- no replacement of namespace/marker layer classification;
- no attempt to analyze native projects that do not produce a Roslyn C# compilation.
