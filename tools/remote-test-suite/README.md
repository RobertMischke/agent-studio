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

## Safe fault injection catalog

`fault-catalog.json` defines the harness-only fault vocabulary and deterministic
operation/occurrence schedules. Production Runner and Task Server binaries do
not import it. A fault manifest is inert unless all of these interlocks hold:

1. The manifest selects a checked-in catalog id.
2. The command includes `--enable-faults`.
3. `--fault-ack` matches the SHA-256 acknowledgement bound to the exact
   scenario, run id, and resolved run root.
4. The harness owns the isolated Task Server. Fault injection refuses
   `--server-url`.
5. The per-run safety marker remains unchanged before every scheduled fault.

Use the dry run to inspect every resource, incident anchor, schedule, and the
run-bound acknowledgement:

```bash
node tools/remote-test-suite/index.mjs \
  --scenario fault-network-and-terminal \
  --seed shipping-v1 \
  --run-id network-terminal-1 \
  --dry-run
```

Then copy the printed acknowledgement into the explicit fault run:

```bash
node tools/remote-test-suite/index.mjs \
  --scenario fault-network-and-terminal \
  --seed shipping-v1 \
  --run-id network-terminal-1 \
  --enable-faults \
  --fault-ack rts-fi-<the-dry-run-value>
```

Fault runs clean their isolated run root automatically after emitting the final
JSON assertion report. Add `--keep` when an operator deliberately needs the
fault journal, outbox, or fixture repositories for inspection. `--cleanup`
retains the original explicit cleanup behavior and is mutually exclusive with
`--keep`. Setup failures, watchdog processes, and Task Server processes are
always cleaned up.

### Manifests

| Manifest | Fault class | Declared terminal |
|---|---|---|
| `fault-task-server-network-blips` | Claim and heartbeat request loss; event, artifact, and completion response loss after commit | Completed after exactly one bounded replay per operation |
| `fault-gate-timeout` | Deterministic command exceeds the watchdog | Process tree reaped, infrastructure timeout recorded, one bounded retry, then Completed |
| `fault-worktree-collision` | Occupied target path for five preparation attempts | Human Review with `worktree-blocked` and the busy path |
| `fault-lost-completion-sentinel` | Sentinel lost while provider completion and immutable result proof remain | Completed from durable proof |
| `fault-interrupted-terminal-marker` | Sentinel and provider terminal proof interrupted | Human Review with `ProtocolInconclusive`; no integration |
| `fault-network-and-terminal` | Network blips plus lost sentinel | Completed after replay and durable-proof recovery |
| `fault-network-and-gate-timeout` | Network blips plus gate timeout | Completed after both bounded recoveries |

Claim loss is injected before send because the claim contract has no
idempotency key. Event, artifact, and completion loss is injected after the
Task Server commits the first delivery. Their retry therefore proves canonical
idempotent replay instead of merely exercising a request that never arrived.

Every final report asserts lane, lease/fence and duplicate-claim count, process
reaping, worktree isolation and foreign-path preservation, outbox monotonicity
and backlog, exact server-side evidence copies, Result-SHA continuity, and the
declared incident outcome. A failed assertion fails the harness even when the
fixture's product tests passed. Token counts may be attached as run telemetry,
but they never affect these acceptance rules.
