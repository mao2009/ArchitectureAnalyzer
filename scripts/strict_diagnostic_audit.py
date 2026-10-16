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

sys.path.insert(0, str(Path(__file__).resolve().parent))
from analyzer_participation import verify_participation

DEFAULT_IDS = ("AARC001", "AARC002", "AARC003", "AARC004", "AARC005", "AARC007", "AARC010", "AARC011", "AARC012",)
PROTECTED_CATEGORIES = frozenset(["architecture"])
SKIP_DIRS = frozenset({
    ".git", ".venv", ".idea", ".vs", "__pycache__", "bin", "obj",
    "node_modules", "packages", "artifacts", "TestResults", "out",
})
XML_SUFFIXES = frozenset({".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".ruleset"})
CONFIG_NAMES = frozenset({".editorconfig", ".globalconfig"})
DOWNGRADED = frozenset({"none", "silent", "hidden", "suggestion", "info", "warning"})
PRAGMA = re.compile(r"^\s*#\s*pragma\s+warning\s+disable(?:\s+(.*))?$", re.I)
SEVERITY = re.compile(
    r"^\s*dotnet_diagnostic\.([A-Za-z][A-Za-z0-9]*)\.severity\s*=\s*([A-Za-z]+)\b",
    re.I,
)
CATEGORY_SEVERITY = re.compile(
    r"^\s*dotnet_analyzer_diagnostic\.category-([A-Za-z][A-Za-z0-9]*)\.severity\s*=\s*([A-Za-z]+)\b", re.I,
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



SUPPRESS_PREFIX = re.compile(
    r"\b(?:Unconditional)?SuppressMessage(?:Attribute)?\s*\(", re.I,
)


def _code_mask(source: str) -> str:
    """Preserve source offsets but blank C# comments and literal content.

    Lexical helper, not a replacement for a full C# parser.
    """
    chars = list(source)
    n = len(chars)
    i = 0
    while i < n:
        start = i
        if source.startswith("//", i):
            j = source.find("\n", i)
            i = n if j < 0 else j
        elif source.startswith("/*", i):
            j = source.find("*/", i + 2)
            i = n if j < 0 else j + 2
        elif source.startswith('"""', i):
            # C# raw string literal, including multiline raw strings.
            q = 3
            while i + q < n and source[i + q] == '"':
                q += 1
            delim = '"' * q
            j = source.find(delim, i + q)
            i = n if j < 0 else j + q
        elif source.startswith('@"', i) or source.startswith('$@"', i) or source.startswith('@$"', i):
            prefix = 3 if source[i] != '@' or source.startswith('@$"', i) else 2
            if source.startswith('@$"', i):
                prefix = 3
            j = i + prefix
            while j < n:
                if source[j] == '"':
                    if j + 1 < n and source[j + 1] == '"':
                        j += 2
                        continue
                    j += 1
                    break
                j += 1
            i = j
        elif source[i] in ('"', "'") or source.startswith('$"', i):
            quote = '"' if source[i] != "'" else "'"
            j = i + (2 if source.startswith('$"', i) else 1)
            while j < n:
                if source[j] == "\\":
                    j += 2
                elif source[j] == quote:
                    j += 1
                    break
                else:
                    j += 1
            i = j
        else:
            i += 1
            continue
        for p in range(start, min(i, n)):
            if chars[p] != "\n":
                chars[p] = " "
    return "".join(chars)


def _attribute_targets(source: str, required: frozenset[str]) -> list[dict]:
    masked = _code_mask(source)
    results = []
    for match in SUPPRESS_PREFIX.finditer(masked):
        opening = match.end() - 1
        depth = 1
        j = opening + 1
        while j < len(masked) and depth:
            if masked[j] == "(":
                depth += 1
            elif masked[j] == ")":
                depth -= 1
            j += 1
        if depth:
            continue
        targets = affected_ids(source[opening + 1:j - 1], required)
        if targets:
            results.append((source.count("\n", 0, match.start()) + 1, targets))
    return results


def _strip_xml_comments(source: str) -> str:
    return re.sub(r"<!--.*?-->", lambda m: re.sub(r"[^\n]", " ", m.group(0)),
                  source, flags=re.S)


def audit_content(relative_path: str, content: str, required: frozenset[str]) -> list[dict]:
    """Return deterministic findings; intentionally does not parse arbitrary MSBuild."""
    path = Path(relative_path)
    name = path.name.lower()
    suffix = path.suffix.lower()
    results: list[dict] = []
    lines = content.splitlines()

    if suffix == ".cs":
        masked_lines = _code_mask(content).splitlines()
        for index, line in enumerate(masked_lines, 1):
            pragma = PRAGMA.match(line)
            if pragma:
                targets = affected_ids(pragma.group(1) or "", required, empty_means_all=True)
                if targets:
                    results.append(_finding(relative_path, index, targets, "pragma", "warning disable"))
        for line, targets in _attribute_targets(content, required):
            results.append(_finding(relative_path, line, targets, "suppress-attribute",
                                    "SuppressMessage attribute"))

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
            category_match = CATEGORY_SEVERITY.match(line)
            if (category_match and category_match.group(1).lower() in PROTECTED_CATEGORIES
                    and category_match.group(2).lower() in DOWNGRADED):
                results.append(_finding(
                    relative_path, index, sorted(required),
                    "category-severity-override", category_match.group(2).lower(),
                ))
            global_match = GLOBAL_SEVERITY.match(line)
            if global_match and global_match.group(1).lower() in DOWNGRADED:
                results.append(_finding(
                    relative_path, index, sorted(required),
                    "global-severity-override", global_match.group(1).lower(),
                ))

    if suffix in XML_SUFFIXES:
        content = _strip_xml_comments(content)
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
    def on_walk_error(error: OSError) -> None:
        findings.append(_finding(str(error.filename or root), 0, sorted(required),
                                 "unreadable-directory", str(error)))

    for directory, dirs, files in os.walk(root, followlinks=False, onerror=on_walk_error):
        next_dirs = []
        for dirname in sorted(dirs):
            if dirname in SKIP_DIRS:
                continue
            candidate = Path(directory) / dirname
            if candidate.is_symlink():
                findings.append(_finding(candidate.relative_to(root).as_posix(), 0, sorted(required),
                                         "symlink-directory", "Directory cannot be traversed safely"))
            else:
                next_dirs.append(dirname)
        dirs[:] = next_dirs
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
    parser.add_argument("--project", type=Path, help="Governed .csproj for MSBuild participation check")
    parser.add_argument("--analyzer", default="loach.ArchitectureAnalyzer", help="Required analyzer reference")
    parser.add_argument("--additional-file", default="architecture.contract.json", help="Required AdditionalFiles input")
    parser.add_argument("--ids", action="append", help="Override protected diagnostic IDs (comma separated)")
    parser.add_argument("--exclude", action="append", default=[], help="Explicit reviewed glob exclusion")
    parser.add_argument("--require-file", action="append", default=[], help="Required input path relative to root")
    args = parser.parse_args(argv)
    ids = parse_ids(args.ids) if args.ids else frozenset(DEFAULT_IDS)
    try:
        if not ids:
            raise ValueError("at least one diagnostic ID is required")
        findings = audit_tree(args.root, ids, tuple(args.exclude), tuple(args.require_file))
        if args.project is not None:
            problems = verify_participation(
                args.root, args.project, args.analyzer, args.additional_file,
            )
            findings.extend(_finding(str(args.project), 0, sorted(ids),
                                     "enforcement-unverified", message)
                            for message in problems)
        # Source scanning alone must never be interpreted as a passing
        # enforcement check. A successful opt-in MSBuild wiring audit is
        # AUDIT_PASS, not proof that a Roslyn diagnostic actually executed.
        status = ("DENY" if findings else
                  "AUDIT_ONLY" if args.project is None else "AUDIT_PASS")
        exit_code = 1 if findings else 2 if args.project is None else 0
        result = {
            "schemaVersion": 1, "status": status,
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
