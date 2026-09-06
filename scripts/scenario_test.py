import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("deployment_scenario", ROOT / "scripts" / "scenario.py")
scenario = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(scenario)


class DeploymentScenarioContractTests(unittest.TestCase):
    def test_definition_has_six_step_smoke_prefix_and_typed_assertions(self) -> None:
        definition = json.loads(scenario.DEFINITION.read_text(encoding="utf-8"))
        scenario.validate_definition(definition)
        smoke = [step["id"] for step in definition["steps"] if "smoke" in step["levels"]]
        self.assertEqual(
            ["bootstrap-principals", "register-runner", "create-task", "claim",
             "run-fake-cli", "auto-review"],
            smoke,
        )

    def test_failure_report_is_junit_and_markdown_with_stable_failure_count(self) -> None:
        definition = {"id": "fixture", "schemaVersion": 1}
        rows = [{"id": "broken", "title": "Broken step", "status": "failed",
                 "duration": 0.125, "evidence": "evidence/broken.json", "message": "expected failure"}]
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            scenario.write_reports(output, definition, "inproc", "smoke", rows, 0.0)
            suite = ET.parse(output / "scenario.junit.xml").getroot()
            self.assertEqual("1", suite.attrib["failures"])
            self.assertIn("Deployment scenario: FAIL", (output / "scenario-report.md").read_text())


if __name__ == "__main__":
    unittest.main()
