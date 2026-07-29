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
