import importlib.machinery
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT_PATH = Path(__file__).resolve().parents[1] / "agent-runner-deps-closure.py"
LOADER = importlib.machinery.SourceFileLoader("agent_runner_deps_closure", str(SCRIPT_PATH))
SPEC = importlib.util.spec_from_loader(LOADER.name, LOADER)
assert SPEC is not None
MODULE = importlib.util.module_from_spec(SPEC)
LOADER.exec_module(MODULE)


def deps_document():
    return {
        "runtimeTarget": {"name": ".NETCoreApp,Version=v10.0"},
        "targets": {
            ".NETCoreApp,Version=v10.0": {
                "agent-host/1.0.0": {
                    "runtime": {
                        "agent-host.dll": {},
                        "TaskServer.Contracts.dll": {},
                    }
                },
                "CodingAgentRunner/0.7.0": {
                    "runtime": {"lib/net10.0/CodingAgentRunner.dll": {}}
                },
                "Native.Library/1.0.0": {
                    "runtimeTargets": {
                        "runtimes/linux-x64/lib/net10.0/Runtime.Helper.dll": {
                            "assetType": "runtime",
                            "rid": "linux-x64",
                        },
                        "runtimes/linux-x64/native/libnative.so": {
                            "assetType": "native",
                            "rid": "linux-x64",
                        },
                    }
                },
            }
        },
    }


class DependencyClosureTests(unittest.TestCase):
    def test_reports_every_missing_managed_runtime_assembly_by_name(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            staged_root = Path(temporary_directory)
            (staged_root / "agent-host.dll").write_bytes(b"present")

            missing = MODULE.find_missing_runtime_assemblies(
                deps_document(),
                staged_root,
            )

        self.assertEqual(
            [
                "CodingAgentRunner.dll",
                "Runtime.Helper.dll",
                "TaskServer.Contracts.dll",
            ],
            missing,
        )

    def test_accepts_a_complete_flattened_publish_set(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            staged_root = Path(temporary_directory)
            for assembly_name in (
                "agent-host.dll",
                "TaskServer.Contracts.dll",
                "CodingAgentRunner.dll",
                "Runtime.Helper.dll",
            ):
                (staged_root / assembly_name).write_bytes(b"present")

            missing = MODULE.find_missing_runtime_assemblies(
                deps_document(),
                staged_root,
            )

        self.assertEqual([], missing)

    def test_cli_fails_with_every_missing_runtime_assembly_name(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            staged_root = Path(temporary_directory)
            deps_path = staged_root / "agent-host.deps.json"
            deps_path.write_text(json.dumps(deps_document()), encoding="utf-8")
            (staged_root / "agent-host.dll").write_bytes(b"present")

            result = subprocess.run(
                [sys.executable, str(SCRIPT_PATH), str(deps_path), str(staged_root)],
                check=False,
                capture_output=True,
                text=True,
            )

        self.assertEqual(1, result.returncode)
        self.assertEqual(
            "missing runtime assemblies: CodingAgentRunner.dll, "
            "Runtime.Helper.dll, TaskServer.Contracts.dll\n",
            result.stderr,
        )

    def test_rejects_a_document_without_the_selected_runtime_target(self):
        document = deps_document()
        document["runtimeTarget"]["name"] = "missing-target"

        with tempfile.TemporaryDirectory() as temporary_directory:
            with self.assertRaisesRegex(
                MODULE.DependencyClosureError,
                "targets\\['missing-target'\\] must be a JSON object",
            ):
                MODULE.find_missing_runtime_assemblies(
                    document,
                    Path(temporary_directory),
                )


if __name__ == "__main__":
    unittest.main()
