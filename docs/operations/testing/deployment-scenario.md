# Deployment regression scenario

The deployment regression scenario is the shared release proof for the Task
Server, Studio BFF, Runner, and Docker deployment surfaces. Every deployment
card runs the smoke level. A tagged release also runs the full Compose level
next to the existing release-topology tests.

## What it proves

The scenario owns one initially empty scenario store and one tiny Git origin.
The repository starts with `answer.txt` set to `41`, so `verify.sh` fails. The
fixed fake coding CLI writes `42`, runs the check, creates the known commit, and
publishes a log through the normal Runner result channel.

The ordered definition is
[`testsupport/scenario/scenario.json`](../../../testsupport/scenario/scenario.json).
Its fixed seed is
[`testsupport/scenario/fixture.json`](../../../testsupport/scenario/fixture.json).
Smoke runs the first six steps: protocol and principal bootstrap, coding and
review registration, project/task creation, claim, coding, and Remote Review.
Full additionally covers integration and completion, an orchestrator turn with
a context receipt, the Dossier decision gate, backup/restore, and logical
inventory hash equality.

Visible fixture time and fake CLI output are fixed. Polling uses 100 ms bounded
checks and no arbitrary sleep. A failed assertion stops the scenario, writes a
report, and exits `1`; invalid arguments or a missing target dependency exit
`2`; success exits `0`.

## Run it

From Git Bash on Windows or a POSIX shell on Linux:

```bash
scripts/scenario.sh --target inproc --level smoke
scripts/scenario.sh --target inproc --level full
```

`inproc` builds and starts Task Server, Studio BFF, and the real Runner as owned
child processes. It needs .NET 10 and Git, but no Docker. CI runs smoke twice
consecutively on Windows and Linux. Both runs must pass.

On a Docker host:

```bash
scripts/scenario.sh --target compose --level full
```

The Compose target starts the default and `distributed` services from
`docker-compose.yml`, performs the former Compose smoke health/API assertions,
and runs the Runner plus both fake CLIs from the hermetic scenario image. The
command owns its Compose project and volumes and removes them on exit. Override
its ports with `SCENARIO_UI_PORT`, `SCENARIO_API_PORT`,
`SCENARIO_TASK_SERVER_PORT`, and `SCENARIO_BFF_PORT` when needed.

Against a deployed control plane:

```bash
SCENARIO_URL=https://studio.example.test \
SCENARIO_TOKEN=replace-with-scenario-credential \
scripts/scenario.sh --target remote --level full
```

Remote creates a uniquely suffixed scenario project, archives both scenario
tasks during cleanup, and never restores over the deployed store. Its backup
step uses the Task Server's verify-only restore contract. The credential needs
the same scenario-scoped API permissions as a coding runner, review runner,
Studio client, and backup verifier. Do not use a broad operator credential when
a scenario credential is available.

## Read and retain the report

Output defaults to
`$JOB_RESULTS_DIR/deployment-scenario-<target>-<level>` when the task runner
exports `JOB_RESULTS_DIR`, otherwise to the repository's ignored `results/`
directory. `--output DIR` overrides it. Each run writes:

- `scenario-report.md`, with status, duration, and a relative evidence link for
  every step;
- `scenario-junit.xml`, the stable CI result;
- `evidence/<step>.json`, including the step's typed assertions and observed
  values;
- `compose.log` on a failing Compose run.

Deployment cards attach the Markdown report and its evidence directory to card
status. CI consumes the JUnit file and also uploads the complete directory.

## Extend it

When a deployment feature adds an observable contract:

1. Add exactly one ordered step to `scenario.json`. Put it before step six when
   it must block smoke; otherwise add it to full.
2. Give every assertion one supported type: `httpStatus`, `jsonEquals`,
   `fileExists`, `gitCommit`, or `sha256Equals`. Add a new assertion type only
   when none of these expresses the contract.
3. Add stable seed data to `fixture.json` or `fixture/repository/`. Do not create
   a second scenario or target-specific seed.
4. Implement the step once in the target-neutral dispatcher in `Program.cs`.
   Target branches are limited to lifecycle needs such as process startup,
   Compose cleanup, authentication, and remote non-destructive cleanup.
5. Run smoke twice and the affected full target. Confirm that a deliberately
   broken expected value makes the command non-zero and produces a failed JUnit
   testcase before restoring the expected value.

This directory is the deployment regression suite. Older entry points, such as
`scripts/compose-smoke-test.sh`, remain compatibility wrappers only.
