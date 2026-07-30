# Parallel remote delivery verification

Run: `canary-parallel-12`. Seed: `agt2200-parallel-v1`.

Verdict: accepted. This is infrastructure verification only; no model or CLI comparison dimension was recorded.

| Scenario | Tasks | Peak active or post | Coding workers | Gate workers | Review workers | Environmental retries | Integration collisions |
|---|---:|---:|---:|---:|---:|---:|---:|
| baseline | 12 | 12 | 3 | 3 | 3 | 0 | 11 |
| worker-loss | 12 | 12 | 3 | 2 | 3 | 4 | 11 |

## Acceptance

- PASS `two-scenarios-recorded`: Baseline and controlled worker-loss scenarios must both be recorded.
- PASS `baseline:at-least-twelve-tasks`: Each scenario must contain at least twelve isolated reference tasks.
- PASS `baseline:ten-active-or-post`: At least ten cards must be observed in active or post-processing state together.
- PASS `baseline:slot-decisions-complete`: Every coding, gate, and review execution needs a recorded admission decision.
- PASS `baseline:multi-worker-execution`: Coding, gates, and reviews must each use more than one worker.
- PASS `baseline:isolated-workspaces`: Workspace paths must be unique and disposable gate/review roots must be removed.
- PASS `baseline:exact-result-sha`: Every gate and review must execute at its declared Result SHA.
- PASS `baseline:delivery-idempotent`: Result handoff and completion must replay idempotently.
- PASS `baseline:review-idempotent`: Review verdict replay must retain one authoritative passing report.
- PASS `baseline:integration-deterministic`: Integration must complete in stable task ordinal order.
- PASS `baseline:no-cross-task-commits`: No result range may contain a commit attributed to another task.
- PASS `baseline:no-product-failures`: Environmental pressure or worker loss must not be counted as product failure.
- PASS `baseline:telemetry-present`: Resource pressure and slot occupancy telemetry must be present.
- PASS `baseline:queues-drained`: No queue item may be lost, duplicated, or left pending.
- PASS `baseline:authority-audit-clear`: Audit sequences must be unique, authority actions drained, and Studio must execute no work.
- PASS `worker-loss:at-least-twelve-tasks`: Each scenario must contain at least twelve isolated reference tasks.
- PASS `worker-loss:ten-active-or-post`: At least ten cards must be observed in active or post-processing state together.
- PASS `worker-loss:slot-decisions-complete`: Every coding, gate, and review execution needs a recorded admission decision.
- PASS `worker-loss:multi-worker-execution`: Coding, gates, and reviews must each use more than one worker.
- PASS `worker-loss:isolated-workspaces`: Workspace paths must be unique and disposable gate/review roots must be removed.
- PASS `worker-loss:exact-result-sha`: Every gate and review must execute at its declared Result SHA.
- PASS `worker-loss:delivery-idempotent`: Result handoff and completion must replay idempotently.
- PASS `worker-loss:review-idempotent`: Review verdict replay must retain one authoritative passing report.
- PASS `worker-loss:integration-deterministic`: Integration must complete in stable task ordinal order.
- PASS `worker-loss:no-cross-task-commits`: No result range may contain a commit attributed to another task.
- PASS `worker-loss:no-product-failures`: Environmental pressure or worker loss must not be counted as product failure.
- PASS `worker-loss:telemetry-present`: Resource pressure and slot occupancy telemetry must be present.
- PASS `worker-loss:queues-drained`: No queue item may be lost, duplicated, or left pending.
- PASS `worker-loss:authority-audit-clear`: Audit sequences must be unique, authority actions drained, and Studio must execute no work.
- PASS `worker-loss:injected`: The repeated scenario must record one controlled worker loss.
- PASS `worker-loss:bounded-redistribution`: Lost gate work must redistribute once while healthy workers continue.
- PASS `worker-loss:honest-capacity`: Evidence must show the reduced eligible capacity after worker loss.

## Evidence map

- `baseline/timeline.jsonl` and `worker-loss/timeline.jsonl`: concurrency, admission, retry, and collision decisions.
- `baseline/telemetry.jsonl` and `worker-loss/telemetry.jsonl`: CPU, memory, load, queue depth, and slot occupancy.
- `*/runtime-events.jsonl`: schema-shaped runtime events for read-only runtime log analysis.
- `*/task-histories.json`, `*/review-attempts.json`, and `*/audit.json`: Task Server authority and idempotency evidence.
- `acceptance.json`: machine-readable invariant result.

