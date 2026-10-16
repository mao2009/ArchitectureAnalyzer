# Guardrail ownership and deployment controls — ArchitectureAnalyzer

The new guard scripts, tests and workflow are defense-in-depth, **not a self-authenticating trust boundary**. Source-control access lets an agent alter Python validators or CI definitions in a PR. A green result from a PR-modifiable script is not approval of its own enforcement.

## Required repository configuration (GitHub Settings, human-owned)

1. Protect `main` with a ruleset requiring a pull request before merge, up-to-date required status checks (verify the exact check names in GitHub), and at least one approval from a reviewer **other than the PR author**. Dismiss stale approvals when new commits arrive.
2. Enable **Require review from Code Owners** for `.github/CODEOWNERS`. The checked-in owner `@mao2009` is a fallback owner of these paths, **not independent approval for that same account's changes**. Appoint a real second maintainer with write permission as a code owner before asserting independent approvals.
3. Include the following checks as required where they exist: `CI`, `Agent guard regression tests / python-guard-tests`. `pull_request` and `workflow_dispatch` tests alone do **not** configure branch protection.
4. Do not let an agent add approval labels, edit GitHub rulesets, or change enforcement scripts as part of an ordinary application-code PR. Put guardrail changes in a separate PR with an explicit rollback plan.
5. Invalidate the approval on the next pushed SHA and re-run the **actual C# package-consumer canary** and full project build. Canary must fail on a deliberately injected rule violation and when the analyzer is missing; a passing build alone is not enough.

Require an independent maintainer review for protected analyzer/contract/workflow changes; this repo currently has no PolicySharp-style runtime approval query.

## What CI proves and does not prove

The agent-guard workflow exercises offline parser tests and live MSBuild project evaluation. Agent feedback PRs also execute real C# consumer RED (prohibited code), GREEN (approved code fix), suppression and analyzer-removal tests. Those test programs assert observed diagnostic IDs, not merely the process exit code.

**Trust limitation:** GitHub workflows committed on an untrusted PR branch are still under that branch's control. To claim untrusted-agent containment, enforce these checks using a pinned validator from a separately protected ref or trusted workflow after the validator baseline has been reviewed and merged, and keep all PR code execution on isolated, least-privilege runners. Never execute PR code with a write-enabled `pull_request_target` token or secrets.

## Operator runbook

- Review the exact diff of protected paths, new tests, validation ID changes and rollback steps.
- Confirm genuine code changes are reviewed independently of changes to policy/configuration.
- Observe all required green checks from the reviewed head SHA.
- Use a separate qualified reviewer and GitHub required-code-owner review. Never equate the repository author's own approval with an independent review.
- Merge strict-audit PR before retargeting/merging the stacked feedback PR. If this cannot be met, leave both as Draft; do not remove protections merely to turn CI green.

Related PR/issue: #87.
