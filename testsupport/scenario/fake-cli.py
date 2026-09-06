#!/usr/bin/env python3
"""Deterministic coding CLI used only by the deployment regression fixture."""

from pathlib import Path
import subprocess
import sys


def main() -> int:
    repository = Path(sys.argv[1]).resolve()
    (repository / "result.txt").write_text("pass\n", encoding="utf-8")
    subprocess.run(["git", "add", "result.txt"], cwd=repository, check=True)
    subprocess.run(
        [
            "git",
            "-c",
            "user.name=Deployment Scenario",
            "-c",
            "user.email=scenario@example.invalid",
            "commit",
            "-m",
            "fix: make deployment fixture pass",
        ],
        cwd=repository,
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
    )
    print('{"type":"agent_message","text":"deterministic fixture fixed"}')
    print('{"type":"tool","name":"fixture-write","path":"result.txt"}')
    print("[[TASK_DONE]]")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
