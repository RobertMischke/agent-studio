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
