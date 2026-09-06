#!/usr/bin/env python3
"""One deterministic deployment regression scenario for every target."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
DEFINITION = ROOT / "testsupport" / "scenario" / "deployment-scenario.json"
FIXTURE_ROOT = DEFINITION.parent
PROTOCOL = "2"
FIXED_REVIEW_START = "2026-09-06T10:00:10Z"
FIXED_REVIEW_END = "2026-09-06T10:00:11Z"


class ScenarioFailure(RuntimeError):
    pass


class TargetUnavailable(ScenarioFailure):
    pass


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def compact_json(value: object) -> str:
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False)


def validate_definition(definition: dict) -> None:
    allowed_assertions = {
        "http-status", "json-equals", "json-prefix", "count", "positive",
        "not-empty", "exit-code", "sha256", "file-exists", "git-commit", "git-ancestor",
    }
    if definition.get("schemaVersion") != 1 or not definition.get("id"):
        raise ScenarioFailure("Scenario definition must declare schemaVersion 1 and an id.")
    steps = definition.get("steps")
    if not isinstance(steps, list) or not steps:
        raise ScenarioFailure("Scenario definition must contain ordered steps.")
    ids = [step.get("id") for step in steps]
    if len(ids) != len(set(ids)) or any(not item for item in ids):
        raise ScenarioFailure("Scenario step ids must be non-empty and unique.")
    for step in steps:
        if not set(step.get("levels", [])).issubset({"smoke", "full"}):
            raise ScenarioFailure(f"Step '{step['id']}' has an unknown level.")
        if not step.get("assertions"):
            raise ScenarioFailure(f"Step '{step['id']}' must declare typed assertions.")
        for assertion in step["assertions"]:
            if assertion.get("type") not in allowed_assertions:
                raise ScenarioFailure(
                    f"Step '{step['id']}' has unknown assertion type '{assertion.get('type')}'.")


def read_path(value: object, path: str) -> object:
    current = value
    for segment in path.split("."):
        if isinstance(current, list):
            current = current[int(segment)]
        elif isinstance(current, dict) and segment in current:
            current = current[segment]
        else:
            raise ScenarioFailure(f"Assertion path '{path}' was not present.")
    return current


class Api:
    def __init__(self, base_url: str, token: str | None, runner_token: str | None = None):
        self.base_url = base_url.rstrip("/")
        self.token = token
        self.runner_token = runner_token or token

    def request(self, method: str, path: str, body: object | None = None) -> tuple[int, object]:
        data = None if body is None else compact_json(body).encode("utf-8")
        headers = {
            "Accept": "application/json",
            "X-Task-Protocol-Version": PROTOCOL,
            "X-Task-Client-Version": "deployment-scenario/1",
            "X-Actor-Id": "deployment-scenario",
        }
        if data is not None:
            headers["Content-Type"] = "application/json"
        is_runner_mutation = method != "GET" and path.startswith(
            ("/api/v1/runners", "/api/v1/runs", "/api/v1/work-permits"))
        credential = self.runner_token if is_runner_mutation else self.token
        if credential:
            headers["Authorization"] = f"Bearer {credential}"
        request = urllib.request.Request(self.base_url + path, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=15) as response:
                raw = response.read()
                return response.status, json.loads(raw) if raw else None
        except urllib.error.HTTPError as error:
            raw = error.read().decode("utf-8", errors="replace")
            raise ScenarioFailure(f"{method} {path} returned {error.code}: {raw}") from error
        except urllib.error.URLError as error:
            raise TargetUnavailable(f"{method} {path} could not reach {self.base_url}: {error.reason}") from error

    def get(self, path: str) -> object:
        return self.request("GET", path)[1]

    def post(self, path: str, body: object) -> tuple[int, object]:
        return self.request("POST", path, body)

    def put(self, path: str, body: object | None = None) -> tuple[int, object]:
        return self.request("PUT", path, body)


class LocalTarget:
    def __init__(self, output: Path):
        self.output = output
        self.temp: tempfile.TemporaryDirectory[str] | None = None
        self.process: subprocess.Popen[str] | None = None
        self.log_handle = None
        self.base_url = ""
        self.assembly: Path | None = None

    def start(self) -> Api:
        self.temp = tempfile.TemporaryDirectory(prefix="agent-studio-scenario-")
        port = free_port()
        self.base_url = f"http://127.0.0.1:{port}"
        configuration = os.environ.get("SCENARIO_CONFIGURATION", "Release")
        self.assembly = ROOT / "task-server" / "bin" / configuration / "net10.0" / "task-server.dll"
        if not self.assembly.exists():
            run_checked([
                "dotnet", "build", "task-server/TaskServer.csproj", "--configuration", configuration,
                "--nologo", "--verbosity", "minimal",
            ], ROOT)
        log_path = self.output / "evidence" / "task-server.log"
        log_path.parent.mkdir(parents=True, exist_ok=True)
        self.log_handle = log_path.open("w", encoding="utf-8")
        data = Path(self.temp.name) / "store"
        self._spawn(data)
        wait_ready(self.base_url, self.process)
        return Api(self.base_url, None)

    def _spawn(self, data: Path) -> None:
        assert self.assembly is not None and self.log_handle is not None
        self.process = subprocess.Popen(
            [
                "dotnet", str(self.assembly), "--urls", self.base_url,
                "--TaskServer:DataDirectory", str(data),
                "--TaskServer:BackupDirectory", str(data / "backups"),
                "--TaskServer:MinimumLeaseSeconds", "5",
                "--TaskServer:MaximumLeaseSeconds", "60",
            ],
            cwd=ROOT,
            stdout=self.log_handle,
            stderr=subprocess.STDOUT,
            text=True,
        )

    def restart_empty(self, backup: dict) -> None:
        self._stop_process()
        assert self.temp is not None and self.log_handle is not None
        empty_store = Path(self.temp.name) / "restored-store"
        backup_directory = empty_store / "backups"
        backup_directory.mkdir(parents=True, exist_ok=True)
        shutil.copy2(backup["path"], backup_directory / f"{backup['backupId']}.db")
        self.log_handle.write("\n[deployment-scenario] restarting with an empty store\n")
        self.log_handle.flush()
        self._spawn(empty_store)
        wait_ready(self.base_url, self.process)

    def _stop_process(self) -> None:
        if not self.process or self.process.poll() is not None:
            return
        self.process.terminate()
        try:
            self.process.wait(timeout=8)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=3)

    def stop(self) -> None:
        self._stop_process()
        if self.log_handle:
            self.log_handle.close()
        if self.temp:
            self.temp.cleanup()


class ComposeTarget:
    TOKEN = "deployment-scenario-token-00000000000000000000000000000000"

    def __init__(self, output: Path):
        self.output = output
        self.project = f"agent-studio-scenario-{os.getpid()}"
        self.port = free_port()
        self.ui_port = free_port()
        self.api_port = free_port()
        self.compose = [
            "docker", "compose", "--project-name", self.project,
            "-f", str(ROOT / "docker-compose.yml"),
            "-f", str(FIXTURE_ROOT / "docker-compose.scenario.yml"),
            "--profile", "distributed",
            "--profile", "runner",
        ]

    def environment(self) -> dict[str, str]:
        environment = os.environ.copy()
        environment["STUDIO_TASKSERVER_PORT"] = str(self.port)
        environment["STUDIO_UI_PORT"] = str(self.ui_port)
        environment["STUDIO_API_PORT"] = str(self.api_port)
        environment["SCENARIO_TASK_SERVER_TOKEN"] = self.TOKEN
        return environment

    def start(self) -> Api:
        if shutil.which("docker") is None:
            raise TargetUnavailable("Docker is required for --target compose.")
        environment = self.environment()
        run_checked(self.compose + ["config", "--quiet"], ROOT, environment)
        run_checked(self.compose + ["up", "--build", "--detach", "--wait",
                                    "orchestrator-api", "frontend", "task-server",
                                    "scenario-fake-cli"], ROOT, environment)
        base_url = f"http://127.0.0.1:{self.port}"
        wait_ready(base_url, None)
        with urllib.request.urlopen(f"http://127.0.0.1:{self.ui_port}/healthz", timeout=10) as response:
            if response.status != 200:
                raise ScenarioFailure(f"Compose frontend health returned {response.status}.")
        with urllib.request.urlopen(f"http://127.0.0.1:{self.ui_port}/", timeout=10) as response:
            if b"<app-root" not in response.read():
                raise ScenarioFailure("Compose frontend did not serve the Studio application shell.")
        with urllib.request.urlopen(f"http://127.0.0.1:{self.ui_port}/api/tasks/grouped", timeout=10) as response:
            if b'"backlog"' not in response.read():
                raise ScenarioFailure("Compose Studio API did not return grouped tasks.")
        return Api(base_url, self.TOKEN)

    def restart_empty(self, backup: dict) -> None:
        environment = self.environment()
        container = run_checked(self.compose + ["ps", "--quiet", "task-server"], ROOT, environment).stdout.strip()
        if not container:
            raise ScenarioFailure("Compose Task Server container was not found for restore.")
        copied_backup = self.output / "evidence" / f"{backup['backupId']}.db"
        run_checked(["docker", "cp", f"{container}:{backup['path']}", str(copied_backup)], ROOT, environment)
        run_checked(self.compose + ["stop", "task-server"], ROOT, environment)
        run_checked(self.compose + ["rm", "--force", "task-server"], ROOT, environment)
        volume = f"{self.project}_orchestrator-data"
        run_checked(["docker", "volume", "inspect", volume], ROOT, environment)
        run_checked(["docker", "volume", "rm", volume], ROOT, environment)
        run_checked(self.compose + ["up", "--detach", "--wait", "task-server"], ROOT, environment)
        container = run_checked(self.compose + ["ps", "--quiet", "task-server"], ROOT, environment).stdout.strip()
        destination_directory = str(Path(backup["path"]).parent)
        run_checked(["docker", "exec", container, "mkdir", "-p", destination_directory], ROOT, environment)
        run_checked(["docker", "cp", str(copied_backup), f"{container}:{backup['path']}"], ROOT, environment)
        wait_ready(f"http://127.0.0.1:{self.port}", None)

    def stop(self) -> None:
        environment = self.environment()
        evidence = self.output / "evidence"
        evidence.mkdir(parents=True, exist_ok=True)
        try:
            with (evidence / "compose.log").open("w", encoding="utf-8") as handle:
                subprocess.run(self.compose + ["logs", "--no-color"], cwd=ROOT, env=environment,
                               stdout=handle, stderr=subprocess.STDOUT, check=False, text=True)
        finally:
            subprocess.run(self.compose + ["down", "--volumes", "--remove-orphans"], cwd=ROOT,
                           env=environment, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)


def free_port() -> int:
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return int(listener.getsockname()[1])


def run_checked(command: list[str], cwd: Path, environment: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(command, cwd=cwd, env=environment, check=True, text=True,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    except FileNotFoundError as error:
        raise TargetUnavailable(f"Required executable was not found: {command[0]}") from error
    except subprocess.CalledProcessError as error:
        raise ScenarioFailure(f"Command failed ({error.returncode}): {' '.join(command)}\n{error.stdout}") from error


def wait_ready(base_url: str, process: subprocess.Popen[str] | None) -> None:
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        if process is not None and process.poll() is not None:
            raise TargetUnavailable(f"In-process Task Server exited with code {process.returncode}.")
        try:
            with urllib.request.urlopen(base_url + "/readyz", timeout=2) as response:
                if response.status == 200:
                    return
        except (urllib.error.URLError, TimeoutError):
            pass
        time.sleep(0.1)
    raise TargetUnavailable(f"Target did not become ready at {base_url}.")


class Scenario:
    def __init__(self, definition: dict, target: str, level: str, output: Path,
                 api: Api, target_handle: LocalTarget | ComposeTarget | None = None):
        self.definition = definition
        self.target = target
        self.level = level
        self.output = output
        self.api = api
        self.target_handle = target_handle
        self.fixture = definition["fixture"]
        self.state: dict[str, object] = {}
        self.output.joinpath("evidence").mkdir(parents=True, exist_ok=True)
        self.run_suffix = uuid.uuid4().hex[:10] if target == "remote" else ""

    def cleanup(self) -> None:
        repository = self.state.get("repository")
        if repository:
            shutil.rmtree(str(repository), ignore_errors=True)
        if self.target != "remote":
            return
        project = self.state.get("project")
        if not isinstance(project, dict):
            return
        for task in self.state.get("tasks", []):
            current = self.api.get(
                f"/api/v1/projects/{project['projectId']}/tasks/{task['taskId']}")
            if current["state"] == "7-archive":
                continue
            self.api.put(f"/api/v1/projects/{project['projectId']}/tasks/{task['taskId']}", {
                "title": None, "body": None, "state": "7-archive", "expectedVersion": current["version"],
            })

    def identity(self, value: str) -> str:
        return f"{value}_{self.run_suffix}" if self.run_suffix else value

    def execute(self, step: dict) -> object:
        action = getattr(self, "step_" + step["id"].replace("-", "_"))
        value = action()
        self.assert_step(step, value)
        return value

    def assert_step(self, step: dict, value: object) -> None:
        for assertion in step["assertions"]:
            kind = assertion["type"]
            path = assertion.get("path")
            actual = read_path(value, path) if path else value
            expected = assertion.get("expected")
            if "expectedByTarget" in assertion:
                expected = assertion["expectedByTarget"].get(
                    self.target, assertion["expectedByTarget"]["default"])
            if "expectedPath" in assertion:
                expected = read_path(value, assertion["expectedPath"])
            if kind == "http-status" and actual != expected:
                raise ScenarioFailure(f"Expected HTTP {expected}, received {actual}.")
            if kind == "json-equals" and actual != expected:
                raise ScenarioFailure(f"Expected {path}={expected!r}, received {actual!r}.")
            if kind == "json-prefix" and not str(actual).startswith(str(expected)):
                raise ScenarioFailure(f"Expected {path} to start with {expected!r}, received {actual!r}.")
            if kind == "count" and len(actual) != expected:  # type: ignore[arg-type]
                raise ScenarioFailure(f"Expected {path} count {expected}, received {len(actual)}.")  # type: ignore[arg-type]
            if kind == "positive" and (not isinstance(actual, (int, float)) or actual <= 0):
                raise ScenarioFailure(f"Expected positive {path}, received {actual!r}.")
            if kind == "not-empty" and not actual:
                raise ScenarioFailure(f"Expected non-empty {path}.")
            if kind == "exit-code" and actual != expected:
                raise ScenarioFailure(f"Expected exit code {expected}, received {actual}.")
            if kind == "sha256" and (not isinstance(actual, str) or len(actual) != 64
                                      or any(c not in "0123456789abcdef" for c in actual.lower())):
                raise ScenarioFailure(f"Expected SHA-256 at {path}, received {actual!r}.")
            if kind == "file-exists" and not self.output.joinpath(str(actual)).is_file():
                raise ScenarioFailure(f"Expected evidence file '{actual}'.")
            if kind == "git-commit":
                run_checked(["git", "cat-file", "-e", f"{actual}^{{commit}}"], Path(self.state["repository"]))
            if kind == "git-ancestor":
                run_checked(["git", "merge-base", "--is-ancestor", str(actual), "HEAD"], Path(self.state["repository"]))

    def step_bootstrap_principals(self) -> object:
        workspace_id = self.identity(self.fixture["workspaceId"])
        project_id = self.identity(self.fixture["projectId"])
        status, workspace = self.api.post("/api/v1/workspaces", {
            "name": self.fixture["projectName"], "workspaceId": workspace_id,
        })
        project_status, project = self.api.post("/api/v1/projects", {
            "workspaceId": workspace_id,
            "name": self.fixture["projectName"],
            "taskKeyPrefix": self.fixture["taskKeyPrefix"],
            "projectId": project_id,
        })
        self.state.update(workspace=workspace, project=project)
        return {"httpStatus": min(status, project_status), "project": {**project, "projectId": self.fixture["projectId"]}}

    def step_register_runner(self) -> object:
        coding_id = self.identity("scenario-coding-runner")
        review_id = self.identity("scenario-review-runner")
        coding_instance = self.identity("scenario-coding-instance")
        review_instance = self.identity("scenario-review-instance")
        _, coding = self.api.put(f"/api/v1/runners/{coding_id}", {
            "name": coding_id, "hostId": self.identity("scenario-coding-host"),
            "instanceId": coding_instance, "runnerVersion": "scenario-1.0",
            "protocolVersion": 2, "capabilities": ["coding-executor"],
            "bootstrapMaxParallelism": 1, "attemptLeaseTtlSeconds": 60,
        })
        _, review = self.api.put(f"/api/v1/runners/{review_id}", {
            "name": review_id, "hostId": self.identity("scenario-review-host"),
            "instanceId": review_instance, "runnerVersion": "scenario-1.0",
            "protocolVersion": 2,
            "capabilities": ["review-executor", "review:git"],
            "bootstrapMaxParallelism": 1, "attemptLeaseTtlSeconds": 60,
        })
        self.state.update(coding_id=coding_id, coding_instance=coding_instance,
                          review_id=review_id, review_instance=review_instance)
        return {"coding": coding, "review": review}

    def step_create_task(self) -> object:
        project = self.state["project"]
        assert isinstance(project, dict)
        project_id = project["projectId"]
        dossier = json.loads((FIXTURE_ROOT / self.fixture["dossier"]).read_text(encoding="utf-8"))
        tasks = []
        for item in self.fixture["epicTasks"]:
            _, task = self.api.post(f"/api/v1/projects/{project_id}/tasks", {
                "title": item["title"], "body": compact_json({"epic": dossier["epic"]["id"]}),
                "state": "0-backlog", "taskId": self.identity(item["taskId"]),
                "taskKey": self.identity(item["taskKey"]),
            })
            tasks.append(task)
        run_item = self.fixture["runTask"]
        _, run_task = self.api.post(f"/api/v1/projects/{project_id}/tasks", {
            "title": run_item["title"],
            "body": compact_json({"dossier": dossier["id"], "decisionGate": dossier["decisionGate"]}),
            "state": "2-ready", "taskId": self.identity(run_item["taskId"]),
            "taskKey": self.identity(run_item["taskKey"]),
        })
        tasks.append(run_task)
        self.state.update(tasks=tasks, run_task=run_task, dossier=dossier)
        return {"tasks": tasks, "runTask": run_task}

    def step_claim(self) -> object:
        _, claim = self.api.post(f"/api/v1/runners/{self.state['coding_id']}/claims", {
            "runnerId": self.state["coding_id"], "instanceId": self.state["coding_instance"],
            "requestedTtlSeconds": 60, "availableSlots": 1,
        })
        if claim.get("status") != "claimed":
            raise ScenarioFailure(f"Coding claim was not admitted: {claim}")
        self.state["claim"] = claim
        return claim

    def _prepare_repository(self) -> tuple[Path, int, str, str]:
        repository = Path(tempfile.mkdtemp(prefix="deployment-fixture-"))
        shutil.copytree(FIXTURE_ROOT / self.fixture["repository"], repository, dirs_exist_ok=True)
        run_checked(["git", "init", "-b", "main"], repository)
        run_checked(["git", "add", "."], repository)
        run_checked(["git", "-c", "user.name=Deployment Scenario", "-c",
                     "user.email=scenario@example.invalid", "commit", "-m", "test: seed failing fixture"], repository)
        run_checked(["git", "switch", "-c", "task/dsr-3"], repository)
        failing = subprocess.run([sys.executable, "fixture_test.py"], cwd=repository, text=True,
                                 stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False)
        if failing.returncode == 0:
            raise ScenarioFailure("Seeded fixture test was expected to fail before the fake CLI ran.")
        cli = subprocess.run([sys.executable, str(FIXTURE_ROOT / "fake-cli.py"), str(repository)],
                             cwd=repository, text=True, stdout=subprocess.PIPE,
                             stderr=subprocess.STDOUT, check=False)
        log = f"before (expected failure):\n{failing.stdout}\nfake CLI:\n{cli.stdout}"
        (self.output / "evidence" / "fake-cli.log").write_text(log, encoding="utf-8")
        if cli.returncode != 0:
            raise ScenarioFailure(f"Fake coding CLI failed with {cli.returncode}.")
        passing = run_checked([sys.executable, "fixture_test.py"], repository)
        with (self.output / "evidence" / "fake-cli.log").open("a", encoding="utf-8") as handle:
            handle.write("\nafter (expected pass):\n" + passing.stdout)
        result_sha = run_checked(["git", "rev-parse", "HEAD"], repository).stdout.strip()
        tree_sha = run_checked(["git", "rev-parse", "HEAD^{tree}"], repository).stdout.strip()
        return repository, cli.returncode, result_sha, tree_sha

    def step_run_fake_cli(self) -> object:
        repository, exit_code, result_sha, tree_sha = self._prepare_repository()
        claim = self.state["claim"]
        assert isinstance(claim, dict)
        run = claim["run"]
        lease = claim["lease"]
        log_bytes = (self.output / "evidence" / "fake-cli.log").read_bytes()
        run_id = run["runId"]
        common = {
            "fence": lease["fence"], "runnerId": self.state["coding_id"],
            "instanceId": self.state["coding_instance"], "leaseId": lease["leaseId"],
        }
        self.api.post(f"/api/v1/runs/{run_id}/events", {
            "eventId": self.identity("evt-scenario-cli"), "kind": "agent.message",
            "payloadJson": compact_json({"text": "deterministic fixture fixed", "clock": self.definition["clock"]}),
            "idempotencyKey": self.identity("scenario-cli-event"), "sequence": 1,
            "occurredAt": self.definition["clock"], **common,
        })
        self.api.post(f"/api/v1/runs/{run_id}/artifacts", {
            "artifactId": self.identity("art-scenario-cli-log"), "name": "results/fake-cli.log",
            "mediaType": "text/plain", "contentBase64": base64.b64encode(log_bytes).decode("ascii"),
            "sha256": sha256_bytes(log_bytes), "idempotencyKey": self.identity("scenario-cli-log"),
            "sequence": 2, **common,
        })
        self.api.post(f"/api/v1/runs/{run_id}/result-finalization", {
            "attempt": 1, "idempotencyKey": self.identity("scenario-finalization"), **common,
        })
        result_ref = f"refs/heads/agent-studio/results/{run_id}/fence-{lease['fence']}/{result_sha}"
        envelope = {
            "repositoryId": self.identity("repo-deployment-fixture"),
            "sourceRunAttemptId": run_id, "baseSha": "1" * 40, "resultSha": result_sha,
            "immutableRemoteRef": result_ref, "sourceBundleDigest": None,
            "artifactManifestDigest": sha256_bytes(log_bytes), "submodules": [], "lfsObjects": [],
            "repositoryUrl": "https://example.invalid/deployment-fixture.git",
        }
        canonical_envelope = dict(envelope)
        canonical_envelope["repositoryUrl"] = None
        envelope_digest = sha256_bytes(compact_json(canonical_envelope).encode("utf-8"))
        self.api.put(f"/api/v1/runs/{run_id}/result-handoff", {
            **common, "sequence": 3, "idempotencyKey": self.identity("scenario-handoff"),
            "envelopeDigest": envelope_digest, "envelope": envelope,
        })
        self.api.post(f"/api/v1/runs/{run_id}/completion", {
            **common, "outcome": "success", "summary": "deterministic fixture completed",
            "resultEnvelopeDigest": envelope_digest, "idempotencyKey": self.identity("scenario-completion"),
            "sequence": 4,
        })
        project = self.state["project"]
        task = self.state["run_task"]
        assert isinstance(project, dict) and isinstance(task, dict)
        current = self.api.get(f"/api/v1/projects/{project['projectId']}/tasks/{task['taskId']}")
        self.state.update(repository=str(repository), result_sha=result_sha, tree_sha=tree_sha,
                          result_ref=result_ref, envelope=envelope, envelope_digest=envelope_digest)
        return {"exitCode": exit_code, "resultSha": result_sha,
                "taskState": current["state"], "fakeCliLog": "evidence/fake-cli.log"}

    def step_auto_review(self) -> object:
        claim = self.state["claim"]
        assert isinstance(claim, dict)
        run_id = claim["run"]["runId"]
        task = self.state["run_task"]
        assert isinstance(task, dict)
        _, subject = self.api.post("/api/v1/reviews/subjects", {
            "taskId": task["taskId"], "sourceRunId": run_id,
            "repositoryId": self.state["envelope"]["repositoryId"],
            "repositoryUrl": self.state["envelope"]["repositoryUrl"],
            "expectedResultSha": self.state["result_sha"], "resultRef": self.state["result_ref"],
            "sourceBundleArtifactId": None, "sourceBundleSha256": None,
            "codingHostId": self.identity("scenario-coding-host"), "reviewPolicyHash": "4" * 64,
            "plan": {"commands": [{"stepId": "fixture-tests", "aspect": "fixture-tests",
                                      "fileName": "fake-review-cli", "arguments": [], "required": True,
                                      "timeoutSeconds": 30, "compareToBaseline": False,
                                      "executionKind": "tool"}],
                     "requiredAspects": ["fixture-tests"], "requiresVisualReview": False,
                     "requireDifferentHostFailureDomain": False},
            "idempotencyKey": self.identity("scenario-review-subject"),
        })
        _, review_claim = self.api.post(f"/api/v1/runners/{self.state['review_id']}/review-claims", {
            "executorId": self.state["review_id"], "instanceId": self.state["review_instance"],
            "requestedTtlSeconds": 60, "availableSlots": 1,
        })
        if review_claim.get("status") != "claimed":
            raise ScenarioFailure(f"Review claim was not admitted: {review_claim}")
        cli = run_checked([sys.executable, str(FIXTURE_ROOT / "fake-review-cli.py")], ROOT)
        review_bytes = cli.stdout.encode("utf-8")
        (self.output / "evidence" / "fake-review.log").write_bytes(review_bytes)
        stderr_bytes = b""
        stdout_sha = sha256_bytes(review_bytes)
        stderr_sha = sha256_bytes(stderr_bytes)
        lease = review_claim["lease"]
        attempt = review_claim["attempt"]
        workspace_path = f"/review/{lease['resourceNamespace']}"
        report_body = {
            "executorId": self.state["review_id"], "instanceId": self.state["review_instance"],
            "leaseId": lease["leaseId"], "fence": lease["fence"],
            "idempotencyKey": self.identity("scenario-review-report"), "outcome": "Pass",
            "failureClassification": None, "summary": "fixture-tests passed",
            "workspace": {"repositoryId": self.state["envelope"]["repositoryId"],
                          "expectedResultSha": self.state["result_sha"], "actualHead": self.state["result_sha"],
                          "treeHash": self.state["tree_sha"], "dirtyBefore": False, "dirtyAfter": False,
                          "workspaceIdentity": sha256_bytes(workspace_path.encode("utf-8")),
                          "resourceNamespace": lease["resourceNamespace"]},
            "environment": {"hostId": lease["hostId"], "executorId": self.state["review_id"],
                            "instanceId": self.state["review_instance"], "osDescription": "scenario",
                            "architecture": "deterministic", "runtimeVersion": "1",
                            "toolchain": {"runtime": "python", "git": "git;fixture",
                                          "command:fixture-tests": "fake-review-cli;fixture"},
                            "isolation": {"workspace": workspace_path, "cache": workspace_path + "/cache",
                                          "temp": workspace_path + "/tmp",
                                          "ports": f"{lease['portBase']}-{lease['portBase'] + 7}",
                                          "containers": lease["resourceNamespace"],
                                          "databases": lease["resourceNamespace"],
                                          "credentials": "review-read-only"}},
            "commands": [{"stepId": "fixture-tests", "aspect": "fixture-tests",
                          "fileName": "fake-review-cli", "arguments": [],
                          "expectedResultSha": self.state["result_sha"], "headBefore": self.state["result_sha"],
                          "treeBefore": self.state["tree_sha"], "startedAt": FIXED_REVIEW_START,
                          "finishedAt": FIXED_REVIEW_END, "exitCode": 0, "signal": None,
                          "stdoutSha256": stdout_sha, "stderrSha256": stderr_sha,
                          "baselineCacheHit": False, "retryPerformed": False,
                          "phase": "verification", "workspaceRole": "candidate",
                          "dependencyCacheHit": False, "executionKind": "tool",
                          "executionLocation": "remote", "executorId": self.state["review_id"],
                          "hostId": lease["hostId"], "attemptId": attempt["attemptId"]}],
            "artifacts": [
                {"name": "fake-review.stdout.log", "mediaType": "text/plain", "sha256": stdout_sha,
                 "sizeBytes": len(review_bytes), "contentBase64": base64.b64encode(review_bytes).decode("ascii")},
                {"name": "fake-review.stderr.log", "mediaType": "text/plain", "sha256": stderr_sha,
                 "sizeBytes": 0, "contentBase64": ""},
            ],
            "verdicts": [{"aspect": "fixture-tests", "status": "pass",
                          "classification": "Verified", "summary": "fixture test passed"}],
        }
        _, report = self.api.post(f"/api/v1/reviews/attempts/{attempt['attemptId']}/report", report_body)
        _, cleanup = self.api.post(f"/api/v1/reviews/attempts/{attempt['attemptId']}/cleanup", {
            "executorId": self.state["review_id"], "instanceId": self.state["review_instance"],
            "leaseId": lease["leaseId"], "fence": lease["fence"],
            "idempotencyKey": self.identity("scenario-review-cleanup"), "workspaceRemoved": True,
        })
        self.state.update(review_subject=subject, review_claim=review_claim)
        return {"report": report, "cleanup": cleanup, "fakeReviewLog": "evidence/fake-review.log"}

    def step_integrate(self) -> object:
        project = self.state["project"]
        task = self.state["run_task"]
        assert isinstance(project, dict) and isinstance(task, dict)
        repository = Path(str(self.state["repository"]))
        run_checked(["git", "switch", "main"], repository)
        run_checked(["git", "merge", "--ff-only", str(self.state["result_sha"])], repository)
        runs = self.api.get(f"/api/v1/orchestration/runs?projectId={project['projectId']}&status=pending")
        if len(runs) != 1:
            raise ScenarioFailure(f"Expected one pending orchestration run, received {len(runs)}.")
        orchestration = runs[0]
        while orchestration["status"] == "pending":
            stage = orchestration["currentStage"]
            stage_number = stage if isinstance(stage, int) else [
                "reviewDecision", "council", "postProcessing", "gateDispatch", "completionJudge"
            ].index(stage[0].lower() + stage[1:])
            _, claimed = self.api.post("/api/v1/orchestration/claims", {
                "engineId": "scenario-engine", "instanceId": "scenario-engine-instance",
                "supportedStages": [stage_number], "requestedTtlSeconds": 60,
            })
            lease = claimed["lease"]
            action = 3 if stage_number == 4 else 0
            _, orchestration = self.api.post(
                f"/api/v1/orchestration/runs/{orchestration['runId']}/stages/complete", {
                    "engineId": "scenario-engine", "instanceId": "scenario-engine-instance",
                    "leaseId": lease["leaseId"], "fence": lease["fence"], "stage": stage_number,
                    "action": action, "outputJson": "{}",
                    "idempotencyKey": self.identity(f"scenario-stage-{stage_number}"),
                })
        current = self.api.get(f"/api/v1/projects/{project['projectId']}/tasks/{task['taskId']}")
        return {"taskState": current["state"], "resultSha": self.state["result_sha"]}

    def step_complete(self) -> object:
        project = self.state["project"]
        task = self.state["run_task"]
        assert isinstance(project, dict) and isinstance(task, dict)
        current = self.api.get(f"/api/v1/projects/{project['projectId']}/tasks/{task['taskId']}")
        _, completed = self.api.put(f"/api/v1/projects/{project['projectId']}/tasks/{task['taskId']}", {
            "title": None, "body": None, "state": "6-completed", "expectedVersion": current["version"],
        })
        self.state["run_task"] = completed
        return completed

    def step_orchestrator_chat(self) -> object:
        project = self.state["project"]
        assert isinstance(project, dict)
        _, context = self.api.put(f"/api/v1/orchestrator-contexts/projects/{project['projectId']}")
        context_key = context["contextKey"]
        turn_id = self.identity("scenario-context-turn")
        self.api.post(f"/api/v1/orchestrator-contexts/projects/{project['projectId']}/turns", {
            "turn": {"turnId": turn_id, "createdAt": self.definition["clock"], "role": "user",
                     "body": "Confirm deployment scenario context receipt."}
        })
        receipt = {
            "receiptId": self.identity("scenario-context-receipt"), "userTurnId": turn_id,
            "contextKey": context_key, "capturedAt": self.definition["clock"],
            "budget": {"automaticSoftCapTokens": 100, "automaticHardCapTokens": 200,
                       "totalHardCapTokens": 300, "estimatedIncludedTokens": 12},
            "sources": [{"sourceId": "scenario-dossier", "kind": "dossier", "revision": "1",
                         "sha256": sha256_bytes(compact_json(self.state["dossier"]).encode("utf-8")),
                         "freshness": "fixed", "includedCharacters": 42, "estimatedTokens": 12,
                         "status": "included"}],
        }
        self.api.post(f"/api/v1/orchestrator-contexts/projects/{project['projectId']}/turns", {
            "turn": {"turnId": self.identity("scenario-context-answer"),
                     "createdAt": "2026-09-06T10:00:01Z", "role": "orchestrator",
                     "body": "Context received.", "receipt": receipt}
        })
        transcript = self.api.get(f"/api/v1/orchestrator-contexts/projects/{project['projectId']}/turns")
        return transcript

    def step_dossier_decision(self) -> object:
        decision = dict(self.state["dossier"])
        decision["status"] = "approved"
        decision["decisionGate"] = dict(decision["decisionGate"])
        decision["decisionGate"]["selected"] = "approved"
        project = self.state["project"]
        first_task = self.state["tasks"][0]
        assert isinstance(project, dict) and isinstance(first_task, dict)
        current = self.api.get(
            f"/api/v1/projects/{project['projectId']}/tasks/{first_task['taskId']}")
        _, updated = self.api.put(
            f"/api/v1/projects/{project['projectId']}/tasks/{first_task['taskId']}", {
                "title": None, "body": compact_json({"dossier": decision}), "state": None,
                "expectedVersion": current["version"],
            })
        self.state["tasks"][0] = updated
        path = self.output / "evidence" / "dossier-decision.json"
        path.write_text(json.dumps(decision, indent=2) + "\n", encoding="utf-8")
        return {"decision": "approved", "taskVersion": updated["version"],
                "dossierDecision": "evidence/dossier-decision.json"}

    def inventory(self) -> str:
        workspace = self.state["workspace"]
        project = self.state["project"]
        assert isinstance(workspace, dict) and isinstance(project, dict)
        projects = self.api.get(f"/api/v1/projects?workspaceId={workspace['workspaceId']}")
        tasks = self.api.get(f"/api/v1/projects/{project['projectId']}/tasks")
        stable = {
            "workspace": {key: workspace[key] for key in ("workspaceId", "name")},
            "projects": sorted(({key: item[key] for key in ("projectId", "workspaceId", "name", "taskKeyPrefix")}
                                for item in projects), key=lambda item: item["projectId"]),
            "tasks": sorted(({key: item.get(key) for key in ("taskId", "projectId", "taskKey", "title", "state", "body")}
                             for item in tasks), key=lambda item: item["taskId"]),
        }
        return sha256_bytes(compact_json(stable).encode("utf-8"))

    def step_backup(self) -> object:
        self.state["inventory_before"] = self.inventory()
        _, backup = self.api.post("/api/v1/management/backups", {"name": "deployment-scenario"})
        self.state["backup"] = backup
        return backup

    def step_restore(self) -> object:
        backup = self.state["backup"]
        assert isinstance(backup, dict)
        if self.target == "remote":
            _, restored = self.api.post("/api/v1/management/restore", {
                "backupId": backup["backupId"], "verifyOnly": True,
            })
        else:
            if self.target_handle is None:
                raise ScenarioFailure("An isolated target controller is required for destructive restore.")
            self.target_handle.restart_empty(backup)
            self.api.put("/api/v1/management/mode", {
                "mode": 3, "reason": "isolated deployment scenario restore",
            })
            _, restored = self.api.post("/api/v1/management/restore", {
                "backupId": backup["backupId"], "verifyOnly": False,
            })
        return restored

    def step_inventory_hash(self) -> object:
        after = self.inventory()
        return {"before": self.state["inventory_before"], "after": after}


def write_reports(output: Path, definition: dict, target: str, level: str,
                  rows: list[dict], started: float) -> None:
    output.mkdir(parents=True, exist_ok=True)
    duration = time.monotonic() - started
    failures = sum(1 for row in rows if row["status"] == "failed")
    suite = ET.Element("testsuite", {
        "name": definition["id"], "tests": str(len(rows)), "failures": str(failures),
        "errors": "0", "skipped": "0", "time": f"{duration:.3f}",
        "target": target, "level": level,
    })
    for row in rows:
        case = ET.SubElement(suite, "testcase", {
            "classname": f"deployment-scenario.{target}.{level}", "name": row["id"],
            "time": f"{row['duration']:.3f}",
        })
        if row["status"] == "failed":
            failure = ET.SubElement(case, "failure", {"message": row["message"]})
            failure.text = row["message"]
        properties = ET.SubElement(case, "properties")
        ET.SubElement(properties, "property", {"name": "evidence", "value": row["evidence"]})
    ET.ElementTree(suite).write(output / "scenario.junit.xml", encoding="utf-8", xml_declaration=True)

    status = "PASS" if failures == 0 and rows else "FAIL"
    lines = [
        f"# Deployment scenario: {status}", "",
        f"- Definition: `{definition['id']}` (schema {definition['schemaVersion']})",
        f"- Target: `{target}`", f"- Level: `{level}`", f"- Duration: `{duration:.3f}s`", "",
        "| Step | Status | Duration | Evidence |", "|---|---:|---:|---|",
    ]
    for row in rows:
        evidence = row["evidence"]
        evidence_link = f"[{evidence}]({evidence})" if (output / evidence).exists() else evidence
        lines.append(f"| {row['title']} | {row['status']} | {row['duration']:.3f}s | {evidence_link} |")
    failed = [row for row in rows if row["status"] == "failed"]
    if failed:
        lines.extend(["", "## Failure", "", failed[0]["message"]])
    (output / "scenario-report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", required=True, choices=("inproc", "compose", "remote"))
    parser.add_argument("--level", required=True, choices=("smoke", "full"))
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    output = (args.output or Path(os.environ.get("JOB_RESULTS_DIR", ROOT / "results")) /
              "deployment-scenario").resolve()
    definition = json.loads(DEFINITION.read_text(encoding="utf-8"))
    validate_definition(definition)
    selected = [step for step in definition["steps"] if args.level in step["levels"]]
    rows: list[dict] = []
    started = time.monotonic()
    target_handle: LocalTarget | ComposeTarget | None = None
    api: Api | None = None
    scenario: Scenario | None = None
    exit_code = 0
    try:
        if args.target == "inproc":
            target_handle = LocalTarget(output)
            api = target_handle.start()
        elif args.target == "compose":
            target_handle = ComposeTarget(output)
            api = target_handle.start()
        else:
            base_url = os.environ.get("SCENARIO_BASE_URL")
            if not base_url:
                raise TargetUnavailable("SCENARIO_BASE_URL is required for --target remote.")
            shared_token = os.environ.get("SCENARIO_TOKEN")
            api = Api(
                base_url,
                os.environ.get("SCENARIO_STUDIO_TOKEN") or shared_token,
                os.environ.get("SCENARIO_RUNNER_TOKEN") or shared_token,
            )
            api.get("/readyz")
        scenario = Scenario(definition, args.target, args.level, output, api, target_handle)
        for step in selected:
            step_started = time.monotonic()
            evidence = f"evidence/{step['id']}.json"
            try:
                result = scenario.execute(step)
                output.joinpath(evidence).write_text(json.dumps(result, indent=2, default=str) + "\n", encoding="utf-8")
                rows.append({"id": step["id"], "title": step["title"], "status": "passed",
                             "duration": time.monotonic() - step_started, "evidence": evidence, "message": ""})
            except Exception as error:  # report the exact failing step, then stop the ordered scenario
                failure_message = f"{type(error).__name__}: {error}"
                output.joinpath(evidence).write_text(
                    json.dumps({"status": "failed", "error": failure_message}, indent=2) + "\n",
                    encoding="utf-8")
                rows.append({"id": step["id"], "title": step["title"], "status": "failed",
                             "duration": time.monotonic() - step_started, "evidence": evidence,
                             "message": failure_message})
                exit_code = 1
                break
    except TargetUnavailable as error:
        output.joinpath("evidence").mkdir(parents=True, exist_ok=True)
        output.joinpath("evidence/target.json").write_text(
            json.dumps({"status": "failed", "error": str(error)}, indent=2) + "\n", encoding="utf-8")
        rows.append({"id": "target", "title": "Start target", "status": "failed",
                     "duration": time.monotonic() - started, "evidence": "evidence/target.json",
                     "message": str(error)})
        exit_code = 3
    except Exception as error:
        output.joinpath("evidence").mkdir(parents=True, exist_ok=True)
        output.joinpath("evidence/scenario.json").write_text(
            json.dumps({"status": "failed", "error": f"{type(error).__name__}: {error}"}, indent=2) + "\n",
            encoding="utf-8")
        rows.append({"id": "scenario", "title": "Run scenario", "status": "failed",
                     "duration": time.monotonic() - started, "evidence": "evidence/scenario.json",
                     "message": f"{type(error).__name__}: {error}"})
        exit_code = 1
    finally:
        if scenario:
            try:
                scenario.cleanup()
            except Exception as cleanup_error:
                if exit_code == 0:
                    rows.append({"id": "cleanup", "title": "Clean scenario resources", "status": "failed",
                                 "duration": 0.0, "evidence": "evidence/cleanup.json",
                                 "message": f"{type(cleanup_error).__name__}: {cleanup_error}"})
                    exit_code = 1
        if target_handle:
            target_handle.stop()
        write_reports(output, definition, args.target, args.level, rows, started)
    print(output / "scenario-report.md")
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
