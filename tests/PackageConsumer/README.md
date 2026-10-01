# Packaged consumer E2E

This directory verifies the public NuGet/MSBuild consumption boundary rather than the analyzer
test harness or a source-tree project reference.

`verify-package-consumer.sh` (Linux) and `verify-package-consumer.ps1` (Windows):

1. packs the current `src/ArchitectureAnalyzer` source into a temporary local NuGet feed,
2. clears the fixture's `bin`/`obj` output and restores `Consumer` from **only** that local feed,
   using an isolated `NUGET_PACKAGES` directory so an older globally cached package cannot win,
3. builds the clean consumer successfully and asserts `ArchitectureAnalyzer.dll` is not copied
   into the consumer runtime output,
4. injects `Fixtures/Violation.cs.txt` as a real `.cs` file and requires `dotnet build` to fail
   with `AARC002`,
5. removes the violation and requires the build to pass again.

The consumer project contains a normal `PackageReference` plus
`<AdditionalFiles Include="architecture.contract.json" />`; it has no `ProjectReference` to the
analyzer source tree. The package version is a local prerelease (`0.0.0-e2e` by default), so this
test does not depend on whatever version happens to be published on NuGet.org.

Run the platform-native verifier from anywhere inside a checkout:

```bash
bash tests/PackageConsumer/verify-package-consumer.sh
```

```powershell
./tests/PackageConsumer/verify-package-consumer.ps1
```

CI executes the same packaged-consumer scenario on both `ubuntu-latest` and `windows-latest`.
See [`../../docs/platform-compatibility.md`](../../docs/platform-compatibility.md) for the
supported compiler/OS matrix.

The older `tests/GateVerification` fixture intentionally remains. It is useful for fast
project-reference development checks, while this directory catches packaging, analyzer-asset,
restore and `AdditionalFiles` integration regressions.
