#!/usr/bin/env python3
"""Deterministic AI-facing build/policy feedback; never edits source or policy.

Run against an isolated candidate workspace. Independently rerun trusted full
CI rather than accepting an agent's claim that diagnostics were fixed.
"""
from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Callable

# Use the adjacent strict audit in the stacked security change.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from strict_diagnostic_audit import DEFAULT_IDS, audit_tree, parse_ids
from analyzer_participation import verify_participation

BUILD_DIAGNOSTIC = re.compile(
    r"^(?P<source>.+?)\((?P<line>\d+),(?P<column>\d+)(?:,\d+,\d+)?\):\s*"
    r"(?P<severity>error|warning)\s+(?P<id>[A-Za-z]+\d{3,4}):\s*"
    r"(?P<message>.*?)(?:\s+\[[^\]]+\])?\s*$", re.I,
)
GUIDANCE = (
    "Change application code to satisfy the active policy. Do not silence diagnostics, "
    "edit analyzer wiring or policy files, or broaden approved exceptions. "
    "Request human review for any intentional policy change."
)


def parse_build_diagnostics(output: str) -> list[dict]:
    findings: list[dict] = []
    for line in output.splitlines():
        match = BUILD_DIAGNOSTIC.match(line.strip())
        if not match:
            continue
        findings.append({
            "id": match.group("id").upper(),
            "severity": match.group("severity").lower(),
            "path": match.group("source"),
            "line": int(match.group("line")),
            "column": int(match.group("column")),
            "message": match.group("message").strip(),
        })
    # dotnet sometimes repeats diagnostics in its final summary.
    return sorted((dict(items) for items in {tuple(sorted(x.items())) for x in findings}),
                  key=lambda item: (item["path"], item["line"], item["column"], item["id"]))


def _error(message: str) -> dict:
    return {"schemaVersion": 1, "status": "ERROR", "exitCode": 2, "error": message,
            "diagnostics": [], "auditFindings": [], "guidance": GUIDANCE}


def run_check(
    root: Path,
    project: Path,
    protected_ids: frozenset[str],
    *,
    backend: str = "build",
    strict: bool = False,
    restore: bool = False,
    timeout: int = 180,
    policysharp_cli: str | None = None,
    guard_project: Path | None = None,
    dotnet_path: str | None = None,
    executor: Callable = subprocess.run,
) -> dict:
    try:
        root = root.resolve(strict=True)
        if not root.is_dir():
            return _error("root is not a directory")
        target = (root / project).resolve(strict=True)
        if not target.is_file() or not target.is_relative_to(root):
            return _error("target must be a file inside the workspace")
        if target.suffix.lower() not in (".sln", ".slnx", ".csproj", ".fsproj", ".vbproj"):
            return _error("target must be a solution or project")
        if not protected_ids:
            return _error("no protected diagnostic IDs configured")
        if timeout < 1:
            return _error("timeout must be positive")
        audit = audit_tree(root, protected_ids) if strict else []
    except (OSError, ValueError) as exc:
        return _error(type(exc).__name__ + ": " + str(exc))

    dotnet = dotnet_path or shutil.which("dotnet")
    # A real compiler build is required even with an installed CLI executable.
    if not dotnet:
        return _error("dotnet SDK not found (no validation performed)")

    if strict:
        governed = guard_project or project
        wiring = verify_participation(
            root, governed, "loach.ArchitectureAnalyzer", "architecture.contract.json",
            internal_cli=(backend == "policysharp"), dotnet=dotnet, executor=executor,
            timeout=timeout,
        )
        if wiring:
            return _error("enforcement participation is unverified: " + "; ".join(wiring))

    if backend == "build":
        command = [dotnet, "build", str(target), "--nologo", "-v:q",
                   "-p:RunAnalyzersDuringBuild=true"]
        if not restore:
            command.append("--no-restore")
    elif backend == "policysharp":
        if policysharp_cli:
            command = [policysharp_cli, "check", "--json", str(target)]
        else:
            cli_project = root / "src" / "PolicySharp.Cli" / "PolicySharp.Cli.csproj"
            if not cli_project.is_file():
                return _error("PolicySharp CLI project not found; set --policysharp-cli")
            command = [dotnet, "run", "--no-build", "--project", str(cli_project)]
            if not restore:
                command.append("--no-restore")
            command.extend(["--", "check", "--json", str(target)])
    else:
        return _error("unknown backend")
    try:
        # The PolicySharp CLI checks policy diagnostics but not all compiler errors.
        if backend == "policysharp":
            build_command = [dotnet, "build", str(target), "--nologo", "-v:q",
                             "-p:RunAnalyzers=true", "-p:RunAnalyzersDuringBuild=true"]
            if not restore:
                build_command.append("--no-restore")
            built = executor(build_command, cwd=root, capture_output=True, text=True,
                             encoding="utf-8", errors="replace", timeout=timeout, check=False)
            if built.returncode != 0:
                return {
                    "schemaVersion": 1, "status": "DENY", "exitCode": 1,
                    "backend": backend, "validatorExitCode": built.returncode,
                    "diagnostics": parse_build_diagnostics(
                        (built.stdout or "") + "\n" + (built.stderr or "")
                    ), "auditFindings": audit, "guidance": GUIDANCE,
                    "outputTail": ((built.stdout or "") + "\n" + (built.stderr or ""))[-4096:],
                }
        completed = executor(command, cwd=root, capture_output=True, text=True,
                             encoding="utf-8", errors="replace", timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        return _error(type(exc).__name__ + ": " + str(exc))

    combined = (completed.stdout or "") + "\n" + (completed.stderr or "")
    if backend == "policysharp":
        try:
            payload = json.loads(completed.stdout)
            cli_code = payload["exitCode"]
            entries = payload["diagnostics"]
            if type(cli_code) is not int or not isinstance(entries, list):
                raise ValueError("invalid PolicySharp result schema")
            if any(not isinstance(entry, dict) or not isinstance(entry.get("id"), str) for entry in entries):
                raise ValueError("invalid PolicySharp diagnostics")
            if completed.returncode != cli_code:
                raise ValueError("PolicySharp CLI exit status and JSON disagree")
            diagnostics = entries
            build_code = cli_code
        except (ValueError, KeyError, TypeError) as exc:
            return _error("invalid PolicySharp CLI JSON: " + str(exc))
    else:
        diagnostics = parse_build_diagnostics(combined)
        build_code = completed.returncode

    status = "DENY" if audit or build_code != 0 else "ALLOW" if strict else "UNVERIFIED"
    return {
        "schemaVersion": 1,
        "status": status,
        "exitCode": 1 if status == "DENY" else 0 if status == "ALLOW" else 2,
        "backend": backend,
        "target": target.relative_to(root).as_posix(),
        "validatorExitCode": build_code,
        "diagnostics": diagnostics,
        "auditFindings": audit,
        "outputTail": combined[-4096:] if backend == "build" else (completed.stderr or "")[-4096:],
        "guidance": GUIDANCE,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Feed validation results back to a coding agent")
    parser.add_argument("--root", type=Path, default=Path("."))
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--ids", action="append", help="Override required IDs (comma separated)")
    parser.add_argument("--backend", choices=("build", "policysharp"), default="build")
    parser.add_argument("--strict", action="store_true", help="Require MSBuild participation and suppression audit")
    parser.add_argument("--guard-project", type=Path, help="Governed .csproj for a solution target")
    parser.add_argument("--restore", action="store_true", help="Allow restore (off by default)")
    parser.add_argument("--timeout", type=int, default=180)
    parser.add_argument("--policysharp-cli", help="Installed PolicySharp executable path (no shell)")
    args = parser.parse_args(argv)
    ids = parse_ids(args.ids) if args.ids else frozenset(DEFAULT_IDS)
    result = run_check(args.root, args.project, ids, backend=args.backend,
                       strict=args.strict, restore=args.restore, timeout=args.timeout,
                       policysharp_cli=args.policysharp_cli,
                       guard_project=args.guard_project)
    print(json.dumps(result, ensure_ascii=False, sort_keys=True, separators=(",", ":")))
    return result["exitCode"]


if __name__ == "__main__":
    sys.exit(main())
