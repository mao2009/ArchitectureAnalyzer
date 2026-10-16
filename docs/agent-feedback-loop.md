# AI agent diagnostic feedback loop — ArchitectureAnalyzer

Based on [Nuno Silva's DEV feedback](https://dev.to/loach2009/implementing-you-cant-for-ai-coding-agents-2d1a): run rules as the coding agent edits, send diagnostics back, and preserve CI as the **independent final gate**.

This opt-in helper is a thin orchestrator for **existing** `dotnet build` and Roslyn diagnostics. It is not a new analyzer, a new rules engine, or evidence that any particular model repairs code rather than suppressing warnings.

## Run

```bash
# Run inside an isolated working tree with restored dependencies:
python scripts/agent_diagnostic_feedback.py --root . --project ArchitectureAnalyzer.sln --backend build --strict

# The build backend also works with any C# solution/project:
python scripts/agent_diagnostic_feedback.py --root path/to/consumer --project Consumer.sln --backend build --strict
```

For PolicySharp's CLI backend inside its own source checkout, `dotnet run --project src/PolicySharp.Cli/PolicySharp.Cli.csproj --no-restore -- check --json ...` is used. Consumers can supply `--policysharp-cli /path/to/policysharp` for an installed compatible executable. The first command expects dependencies already restored; `--restore` explicitly opts into restoring. The script intentionally does not run a shell command or edit source. **Building arbitrary agent-proposed code is not sandboxing; run it in an appropriately isolated environment.**

JSON response: `schemaVersion`, `status` (`ALLOW`, `DENY`, `ERROR`), `exitCode` (0/1/2), `backend`, `validatorExitCode`, `diagnostics`, `auditFindings`, bounded `outputTail`, and repair `guidance`. The script deliberately preserves original diagnostic IDs rather than depending on localized messages. A failed build without an individually parsed diagnostic remains `DENY`; malformed/contradictory PolicySharp CLI JSON is `ERROR`, never ALLOW.

## Agent loop

1. Agent proposes a code-only patch into an isolated candidate workspace.
2. Run this script. Give its JSON to the agent.
3. If `DENY`, repair application code rather than suppress the diagnostic or editing policy wiring. Re-run.
4. A human must approve intentional policy/baseline exceptions separately; generated attempts to loosen guardrails must not be automatically accepted.
5. Rerun the repository's **trusted, full CI** against the candidate commit. Never accept a model's self-reported pass.

Test fixtures exercise one real-looking diagnostic payload, a passing build, a suppression-only bypass, and error exits through a mocked process runner, without calling any LLM. They are regression fixtures, **not** agent behavior measurements. For claims about agent root-cause repairs, record the exact model/tool/version, prompt, attempts and results before publishing comparisons.

## Boundaries

`--strict` uses the opt-in source/configuration scanner described in [agent-guard-strict.md](agent-guard-strict.md). It catches common suppressions but does not prove effective MSBuild/analyzer evaluation; use protected references, full CI and trusted execution together. Default Roslyn semantics are not changed. Run this helper from a pinned trusted checkout in authoritative CI; if a proposed patch changes scripts, CI, policy or project wiring, require external approval. Version fixture expectations and review any changes before merging; rollback is removing the opt-in hook, not weakening shipped diagnostics.

Related issue: #86.