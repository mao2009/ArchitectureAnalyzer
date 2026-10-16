#!/usr/bin/env python3
"""Opt-in, independent audit of common Roslyn diagnostic suppression paths.

This is a conservative repository-source audit, NOT a proof of effective MSBuild
properties or arbitrary imported/generated configuration. Run the copy from a
trusted CI revision and separately run the actual full solution build.
"""
from __future__ import annotations

import argparse
import fnmatch
import json
import os
import re
import sys
from pathlib import Path

DEFAULT_IDS = ("AARC001", "AARC002", "AARC003", "AARC004", "AARC005", "AARC006", "AARC007", "AARC008", "AARC009", "AARC010", "AARC011", "AARC012",)
SKIP_DIRS = frozenset({
    ".git", ".venv", ".idea", ".vs", "__pycache__", "bin", "obj",
    "node_modules", "packages", "artifacts", "TestResults", "out",
})
XML_SUFFIXES = frozenset({".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".ruleset"})
CONFIG_NAMES = frozenset({".editorconfig", ".globalconfig"})
DOWNGRADED = frozenset({"none", "silent", "hidden", "suggestion", "info", "warning"})
PRAGMA = re.compile(r"^\s*#\s*pragma\s+warning\s+disable(?:\s+(.*))?$", re.I)
ATTRIBUTE = re.compile(
    r"(?<![\w])(?:global::)?(?:[\w]+\.)*(?:Unconditional)?"
    r"SuppressMessage(?:Attribute)?\s*\(([^)]*)\)", re.I | re.S,
)
SEVERITY = re.compile(
    r"^\s*dotnet_diagnostic\.([A-Za-z][A-Za-z0-9]*)\.severity\s*=\s*([A-Za-z]+)\b",
    re.I,
)
GLOBAL_SEVERITY = re.compile(
    r"^\s*dotnet_analyzer_diagnostic\.severity\s*=\s*([A-Za-z]+)\b", re.I,
)
XML_SETTING = re.compile(
    r"<(?P<key>NoWarn|WarningsNotAsErrors|RunAnalyzers|RunAnalyzersDuringBuild)"
    r"\b[^>]*>(?P<value>.*?)</(?P=key)\s*>", re.I | re.S,
)
RULESET = re.compile(
    r"<Rule\b(?P<attrs>[^>]+)>?", re.I | re.S,
)
ATTR_VALUE = re.compile(r"([A-Za-z]+)\s*=\s*['\"]([^'\"]*)['\"]")
GITHUB_BUILD_DISABLE = re.compile(
    r"(?:RunAnalyzers|RunAnalyzersDuringBuild)\s*=\s*false\b", re.I,
)


def parse_ids(values: list[str]) -> frozenset[str]:
    return frozenset(
        item.upper() for value in values
        for item in re.split(r"[\s,;]+", value.strip())
        if item
    )


def affected_ids(fragment: str, required: frozenset[str], *, empty_means_all: bool = False) -> list[str]:
    fragment = fragment.split("//", 1)[0].strip()
    if not fragment:
        return sorted(required) if empty_means_all else []
    tokens = set(re.findall(r"[A-Za-z][A-Za-z0-9]*|\*", fragment.upper()))
    if "*" in tokens or "ALL" in tokens:
        return sorted(required)
    return sorted(tokens & required)


def _finding(path: str, line: int, ids: list[str], kind: str, detail: str) -> dict:
    return {"path": path, "line": line, "diagnosticIds": ids, "kind": kind, "detail": detail}


def audit_content(relative_path: str, content: str, required: frozenset[str]) -> list[dict]:
    """Return deterministic findings; intentionally does not parse arbitrary MSBuild."""
    path = Path(relative_path)
    name = path.name.lower()
    suffix = path.suffix.lower()
    results: list[dict] = []
    lines = content.splitlines()

    if suffix == ".cs":
        for index, line in enumerate(lines, 1):
            pragma = PRAGMA.match(line)
            if pragma:
                targets = affected_ids(pragma.group(1) or "", required, empty_means_all=True)
                if targets:
                    results.append(_finding(relative_path, index, targets, "pragma", "warning disable"))
        for match in ATTRIBUTE.finditer(content):
            targets = affected_ids(match.group(1), required)
            if targets:
                results.append(_finding(
                    relative_path, content.count("\n", 0, match.start()) + 1,
                    targets, "suppress-attribute", "SuppressMessage attribute",
                ))

    if name in CONFIG_NAMES:
        for index, line in enumerate(lines, 1):
            if line.lstrip().startswith(("#", ";")):
                continue
            match = SEVERITY.match(line)
            if match and match.group(1).upper() in required and match.group(2).lower() in DOWNGRADED:
                results.append(_finding(
                    relative_path, index, [match.group(1).upper()],
                    "severity-override", match.group(2).lower(),
                ))
            global_match = GLOBAL_SEVERITY.match(line)
            if global_match and global_match.group(1).lower() in DOWNGRADED:
                results.append(_finding(
                    relative_path, index, sorted(required),
                    "global-severity-override", global_match.group(1).lower(),
                ))

    if suffix in XML_SUFFIXES:
        for match in XML_SETTING.finditer(content):
            key = match.group("key").lower()
            value = match.group("value").strip()
            if key in ("runanalyzers", "runanalyzersduringbuild"):
                targets = sorted(required) if value.lower() == "false" else []
            else:
                targets = affected_ids(value, required)
            if targets:
                results.append(_finding(
                    relative_path, content.count("\n", 0, match.start()) + 1,
                    targets, "msbuild-" + key, value[:120],
                ))
        if suffix == ".ruleset":
            for match in RULESET.finditer(content):
                attrs = {k.lower(): v for k, v in ATTR_VALUE.findall(match.group("attrs"))}
                rule_id = attrs.get("id", "").upper()
                action = attrs.get("action", "").lower()
                if rule_id in required and action in ("none", "hidden", "info", "warning"):
                    results.append(_finding(
                        relative_path, content.count("\n", 0, match.start()) + 1,
                        [rule_id], "ruleset-override", action,
                    ))

    if suffix in (".yml", ".yaml") and "/.github/workflows/" in "/" + relative_path:
        for index, line in enumerate(lines, 1):
            if not line.lstrip().startswith("#") and GITHUB_BUILD_DISABLE.search(line):
                results.append(_finding(
                    relative_path, index, sorted(required),
                    "workflow-disables-analyzers", "RunAnalyzers/RunAnalyzersDuringBuild=false",
                ))
    return results


def audit_tree(
    root: Path,
    required: frozenset[str],
    excludes: tuple[str, ...] = (),
    require_files: tuple[str, ...] = (),
) -> list[dict]:
    root = root.resolve(strict=True)
    if not root.is_dir():
        raise ValueError("root is not a directory")
    findings: list[dict] = []
    for required_file in require_files:
        rel = Path(required_file)
        if rel.is_absolute() or ".." in rel.parts:
            raise ValueError("required paths must be relative and remain inside root")
        if not (root / rel).is_file():
            findings.append(_finding(required_file, 0, sorted(required), "missing-required-file", "required enforcement input absent"))
    for directory, dirs, files in os.walk(root, followlinks=False):
        dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS and not (Path(directory) / d).is_symlink())
        for filename in sorted(files):
            path = Path(directory) / filename
            relative = path.relative_to(root).as_posix()
            if any(fnmatch.fnmatch(relative, pattern) for pattern in excludes):
                continue
            if path.suffix.lower() not in XML_SUFFIXES | {".cs", ".yml", ".yaml"} and filename.lower() not in CONFIG_NAMES:
                continue
            if path.is_symlink():
                findings.append(_finding(relative, 0, sorted(required), "symlink", "unresolved enforcement input"))
                continue
            try:
                content = path.read_text(encoding="utf-8-sig")
            except (OSError, UnicodeError) as exc:
                findings.append(_finding(relative, 0, sorted(required), "unreadable", type(exc).__name__))
                continue
            findings.extend(audit_content(relative, content, required))
    return sorted(findings, key=lambda x: (x["path"], x["line"], x["kind"], x["diagnosticIds"]))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="ArchitectureAnalyzer" + " opt-in suppression audit")
    parser.add_argument("--root", type=Path, default=Path("."))
    parser.add_argument("--ids", action="append", help="Override protected diagnostic IDs (comma separated)")
    parser.add_argument("--exclude", action="append", default=[], help="Explicit reviewed glob exclusion")
    parser.add_argument("--require-file", action="append", default=[], help="Required input path relative to root")
    args = parser.parse_args(argv)
    ids = parse_ids(args.ids) if args.ids else frozenset(DEFAULT_IDS)
    try:
        if not ids:
            raise ValueError("at least one diagnostic ID is required")
        findings = audit_tree(args.root, ids, tuple(args.exclude), tuple(args.require_file))
        exit_code = 1 if findings else 0
        result = {
            "schemaVersion": 1, "status": "DENY" if findings else "ALLOW",
            "diagnosticIds": sorted(ids), "findings": findings, "exitCode": exit_code,
        }
    except (OSError, ValueError) as exc:
        exit_code = 2
        result = {"schemaVersion": 1, "status": "ERROR", "findings": [],
                  "error": str(exc), "exitCode": exit_code}
    print(json.dumps(result, ensure_ascii=False, sort_keys=True, separators=(",", ":")))
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
