"""Regression fixtures for the opt-in suppression audit (stdlib only)."""
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SOURCE = Path(__file__).resolve().parents[2] / "scripts" / "strict_diagnostic_audit.py"
SPEC = importlib.util.spec_from_file_location("strict_diagnostic_audit", SOURCE)
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)


class StrictAuditTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.ids = frozenset({"AARC002", "RT0001", "PSHARP2001"})

    def write(self, name, content):
        target = self.root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8")
        return target

    def findings(self):
        return gate.audit_tree(self.root, self.ids)

    def test_clean_project_and_unrelated_disable_allowed(self):
        self.write("src/C.cs", "#pragma warning disable CS0168\nclass C {}")
        self.write(".editorconfig", "[*.cs]\ndotnet_diagnostic.CS0168.severity = none\n")
        self.assertEqual([], self.findings())

    def test_pragmas_scoped_and_all(self):
        self.write("src/C.cs", "#pragma warning disable RT0001, CS0168\n#pragma warning disable\n")
        items = self.findings()
        self.assertEqual(["pragma", "pragma"], [x["kind"] for x in items])
        self.assertEqual(["RT0001"], items[0]["diagnosticIds"])
        self.assertEqual(sorted(self.ids), items[1]["diagnosticIds"])

    def test_suppress_message_attribute_and_global_suppression(self):
        self.write("src/GlobalSuppressions.cs", '[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Architecture", "AARC002:reason")]\n')
        self.write("src/C.cs", '[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Purity", "RT0001")]\nclass C {}')
        items = self.findings()
        self.assertEqual(2, len(items))
        self.assertTrue(all(i["kind"] == "suppress-attribute" for i in items))

    def test_editorconfig_severity_downgrade(self):
        self.write(".editorconfig", "dotnet_diagnostic.AARC002.severity = warning\n")
        self.write("module/.globalconfig", "dotnet_analyzer_diagnostic.severity = none\n")
        self.assertEqual(["severity-override", "global-severity-override"],
                         [item["kind"] for item in self.findings()])

    def test_category_severity_downgrade(self):
        category = sorted(gate.PROTECTED_CATEGORIES)[0]
        self.write(".editorconfig",
                   "dotnet_analyzer_diagnostic.category-" + category + ".severity = none\n")
        found = self.findings()
        self.assertEqual(1, len(found))
        self.assertEqual("category-severity-override", found[0]["kind"])

    def test_msbuild_and_ruleset(self):
        self.write("Directory.Build.props", "<Project><PropertyGroup><NoWarn>$(NoWarn);PSHARP2001</NoWarn>"
                   "<RunAnalyzersDuringBuild>false</RunAnalyzersDuringBuild></PropertyGroup></Project>")
        self.write("config.ruleset", '<Rule Id="AARC002" Action="None" />')
        self.assertEqual({"msbuild-nowarn", "msbuild-runanalyzersduringbuild", "ruleset-override"},
                         {x["kind"] for x in self.findings()})

    def test_workflow_disable_and_comment_ignored(self):
        self.write(".github/workflows/build.yml", "steps:\n  # RunAnalyzers=false\n  - run: dotnet build -p:RunAnalyzers=false\n")
        items = self.findings()
        self.assertEqual(1, len(items))
        self.assertEqual("workflow-disables-analyzers", items[0]["kind"])

    def test_require_file_fail_closed(self):
        self.assertEqual("missing-required-file", gate.audit_tree(
            self.root, self.ids, require_files=("architecture.contract.json",))[0]["kind"])
        with self.assertRaises(ValueError):
            gate.audit_tree(self.root, self.ids, require_files=("../escape",))

    def test_cli_exit_and_json(self):
        self.write(".editorconfig", "dotnet_diagnostic.RT0001.severity = none\n")
        run = subprocess.run(
            [sys.executable, str(SOURCE), "--root", str(self.root), "--ids", "RT0001"],
            text=True, capture_output=True, check=False,
        )
        self.assertEqual(1, run.returncode)
        self.assertEqual("DENY", json.loads(run.stdout)["status"])

    def test_invalid_root_fails_closed(self):
        self.assertEqual(2, gate.main(["--root", str(self.root / "missing")]))


if __name__ == "__main__":
    unittest.main()
