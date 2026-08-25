# Gate verification

This directory holds the end-to-end proof that ArchitectureAnalyzer fails a *real* `dotnet build`
— not just an in-memory compilation created by `Microsoft.CodeAnalysis.Testing`. `SampleConsumer`
is a minimal class library that wires the analyzer up exactly the way any other repository would
(`ProjectReference` with `OutputItemType="Analyzer"`, plus an `AdditionalFiles` contract), and it
is committed in a clean, buildable state.

[`verify-gate.sh`](verify-gate.sh) drives the full cycle: build the clean project (must succeed),
copy [`Fixtures/Violation.cs.txt`](Fixtures/Violation.cs.txt) in as `SampleConsumer/Domain/Violation.cs`
and rebuild (must fail *and* the output must actually contain `AARC002`), then remove it and
rebuild (must succeed again). The fixture is stored with a `.cs.txt` extension so the default
compile glob never picks it up, and the script deletes the injected `.cs` file on exit.

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
