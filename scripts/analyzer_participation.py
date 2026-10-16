"""Fail-closed inspection of evaluated .NET analyzer wiring (MSBuild 17.8+).

This verifies declared, evaluated analyzer references and policy inputs. It is
necessary but not sufficient to prove that an analyzer actually executed.
Always follow it with a real build and independently trusted CI.
"""
from __future__ import annotations

import json
import subprocess
from pathlib import Path
from typing import Callable


def verify_participation(
    root: Path,
    project: Path,
    analyzer: str,
    required_additional: str | None = None,
    *,
    internal_cli: bool = False,
    dotnet: str = "dotnet",
    executor: Callable = subprocess.run,
    timeout: int = 90,
) -> list[str]:
    """Return failure reasons. Empty means evaluated wiring appears enabled."""
    try:
        root = root.resolve(strict=True)
        target = (root / project).resolve(strict=True)
        if not root.is_dir() or not target.is_file() or not target.is_relative_to(root):
            return ["governed project must exist inside the candidate workspace"]
        if target.suffix.lower() != ".csproj":
            return ["a governed .csproj is required; specify --guard-project for a solution"]
        cmd = [
            dotnet, "msbuild", str(target), "-nologo",
            "-getProperty:RunAnalyzers,RunAnalyzersDuringBuild",
            "-getItem:PackageReference,ProjectReference,Analyzer,AdditionalFiles",
        ]
        result = executor(cmd, cwd=root, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=timeout, check=False)
        if result.returncode:
            return ["MSBuild evaluation failed (exit " + str(result.returncode) + ")"]
        payload = json.loads(result.stdout)
        properties = payload["Properties"]
        items = payload["Items"]
        if not isinstance(items, dict) or not isinstance(properties, dict):
            raise ValueError("invalid MSBuild result shapes")
    except (OSError, ValueError, KeyError, TypeError, subprocess.TimeoutExpired) as exc:
        return ["MSBuild evaluation cannot be verified: " + type(exc).__name__]

    failures: list[str] = []
    for key in ("RunAnalyzers", "RunAnalyzersDuringBuild"):
        if str(properties.get(key, "")).strip().lower() == "false":
            failures.append(key + " is disabled")

    def entries(name: str) -> list[dict]:
        raw = items.get(name)
        return raw if isinstance(raw, list) and all(isinstance(x, dict) for x in raw) else []

    if not internal_cli:
        hint = analyzer.lower()
        found = False
        for item in entries("PackageReference") + entries("Analyzer"):
            identity = str(item.get("Identity", "")).replace("\\", "/").lower()
            if hint in identity:
                found = True
                break
        for item in entries("ProjectReference"):
            identity = str(item.get("Identity", "")).replace("\\", "/").lower()
            output_type = str(item.get("OutputItemType", "")).lower()
            if hint in identity and output_type == "analyzer":
                found = True
                break
        if not found:
            failures.append("required analyzer reference not present in evaluated MSBuild items: " + analyzer)

    if required_additional:
        expected = required_additional.lower()
        additional = [
            str(item.get("Identity", "")).replace("\\", "/").lower().split("/")[-1]
            for item in entries("AdditionalFiles")
        ]
        if expected not in additional:
            # PolicySharp's own CLI also locates a policy next to the project
            # when it is not declared as an AdditionalFile.
            if not (internal_cli and (target.parent / required_additional).is_file()):
                failures.append("required AdditionalFiles item not evaluated: " + required_additional)
    return failures
