# Optional strict diagnostic audit — ArchitectureAnalyzer

Motivated by [Chris Sellers' DEV comment](https://dev.to/loach2009/implementing-you-cant-for-ai-coding-agents-2d1a) about suppression-based bypasses.

This is an **opt-in, conservative source/configuration audit**. It does not change Roslyn's default suppression behavior or package compatibility. It checks source-controlled inputs for `#pragma warning disable`, `SuppressMessage`, `.editorconfig`/global severity downgrades, `NoWarn`, `WarningsNotAsErrors`, analyzer-disabling MSBuild settings, ruleset overrides and obvious workflow disable flags. It uses an independent Python 3.11+ process so a hidden Roslyn diagnostic cannot hide its own bypass.

## Usage

```bash
python scripts/strict_diagnostic_audit.py --root .
python scripts/strict_diagnostic_audit.py --root path/to/consumer --ids "AARC001,AARC002,AARC003,AARC004,AARC005,AARC007,AARC010,AARC011,AARC012" --require-file path/to/expected-input
python -m unittest discover -s tests/agent_guard -p "test_strict_diagnostic_audit.py" -v
```

Output is deterministic JSON: `ALLOW` (exit 0), `DENY` (exit 1), and `ERROR` (exit 2). `findings` contain file, line, protected IDs, and bypass kind. The default ID set is `AARC001`, `AARC002`, `AARC003`, `AARC004`, `AARC005`, `AARC007`, `AARC010`, `AARC011`, `AARC012`. The advisory warning IDs AARC006/AARC008/AARC009 are intentionally excluded from strict defaults. Override `--ids` for a consumer's enforced IDs; in particular, intentionally configurable/advisory rules must not be elevated silently. `--exclude` supports reviewed file glob exceptions, but avoid excluding production sources.

**Important limitations:** This scanner does **not** evaluate imported/generated MSBuild properties, dynamically created .editorconfig, response files, category-specific severity, preprocessor reachability, package removal, or every possible suppression syntax. False positives are possible (conditional XML settings, string literals). It is a guardrail, **not a security boundary on its own**. A complete enforcement gate additionally needs a trusted workflow and approved analyzer/contract references, a full solution build, actual package/configuration evaluation, reviewed exceptions, and branch protection. Never run an untrusted PR's modifiable copy of the audit script as the sole authoritative security check; run a trusted, pinned copy or independently verify its integrity.

To approve an intentional exception, review the policy/configuration change separately in a human-controlled PR (for ArchitectureAnalyzer, prefer schema-v5 exact justified exceptions and reviewed baselines; for PureSharp, preserve the intended opt-in severity contract). Do not teach an agent to silence errors. Rollback: remove the opt-in invocation; package default semantics are unchanged.

Related issue: #85.