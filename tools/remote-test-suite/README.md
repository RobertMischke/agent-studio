# Remote test suite core

This repository-owned harness verifies Remote Run infrastructure. It does not
compare models or CLIs, execute benchmarks, or rank results. A run may retain
token counts as telemetry, but tokens never affect acceptance.

Run a scenario with a stable seed and a unique run id:

```bash
node tools/remote-test-suite/index.mjs \
  --scenario divergent-salvage-lineage \
  --seed historical-agt-2177 \
  --run-id "$(date -u +%Y%m%dT%H%M%SZ)"
```

Every command starts an isolated Task Server and provisions resources through
`/api/v1`. Runner actions use the real fenced claim, lease, event, immutable
Result-SHA handoff, completion, and review contracts. The restart replay also
launches the real `agent-host` daemon around a controlled detached worker. The
harness never writes under the managed task workspace.

| Scenario | Historical boundary | Expected terminal | Recovery budget |
|---|---|---|---|
| `reference-change` | Deterministic protocol reference | `6-completed` | 0 reference recoveries |
| `divergent-salvage-lineage` | Divergent canonical and host lineage, contaminated and stale review scopes | `5-human-review` | 3 review attempts |
| `lease-adoption-restart` | Live, dead, and PID-mismatched generations across daemon restart | `4-auto-review` | 1 live-generation adoption |
| `external-completion-cycle` | Idempotent external completion, requeue replay, and failed stranded salvage | `2-ready` | 1 post-completion requeue |

Each manifest declares its hardening-chronicle links, expected terminal,
bounded recovery budget, and machine assertion IDs. The executor must satisfy
every declaration before it can report `accepted: true`. The complete evidence
record is written to
`.tmp/remote-test-suite/<scenario>/<run-id>/result.json`.

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

The real protocol integration tests are opt-in because they build and host the
Task Server, and the restart replay also builds and launches the Runner:

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
