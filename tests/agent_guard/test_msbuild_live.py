"""Real MSBuild evaluation regression checks; no package download needed."""
import shutil
import tempfile
import unittest
from pathlib import Path
import importlib.util

SRC = Path(__file__).resolve().parents[2] / "scripts" / "analyzer_participation.py"
SPEC = importlib.util.spec_from_file_location("analyzer_participation", SRC)
module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(module)


@unittest.skipUnless(shutil.which("dotnet"), "requires the .NET SDK")
class LiveMSBuildParticipationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.project = self.root / "Guarded.csproj"
        self.contract = self.root / "architecture.contract.json"
        self.contract.write_text("{}", encoding="utf-8")
        self.write_project(True)

    def write_project(self, analyzer_present, enabled=True, include_contract=True):
        package = ('<PackageReference Include="loach.ArchitectureAnalyzer" Version="0.1.0" />'
                   if analyzer_present else "")
        additional = ('<AdditionalFiles Include="architecture.contract.json" />'
                      if include_contract else "")
        flag = "true" if enabled else "false"
        self.project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk">'
            '<PropertyGroup><TargetFramework>net8.0</TargetFramework>'
            '<RunAnalyzers>' + flag + '</RunAnalyzers></PropertyGroup>'
            '<ItemGroup>' + package + additional + '</ItemGroup></Project>',
            encoding="utf-8",
        )

    def verify(self):
        return module.verify_participation(
            self.root, Path("Guarded.csproj"),
            "loach.ArchitectureAnalyzer", "architecture.contract.json",
        )

    def test_evaluated_reference_and_contract(self):
        self.assertEqual([], self.verify())

    def test_removing_analyzer_reference_is_denied(self):
        self.write_project(False)
        self.assertTrue(any("not present" in p for p in self.verify()))

    def test_removing_contract_binding_is_denied(self):
        self.write_project(True, include_contract=False)
        self.assertTrue(any("AdditionalFiles" in p for p in self.verify()))

    def test_nonexistent_contract_file_is_denied(self):
        self.contract.unlink()
        self.assertTrue(any("missing" in p for p in self.verify()))

    def test_disabling_run_analyzers_is_denied(self):
        self.write_project(True, enabled=False)
        self.assertTrue(any("disabled" in p for p in self.verify()))


if __name__ == "__main__":
    unittest.main()
