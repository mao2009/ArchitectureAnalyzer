# Gate verification

This directory holds the end-to-end proof that ArchitectureAnalyzer fails a *real* `dotnet build`
— not just an in-memory compilation created by `Microsoft.CodeAnalysis.Testing`. `SampleConsumer`
is a minimal class library that wires the analyzer up exactly the way any other repository would
(`ProjectReference` with `OutputItemType="Analyzer"`, plus an `AdditionalFiles` contract), and it
is committed in a clean, buildable state.

[`verify-gate.sh`](verify-gate.sh) drives the real enforcement and exception scenarios. The
tracked schema-v5 contract contains one exact AARC002 exception. The script first injects
[`Fixtures/ExceptedDependency.cs.txt`](Fixtures/ExceptedDependency.cs.txt) and proves that exact
reviewed source/target pair builds successfully. It then injects
[`Fixtures/Violation.cs.txt`](Fixtures/Violation.cs.txt) on the same forbidden layer direction
but from a different source type and requires AARC002, proving the exception did not suppress the
rule broadly.

The gate then injects
[`Fixtures/AllowlistViolation.cs.txt`](Fixtures/AllowlistViolation.cs.txt) and requires AARC002
from schema-v3 positive allowlisting, temporarily replaces the contract with
[`Fixtures/CyclicContract.json.txt`](Fixtures/CyclicContract.json.txt) and requires AARC011,
then exercises AARC010 strict/ignore coverage with
[`Fixtures/CoverageGap.cs.txt`](Fixtures/CoverageGap.cs.txt). Finally it restores the tracked
schema-v5 contract, removes every injected source and requires a clean build.

The source fixtures use `.cs.txt` so the default compile glob never picks them up. Cleanup restores
the tracked contract even if an intermediate assertion fails.

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
