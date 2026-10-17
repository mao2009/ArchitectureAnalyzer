#!/usr/bin/env python3
"""Validate an explicit solo-maintainer sign-off for the CURRENT PR head.

The caller must execute this from a protected base revision, NEVER the PR
checkout. This tool is read-only; it cannot grant a review or add a label.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

SHA = re.compile(r"^[0-9a-f]{40}$")
REPO = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
PHRASE = "SOLO-APPROVED "


def is_approved(pr: dict, comments: list[dict], *, owner: str,
                expected_sha: str, required_label: str = "") -> bool:
    """Require a precisely scoped owner comment, not a PR body or AI assertion."""
    if not SHA.fullmatch(expected_sha):
        return False
    if pr.get("head", {}).get("sha") != expected_sha:
        return False
    if pr.get("state") != "open" or pr.get("base", {}).get("ref") != "main":
        return False
    if required_label and required_label not in {
        entry.get("name") for entry in pr.get("labels", []) if isinstance(entry, dict)
    }:
        return False
    return any(
        isinstance(entry, dict)
        and entry.get("user", {}).get("login", "").casefold() == owner.casefold()
        and entry.get("author_association") == "OWNER"
        and entry.get("body", "").strip() == PHRASE + expected_sha
        for entry in comments
    )


def get_json(path: str, *, token: str) -> object:
    base = "https://api.github.com/"
    request = urllib.request.Request(
        base + path,
        headers={
            "Authorization": "Bearer " + token,
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "solo-maintainer-gate",
        },
    )
    with urllib.request.urlopen(request, timeout=20) as response:
        return json.load(response)


def check(repo: str, number: int, head: str, owner: str, token: str,
          required_label: str = "", fetch=get_json) -> bool:
    if not REPO.fullmatch(repo) or number <= 0 or not SHA.fullmatch(head):
        raise ValueError("invalid repository, PR number or SHA")
    if repo.split("/", 1)[0].casefold() != owner.casefold():
        raise ValueError("owner must match repository owner")
    if not token:
        raise ValueError("read-only GitHub token is required")

    pull = fetch(f"repos/{repo}/pulls/{number}", token=token)
    if not isinstance(pull, dict):
        raise ValueError("invalid PR response")
    # Refuse stale check runs, even if the event payload predates a push.
    if pull.get("head", {}).get("sha") != head:
        return False

    comments: list[dict] = []
    for page in range(1, 51):
        batch = fetch(
            f"repos/{repo}/issues/{number}/comments?per_page=100&page={page}",
            token=token,
        )
        if not isinstance(batch, list):
            raise ValueError("invalid GitHub comments response")
        comments.extend(batch)
        if len(batch) < 100:
            break
    else:
        raise ValueError("more than 5000 comments; cannot establish approval")

    return is_approved(pull, comments, owner=owner, expected_sha=head,
                       required_label=required_label)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Owner-authored SHA-bound PR sign-off")
    parser.add_argument("--repo", required=True)
    parser.add_argument("--pr", required=True, type=int)
    parser.add_argument("--head", required=True)
    parser.add_argument("--owner", required=True)
    parser.add_argument("--require-label", default="")
    args = parser.parse_args(argv)
    try:
        accepted = check(args.repo, args.pr, args.head, args.owner,
                         os.environ.get("GH_TOKEN", ""),
                         required_label=args.require_label)
    except (ValueError, urllib.error.URLError, TimeoutError, OSError) as exc:
        print("Solo sign-off ERROR:", type(exc).__name__, file=sys.stderr)
        return 2
    print("Solo sign-off:", "APPROVED" if accepted else "DENIED")
    return 0 if accepted else 1


if __name__ == "__main__":
    raise SystemExit(main())
