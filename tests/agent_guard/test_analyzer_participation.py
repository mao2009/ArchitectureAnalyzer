"""Offline contract tests for evaluated analyzer/AdditionalFiles inspection."""
import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path

SOURCE = Path(__file__).resolve().parents[2] / "scripts" / "analyzer_participation.py"
SPEC = importlib.util.spec_from_file_location("analyzer_participation", SOURCE)
guard = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(guard)


class ParticipationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "App.csproj").write_text("<Project />", encoding="utf-8")

    def runner(self, package=None, additional=None, props=None, returncode=0):
        values = {
            "Properties": props if props is not None else {
                "RunAnalyzers": "true", "RunAnalyzersDuringBuild": "true"
            },
            "Items": {
                "PackageReference": ([{"Identity": package}] if package else []),
                "ProjectReference": [],
                "Analyzer": [],
                "AdditionalFiles": ([{"Identity": additional}] if additional else []),
            },
        }
        return lambda cmd, **kw: subprocess.CompletedProcess(
            cmd, returncode, stdout=json.dumps(values), stderr="",
        )

    def test_missing_analyzer_reference_denied(self):
        problems = guard.verify_participation(
            self.root, Path("App.csproj"), "loach.PureSharp",
            executor=self.runner(),
        )
        self.assertTrue(any("not present" in p for p in problems))

    def test_missing_contract_denied(self):
        problems = guard.verify_participation(
            self.root, Path("App.csproj"), "loach.ArchitectureAnalyzer",
            required_additional="architecture.contract.json",
            executor=self.runner(package="loach.ArchitectureAnalyzer"),
        )
        self.assertTrue(any("AdditionalFiles" in p for p in problems))

    def test_explicitly_wired_analyzer_and_contract(self):
        (self.root / "architecture.contract.json").write_text("{}", encoding="utf-8")
        problems = guard.verify_participation(
            self.root, Path("App.csproj"), "loach.ArchitectureAnalyzer",
            required_additional="architecture.contract.json",
            executor=self.runner(package="loach.ArchitectureAnalyzer",
                                 additional="architecture.contract.json"),
        )
        self.assertEqual([], problems)

    def test_evaluated_nowarn_is_denied(self):
        props = {"RunAnalyzers": "true", "RunAnalyzersDuringBuild": "true",
                 "NoWarn": "$(NoWarn);AARC002", "WarningsNotAsErrors": ""}
        problems = guard.verify_participation(
            self.root, Path("App.csproj"), "loach.PureSharp",
            executor=self.runner(package="loach.PureSharp", props=props),
            protected_ids=frozenset({"AARC002"}),
        )
        self.assertTrue(any("NoWarn" in p for p in problems))

    def test_exclude_analyzer_assets_is_denied(self):
        def without_analyzer(cmd, **kwargs):
            values = {"Properties": {"RunAnalyzers": "true",
                                     "RunAnalyzersDuringBuild": "true"},
                      "Items": {"PackageReference": [
                          {"Identity": "loach.PureSharp", "ExcludeAssets": "analyzers"}],
                          "ProjectReference": [], "Analyzer": [], "AdditionalFiles": []}}
            return subprocess.CompletedProcess(cmd, 0, stdout=json.dumps(values), stderr="")
        problems = guard.verify_participation(
            self.root, Path("App.csproj"), "loach.PureSharp",
            executor=without_analyzer,
        )
        self.assertTrue(any("excludes analyzer assets" in p for p in problems))

    def test_analyzers_disabled_denied(self):
        props = {"RunAnalyzers": "false", "RunAnalyzersDuringBuild": "true"}
        problems = guard.verify_participation(
            self.root, Path("App.csproj"), "loach.PureSharp",
            executor=self.runner(package="loach.PureSharp", props=props),
        )
        self.assertTrue(any("disabled" in p for p in problems))

    def test_empty_or_invalid_msbuild_result_fails_closed(self):
        def invalid(cmd, **kw):
            return subprocess.CompletedProcess(cmd, 0, stdout="not-json", stderr="")
        self.assertTrue(guard.verify_participation(
            self.root, Path("App.csproj"), "loach.PureSharp", executor=invalid))

    def test_internal_cli_requires_policy(self):
        self.assertTrue(guard.verify_participation(
            self.root, Path("App.csproj"), "PolicySharp",
            "policysharp.json", internal_cli=True, executor=self.runner(),
        ))
        (self.root / "policysharp.json").write_text("{}", encoding="utf-8")
        self.assertEqual([], guard.verify_participation(
            self.root, Path("App.csproj"), "PolicySharp",
            "policysharp.json", internal_cli=True, executor=self.runner(),
        ))

    def test_solution_must_identify_governed_project(self):
        (self.root / "Solution.sln").write_text("", encoding="utf-8")
        self.assertTrue(guard.verify_participation(
            self.root, Path("Solution.sln"), "loach.PureSharp",
            executor=self.runner(package="loach.PureSharp"),
        ))


if __name__ == "__main__":
    unittest.main()
