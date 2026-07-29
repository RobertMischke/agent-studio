# Remote three-unit Compose test harness

Use this harness to verify Remote Run infrastructure on a Docker host without
touching the live Agent Studio or stable runner stack. It provisions exactly
three product units:

- Task Server with a disposable authenticated SQLite authority store.
- Agent Runner image with a deterministic protocol process and durable harness
  workspace.
- Production Agent Studio UI routed to Task Server.

One supporting TCP proxy supplies deterministic Runner and Studio network
links. It does not schedule work or own lifecycle state.

## Safety boundary

Every run id produces a Compose project named `agt2394-rts-<run-id>`. Container
labels, image names, one network, two volumes, four loopback ports, a random
disposable bearer credential, and the evidence root all carry that identity.
The harness refuses invalid run ids, duplicate ports, existing identity
resources, or teardown of a Compose container without the matching harness
label.

Defaults are bound only on loopback:

| Resource | Default |
|---|---:|
| Task Server | `127.0.0.1:19741` |
| Studio | `127.0.0.1:19742` |
| Fault control | `127.0.0.1:19743` |
| Runner control | `127.0.0.1:19744` |
| Evidence | `.tmp/remote-test-suite/compose/<run-id>/evidence/` |

Use `--task-server-port`, `--studio-port`, `--fault-control-port`, and
`--runner-control-port` when the host reserves this block. The four values must
be distinct.

The harness never enumerates containers by a broad name pattern and never
mounts a live task workspace. Teardown selects the exact Compose project after
verifying the harness identity label. It removes the project-scoped images,
containers, network, volumes, and ephemeral credential files while preserving
exported evidence.

## Inspect without changing Docker

From the active development checkout:

```bash
node tools/remote-test-suite/compose-harness.mjs inspect --run-id rehearsal-01
```

The JSON output lists names, ports, paths, labels, and the acceptance sequence.
It creates nothing.

## One-command acceptance

Docker Engine and Docker Compose v2 are the only host prerequisites. Run:

```bash
node tools/remote-test-suite/compose-harness.mjs run \
  --run-id "$(date -u +%Y%m%d-%H%M%S)"
```

The command explicitly enables the `remote-integration` profile and performs a
bounded workflow:

1. Validate the Compose model, build images, start services, and wait for every
   readiness check.
2. Capture repository, Docker, Compose, component, runtime, and image versions.
3. Run the deterministic `reference-change` task through public Task Server
   claim, handoff, review, and history APIs.
4. Hold a second task under an active fenced lease.
5. Partition and replace Studio. The Runner must keep renewing the same run and
   fence, proving Studio does not own or cancel execution.
6. Partition and replace Runner. Its disposable volume must reattach the same
   active run and fence without another claim.
7. Partition Task Server from both clients, heal it, then exercise a Task
   Server replacement. `prepare-shutdown` must first report unresolved
   authority. Restart must quarantine the attempt as `process-unknown`.
8. Replace the old Runner container to provide positive no-overlap evidence,
   fence the unknown attempt, and claim one higher-fenced recovery.
9. Prove a stale completion from the old fence is rejected, release the
   recovery task back to Ready, and assert empty authority and delivery
   backlogs.
10. Export evidence and tear down only the run's identity.

Startup, readiness, component operations, evidence commands, and teardown all
have explicit time bounds. A failed run still attempts evidence capture and
identity-scoped cleanup. Use `--keep` only when a failed stack must remain
available for interactive inspection.

The Task Server recovery expectation is deliberately fail-closed. The current
contract does not claim that an in-flight process survives Task Server restart.
It preserves the durable fence, reports process state as unknown, and requires
positive containment proof before a higher-fenced replacement can run.

## Parallel delivery load

The sibling protocol harness verifies horizontal slot and post-processing
behavior without adding scheduler-only branches:

```bash
node tools/remote-test-suite/parallel-harness.mjs \
  --run-id "$(date -u +%Y%m%dT%H%M%SZ)" \
  --seed parallel-reference-v1 \
  --export-root "$JOB_RESULTS_DIR/parallel-delivery"
```

It runs two isolated 12-task scenarios against one disposable Task Server.
The baseline admits twelve coding claims before releasing their start barrier,
then uses independent coding, gate, and review worker pools. The repeat kills
one live four-slot gate worker and redistributes each cancelled gate once while
healthy workers continue. Integration preparation is concurrent, but stale
base collisions resolve in deterministic task ordinal through exact reviewed
Result SHAs.

`acceptance.json` is the machine-readable verdict.
`concurrency-report.md`, the per-scenario timelines, pressure samples, Task
Server histories, reviews, audits, and invariants are the review surface. The
runtime-event stream stays separate from the detailed harness timeline. No
model comparison, leaderboard, or benchmark dimension is present.

## Manual unit controls

For diagnosis, keep a named stack up:

```bash
node tools/remote-test-suite/compose-harness.mjs up --run-id rehearsal-01
node tools/remote-test-suite/compose-harness.mjs control restart studio --run-id rehearsal-01
node tools/remote-test-suite/compose-harness.mjs control partition runner --run-id rehearsal-01
node tools/remote-test-suite/compose-harness.mjs control heal runner --run-id rehearsal-01
node tools/remote-test-suite/compose-harness.mjs down --run-id rehearsal-01
```

Valid operations are `stop`, `restart`, `replace`, `partition`, and `heal`.
Valid units are `studio`, `task-server`, and `runner`.

Task Server stop, restart, and replacement use drain-before-update. They stop
when `prepare-shutdown` reports unresolved authority. `--force` is reserved for
the explicit fail-closed recovery rehearsal above, where the resulting
`process-unknown` attempt is an asserted outcome rather than hidden damage.
Restart and replacement wait for readiness, then return Task Server to
`Normal`. Stop leaves its persisted drained or maintenance mode intact. The
`down` command applies the same guarded Task Server stop before removing the
identity-scoped stack.

## Evidence and test profile

The evidence folder contains:

- `acceptance.json` with machine-readable assertions and old/new fence facts.
- `versions.json` with source, Docker, Compose, component, runtime, and image
  identity.
- Task Server status, audit, invariant, outbox, and task-history snapshots.
- Per-service logs and redacted container inspection.
- The reference-task result, Compose source, resource plan, and teardown proof.

Credentials are redacted from inspection and logs, then removed. Token counts
may appear only if a future protocol fixture records them as per-run telemetry;
they never influence acceptance.

Routine tests use fakes and pure command plans:

```bash
npm --prefix tools/remote-test-suite test
```

The real Docker workflow requires explicit opt-in:

```bash
REMOTE_TEST_COMPOSE_INTEGRATION=1 npm --prefix tools/remote-test-suite test
```

The harness performs no model or CLI comparison, benchmark, ranking, or model
selection. See the
[Remote test suite README](../../../tools/remote-test-suite/README.md) for the
scenario manifest contract.
