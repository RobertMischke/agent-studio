# Remote test suite core

This repository-owned harness verifies Remote Run infrastructure. It does not
compare models or CLIs, execute benchmarks, or rank results. A run may retain
token counts as telemetry, but tokens never affect acceptance.

Run the deterministic reference scenario with a stable seed and a unique run
id:

```bash
node tools/remote-test-suite/index.mjs \
  --scenario reference-change \
  --seed shipping-v1 \
  --run-id "$(date -u +%Y%m%dT%H%M%SZ)"
```

The command starts an isolated Task Server, provisions its workspace, project,
and task through `/api/v1`, claims the task through the real fenced Remote Run
protocol, journals and replays the immutable Result-SHA handoff, runs the
semantic gate, performs fresh exact-SHA review, and integrates only that
reviewed SHA into the fixture repository. It never writes under the managed
task workspace. The run root defaults to
`.tmp/remote-test-suite/reference-change/<run-id>`.

Add `--cleanup` to remove the isolated run root after success. Setup rollback
and cleanup are idempotent. To inspect the full resource plan without creating
anything, add `--dry-run`; its JSON lists every resource the run would create
and destroy.

Each manifest exposes `claim`, `run`, `gate`, `review`, and `integration`
hooks. A hook is an argv array and runs both before and after its phase with
`REMOTE_TEST_PHASE`, `REMOTE_TEST_HOOK_POINT`, `REMOTE_TEST_RUN_ID`, and
`REMOTE_TEST_SEED`. Hooks observe or inject infrastructure conditions; they do
not add test branches to the production scheduler.

Fast contract tests:

```bash
npm --prefix tools/remote-test-suite test
```

The real protocol integration test is opt-in because it builds and hosts the
Task Server:

```bash
REMOTE_TEST_SUITE_INTEGRATION=1 npm --prefix tools/remote-test-suite test
```

## Three-unit Docker Compose harness

The remote-host harness provisions a separate Task Server, deterministic Agent
Runner protocol process, and production Studio UI. A small TCP proxy is the only
supporting service. It supplies named Runner and Studio links so partitions are
deterministic and do not disconnect or enumerate unrelated Docker networks.

Inspect every resource and lifecycle operation without changing Docker:

```bash
node tools/remote-test-suite/compose-harness.mjs inspect \
  --run-id "$(date -u +%Y%m%d-%H%M%S | tr '[:upper:]' '[:lower:]')"
```

Run the complete acceptance workflow on a Docker host such as
`agent-runner-01`:

```bash
node tools/remote-test-suite/compose-harness.mjs run \
  --run-id "$(date -u +%Y%m%d-%H%M%S)"
```

That one command uses the explicit `remote-integration` Compose profile,
builds and health-gates the isolated stack, and runs `reference-change`. It then
holds two already-claimed slots through a real Task Server partition lasting at
least ten wall-clock minutes. The slots journal useful work through the outage;
a waiting third task remains unclaimed; and recovery must reconcile each exact
fence before idempotently replaying events, artifacts, Result SHAs, terminal
reports, and completions. The workflow separately replaces Runner and Task
Server after safely claiming the same third task that remained Ready through
the outage. The Task Server update must quarantine the old attempt, reject its
stale completion, and accept a higher-fenced recovery only after the old Runner
container is positively gone. It exports periodic and asserted autonomy
timelines, versions, logs, container inspection, API history, audits, invariant
state, and assertions below
`.tmp/remote-test-suite/compose/<run-id>/evidence/`. Finally it drains the
server, proves there is no unresolved authority, and removes only containers,
volumes, networks, and images under the run's harness identity. Add `--keep`
only for interactive diagnosis.

The unit controls are available after `up`:

```bash
node tools/remote-test-suite/compose-harness.mjs up --run-id rehearsal-01
node tools/remote-test-suite/compose-harness.mjs control partition runner --run-id rehearsal-01
node tools/remote-test-suite/compose-harness.mjs control heal runner --run-id rehearsal-01
node tools/remote-test-suite/compose-harness.mjs control replace studio --run-id rehearsal-01
node tools/remote-test-suite/compose-harness.mjs down --run-id rehearsal-01
```

`stop`, `restart`, `replace`, `partition`, and `heal` accept `studio`,
`task-server`, or `runner`. Task Server stop, restart, and replacement first
enter `Draining` and call `prepare-shutdown`. They refuse unresolved authority
unless `--force` is explicit. Successful restart and replacement wait for
readiness before returning Task Server to `Normal`; stop deliberately leaves
the persisted mode unchanged. The full acceptance workflow uses a forced
restart only to verify the documented fail-closed recovery contract.

The Docker scenario stays opt-in:

```bash
REMOTE_TEST_COMPOSE_INTEGRATION=1 npm --prefix tools/remote-test-suite test
```

Routine `npm test` runs only fake and plan-level checks. The harness never
selects a model or provider CLI, runs a benchmark, or compares models. The
Runner image contains the production `agent-host` binary for version capture,
but its deterministic harness process drives only the public lease protocol.
