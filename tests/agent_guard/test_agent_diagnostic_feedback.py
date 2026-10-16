"""Offline agent-feedback fixtures. Fake process outputs are NOT model evaluations."""
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

SOURCE = Path(__file__).resolve().parents[2] / "scripts" / "agent_diagnostic_feedback.py"
SPEC = importlib.util.spec_from_file_location("agent_diagnostic_feedback", SOURCE)
feedback = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(feedback)


class FeedbackTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        (self.root / "App.csproj").write_text("<Project />", encoding="utf-8")
        self.ids = frozenset({"AARC002", "RT0001", "PSHARP2001"})

    def fake(self, code, stdout="", stderr=""):
        return lambda command, **kwargs: subprocess.CompletedProcess(
            command, code, stdout=stdout, stderr=stderr,
        )

    def check(self, runner, **extra):
        return feedback.run_check(self.root, Path("App.csproj"), self.ids,
                                  dotnet_path="dotnet", executor=runner, **extra)

    def test_build_violation_feedback(self):
        log = "/src/Bad.cs(12,3): error AARC002: forbidden dependency [App.csproj]\n"
        result = self.check(self.fake(1, log), strict=True)
        self.assertEqual("DENY", result["status"])
        self.assertEqual("AARC002", result["diagnostics"][0]["id"])
        self.assertEqual(12, result["diagnostics"][0]["line"])
        self.assertIn("Do not silence diagnostics", result["guidance"])

    def test_actual_fix_in_clean_candidate(self):
        self.assertEqual("ALLOW", self.check(self.fake(0), strict=True)["status"])

    def test_warning_suppression_cannot_masquerade_as_fix_in_strict_mode(self):
        (self.root / ".editorconfig").write_text(
            "dotnet_diagnostic.AARC002.severity = none\n", encoding="utf-8",
        )
        green_build = self.fake(0)
        self.assertEqual("DENY", self.check(green_build, strict=True)["status"])
        self.assertEqual("ALLOW", self.check(green_build, strict=False)["status"])

    def test_cli_uses_existing_policysharp_json_contract(self):
        cli = self.root / "src" / "PolicySharp.Cli" / "PolicySharp.Cli.csproj"
        cli.parent.mkdir(parents=True)
        cli.write_text("<Project />", encoding="utf-8")
        payload = {"exitCode": 1, "diagnostics": [
            {"id": "PSHARP2001", "message": "Denied", "location": "C.cs:1:1", "project": "App"}
        ]}
        result = self.check(self.fake(1, json.dumps(payload)), backend="policysharp", strict=True)
        self.assertEqual("DENY", result["status"])
        self.assertEqual(payload["diagnostics"], result["diagnostics"])

    def test_cli_invalid_json_fails_closed(self):
        cli = self.root / "src" / "PolicySharp.Cli" / "PolicySharp.Cli.csproj"
        cli.parent.mkdir(parents=True)
        cli.write_text("<Project />", encoding="utf-8")
        self.assertEqual("ERROR", self.check(self.fake(0, "not json"), backend="policysharp")["status"])

    def test_cli_exit_mismatch_fails_closed(self):
        cli = self.root / "src" / "PolicySharp.Cli" / "PolicySharp.Cli.csproj"
        cli.parent.mkdir(parents=True)
        cli.write_text("<Project />", encoding="utf-8")
        self.assertEqual("ERROR", self.check(self.fake(0, '{"exitCode":1,"diagnostics":[]}'),
                         backend="policysharp")["status"])

    def test_target_outside_workspace_is_rejected(self):
        with tempfile.TemporaryDirectory() as second:
            elsewhere = Path(second) / "Outside.csproj"
            elsewhere.write_text("<Project />", encoding="utf-8")
            result = feedback.run_check(self.root, elsewhere, self.ids, dotnet_path="dotnet",
                                        executor=self.fake(0))
            self.assertEqual("ERROR", result["status"])

    def test_missing_sdk_does_not_report_success(self):
        with patch.object(feedback.shutil, "which", return_value=None):
            self.assertEqual("ERROR", feedback.run_check(
                self.root, Path("App.csproj"), self.ids, executor=self.fake(0),
            )["status"])

    def test_timeout_fails_closed(self):
        def run(cmd, **kw):
            raise subprocess.TimeoutExpired(cmd, 1)
        self.assertEqual("ERROR", self.check(run)["status"])


if __name__ == "__main__":
    unittest.main()
