# Gate verification

This directory holds the end-to-end proof that ArchitectureAnalyzer fails a *real* `dotnet build`
— not just an in-memory compilation created by `Microsoft.CodeAnalysis.Testing`. `SampleConsumer`
is a minimal class library that wires the analyzer up exactly the way any other repository would
(`ProjectReference` with `OutputItemType="Analyzer"`, plus an `AdditionalFiles` contract), and it
is committed in a clean, buildable state.

[`verify-gate.sh`](verify-gate.sh) drives three real enforcement scenarios. It first injects
[`Fixtures/Violation.cs.txt`](Fixtures/Violation.cs.txt) and requires AARC002 from an explicit
`forbiddenDependencies` edge. It then injects
[`Fixtures/AllowlistViolation.cs.txt`](Fixtures/AllowlistViolation.cs.txt) and requires AARC002
from a schema-v3 `allowedDependencies` violation with no matching explicit deny edge. Finally it
injects [`Fixtures/CoverageGap.cs.txt`](Fixtures/CoverageGap.cs.txt) into a namespace outside
every declared root and requires AARC010 while `unclassifiedCode` is `"error"`. The script
temporarily switches coverage to `"ignore"`, proves the identical coverage-gap source then builds
successfully, restores the strict contract, removes all injected files, and requires a final clean
build.

Both fixtures use `.cs.txt` so the default compile glob never picks them up. Cleanup restores the
tracked contract even if an intermediate assertion fails.

It exists because "the tests pass" is a weaker claim than the one this project makes; see
[`../../docs/design.md` §2](../../docs/design.md#2-why-compiler-time-enforcement-specifically) and
[§9](../../docs/design.md#9-what-this-analyzer-guarantees-and-what-it-does-not). CI runs the
script as its own step so a reader of the Actions log can watch the enforcement happen.
`SampleConsumer` is deliberately excluded from `ArchitectureAnalyzer.sln` so that intentionally
breaking it never breaks the solution build.

```bash
chmod +x tests/GateVerification/verify-gate.sh
tests/GateVerification/verify-gate.sh
```
