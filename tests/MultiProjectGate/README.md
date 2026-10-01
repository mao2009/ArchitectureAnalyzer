# Multi-project gate

This fixture proves ArchitectureAnalyzer's multi-project behavior with real `dotnet build`
invocations.

## Shape

```text
MultiProjectGate/
├── Directory.Build.props
├── MultiProjectGate.slnx
├── Producer/
│   ├── Producer.csproj
│   └── architecture.contract.json
├── Consumer/
│   ├── Consumer.csproj
│   └── architecture.contract.json
├── Tests/
│   └── Tests.csproj
└── Tooling/
    └── Tooling.csproj
```

`Directory.Build.props` loads the ArchitectureAnalyzer project as an analyzer for **all four**
projects. Only Producer and Consumer include `architecture.contract.json` as `AdditionalFiles`.

That distinction is the behavior under test: analyzer presence is not architecture-policy opt-in.

## What the script proves

[`verify-multi-project.sh`](verify-multi-project.sh) runs five stages:

1. clean solution build succeeds with different Producer/Consumer contracts;
2. an injected Producer `System.Console` use fails with Producer's AARC003 rule;
3. an injected Consumer source reference to a Producer metadata type fails with Consumer's
   cross-project AARC002 rule;
4. Tests and Tooling build successfully despite intentionally using Console/direct project
   references, because neither compilation receives a contract;
5. after injected sources are removed, the complete solution builds cleanly again.

The violation fixtures are stored as `.cs.txt` so normal SDK compile globs never include them.

## Why no project selector exists in the contract

Roslyn analyzers run per compilation and MSBuild already supplies `AdditionalFiles` per project.
The fixture therefore exercises the intended routing boundary directly instead of inventing
project-name or assembly-name filtering in the Architecture Contract.

See [`docs/multi-project-design.md`](../../docs/multi-project-design.md).
