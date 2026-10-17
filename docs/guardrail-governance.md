# Solo Maintainer Mode — ArchitectureAnalyzer

This repository is maintained by one person. Requiring a different PR approver would permanently block ordinary work. The goal here is **not** to claim independent human review: it is to keep the coding agent's edits, reproducible CI checks and the owner's explicit final decision separate.

## GitHub repository configuration (owner-only, manual)

- Under **Settings > Rules > Rulesets**, protect `main`: require a pull request, successful required CI checks and up-to-date validation. Choose the exact names from GitHub's observed status checks; do not guess a missing check name.
- Set required PR approvals to **zero** for this solo workflow and **do not enable "Require review from Code Owners"**. `.github/CODEOWNERS` remains a sensitive-path inventory for future collaborators, not a request for impossible self-approval.
- After the new workflows are integrated into the **protected main branch**, include `Policy integrity / trust-boundary` (PolicySharp) or `Guardrail integrity / protected-change-review` (the other repos) as a required check.
- Keep the default PR `build/test`, packaged-consumer regression, suppression-audit and red/green canary checks as required where available.
- Keep AI/CI credentials limited to **read-only repo access or narrowly scoped code contribution**. They must not be able to add an owner's approval comment, apply approval labels, edit repository rulesets or merge to `main`.
- If the agent uses the same owner identity or access token as the maintainer, GitHub cannot distinguish human approval from agent activity. **This mode does not provide a security boundary in that configuration.** Use separate accounts/credentials and protect the owner's login.

## Exact owner sign-off (only for protected changes)

1. Review all protected changes yourself, including policy files, analyzer references, workflows, hooks, validation scripts and their tests.
2. Read the complete current **PR head SHA** (40 lowercase hexadecimal characters). Do not use the base branch SHA or an abbreviated hash.
3. On that PR, manually post this **exact one-line comment** (replace the placeholder with the actual SHA):

   ```text
   SOLO-APPROVED <40-character-current-pr-head-sha>
   ```

4. The trusted Guardrail integrity check requires the SHA-bound owner comment when protected sources, analyzer rules or CI are changed. After posting the comment, use GitHub Actions > Re-run failed jobs for this PR (or another supported PR workflow event) to evaluate the comment. The comment alone does not trigger this pull_request_target workflow.
5. Confirm the required trusted workflow and regular CI checks passed for the **same current head**. Merge explicitly yourself.

The trusted, read-only `pull_request_target` gate checks GitHub's live PR head and owner-authored issue comments. It accepts only an exact `SOLO-APPROVED <SHA>` line by the repository **OWNER** for the current SHA; a comment in code or the PR description does not count. Any subsequent push changes the SHA, invalidating the prior sign-off. External review is still encouraged when available, but is not a hard dependency for solo maintenance.

## Trust boundary and bootstrap

A PR's own tests are **not** independent of the PR's contents. The read-only guardrail workflow checks out only the protected **base-branch SHA**, fetches the PR head as Git data and never executes PR code with elevated tokens. Its approvals are checked through live GitHub API data.

**Bootstrap caveat:** GitHub executes `pull_request_target` workflows from the base branch. Before this workflow is merged into protected `main`, its new rules are not active on that repository, even if its PR tests are green. The initial merge needs a conscious owner decision using the existing branch protection and GitHub interface. Never call the new mechanism active until that transition completes.

A GitHub ruleset can allow an owner/admin bypass. Keep that exceptional, intentional and recorded with a justification. Never disable required CI or grant an agent admin access merely to obtain a green check.

## Test and rollback

- Automated regression tests cover wrong owner, wrong SHA, missing comment, missing label (PolicySharp), stale PR state, live PR SHA mismatch, pagination and malformed API data.
- The .NET consumer red/green, analyzer-unloaded and suppression fixtures must continue to pass.
- To revoke an approval before merge: remove `policy-approved` for PolicySharp, or remove/edit the owner comment for the other repos; push a new SHA to invalidate previous authorizations.
- Roll back by reverting the solo gate changes in a separately reviewed owner-controlled PR, **not** by silently deleting the gate during an unrelated code change.

Related work: #87.
