# Strict diagnostic audit — ArchitectureAnalyzer

Based on [Chris Sellers' review](https://dev.to/loach2009/implementing-you-cant-for-ai-coding-agents-2d1a), the opt-in audit looks for unauthorized suppression of protected diagnostics in C#, `.editorconfig`, MSBuild and GitHub workflows.

## Run against a governed consumer project

```bash
python scripts/strict_diagnostic_audit.py --root path/to/consumer --project MyApp.csproj --ids "AARC001,AARC002,AARC003,AARC004,AARC005,AARC007,AARC010,AARC011,AARC012"
python -m unittest discover -s tests/agent_guard -p 'test_strict_diagnostic_audit.py' -v
python -m unittest discover -s tests/agent_guard -p 'test_analyzer_participation.py' -v
python -m unittest discover -s tests/agent_guard -p 'test_msbuild_live.py' -v
```

`--project` **must identify a governed .csproj**, not merely a solution. The audit evaluates package/analyzer/project references and `AdditionalFiles` for `architecture.contract.json` via `dotnet msbuild -getItem/-getProperty` (MSBuild 17.8+), and rejects missing or disabled wiring. 

Status JSON distinguishes `DENY` (exit 1, suppression/invalid wiring), `AUDIT_PASS` (exit 0, scanner plus evaluated MSBuild wiring), `AUDIT_ONLY` (exit 2, project omitted and no violation seen), and `ERROR` (exit 2). **AUDIT_PASS does not prove the analyzer executed or the code compiled.** Feed the result into a separate real build and trusted CI; agent-facing `scripts/agent_diagnostic_feedback.py --strict` combines an audit, evaluated wiring inspection and a full build.

The scanner recognizes ordinary `#pragma warning disable`, `SuppressMessage` and `UnconditionalSuppressMessage` (including nested `nameof` arguments), `.editorconfig` per-ID, category and global downgrades, `NoWarn`, and explicit build-analyzer disabling. C# comments/strings and XML comments are excluded from their corresponding syntax checks. Inaccessible or symlinked source/config folders cannot silently disappear from the scan.

## Trust boundary and caveats

Static scanning plus evaluated references **is not equivalent to proving an analyzer ran**. Imported/generated/dynamic MSBuild settings, compiler response files, suppressed or skipped diagnostics, unrecognized C# syntax, per-file scoped analyzer configuration, and runtime analyzer loading can still differ. All accepted builds must rerun a trusted actual policy probe / compiler gate and compare expected diagnostics; the repository's guard code and GitHub Actions should be protected by independent review/CODEOWNERS and branch rules. Do not execute an untrusted changed audit script as the sole security check. Build execution itself must occur in a sandbox appropriate for untrusted code; this script does not provide that sandbox.

Exception approvals should be narrow, separately reviewed, auditable and reversible. For intentional architectural exceptions prefer schema-v5 exact justified exceptions and reviewed baselines over global severity suppression.

Related Issue: #85.
