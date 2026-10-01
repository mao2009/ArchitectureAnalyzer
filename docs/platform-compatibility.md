# Platform and compiler-host compatibility

ArchitectureAnalyzer is a Roslyn analyzer packaged for compiler-time use. Compatibility therefore
depends on the analyzer API surface, the compiler host that loads it, the .NET SDK used for command
line builds, and the operating system path/file behavior.

## CI-validated support envelope

The following combinations are continuously validated in GitHub Actions:

| Operating system | .NET SDK | Consumer path | What is proven |
|---|---|---|---|
| Ubuntu `ubuntu-latest` | 10.0.302 | local packed NuGet package | restore, analyzer loading, `AdditionalFiles`, clean build, AARC002 failure, recovery build |
| Windows `windows-latest` | 10.0.302 | local packed NuGet package | restore, analyzer loading, `AdditionalFiles`, clean build, AARC002 failure, recovery build |

The analyzer project itself targets `netstandard2.0` and compiles against
`Microsoft.CodeAnalysis.CSharp` 4.12.0. That is the API surface used to build the analyzer; the
actual command-line compiler host in the E2E matrix comes from the pinned .NET SDK above.

A combination not listed here is **unvalidated**, not automatically incompatible. In particular,
older .NET SDKs/Roslyn hosts, macOS, and IDE-specific hosts are not promised until they are added
to this matrix and exercised in CI.

## What the cross-platform E2E checks

Both operating systems run the same consumer scenario through native tooling:

1. pack the current analyzer source into a temporary local NuGet feed,
2. restore a standalone consumer from only that feed with an isolated package cache,
3. build the clean consumer successfully,
4. confirm `ArchitectureAnalyzer.dll` is not a runtime output dependency,
5. inject a Domain -> Application violation,
6. require a real `dotnet build` failure containing `AARC002`,
7. remove the violation and require the build to recover.

The consumer supplies `architecture.contract.json` through `AdditionalFiles`. Because the same
UTF-8 contract is consumed through native Linux and Windows paths, this matrix also protects the
contract discovery/path boundary. Unit tests separately cover mixed `/` and `\\` path separator
handling for duplicate-contract discovery.

An analyzer load failure cannot pass silently in this test: if the package asset is missing, the
contract is not delivered, or Roslyn fails to execute the analyzer, the injected violation does
not produce `AARC002` and the job fails.

## Compatibility policy

- CI-pinned combinations above are the supported, continuously verified baseline.
- Raising the analyzer's Roslyn API dependency or the pinned SDK requires updating this document
  and preserving at least one Linux and one Windows real-consumer path.
- New operating systems or older compiler hosts become supported only after a deterministic CI
  lane exists for them.
- No claim is made that every host capable of loading `netstandard2.0` analyzers is supported.
- Package/runtime separation is part of compatibility: the analyzer must remain an analyzer asset,
  not a runtime dependency of consumer applications.
