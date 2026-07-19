# Runtime log analysis: agent-taskboard / sample-job / run 1

> **Verdict:** Failing - 3 repeated http.request.failed errors, 1 slow operation, and 1 test passed while runtime emitted level=Error in the same correlationId.
> **Window:** 2026-05-06T12:00:00.000Z .. 2026-05-06T12:01:55.000Z
> **Inputs:** 17 structured events from 1 file; 0 parse warnings; test artefacts present? yes

## Repeated errors

- `http.request.failed` x3 (System.IO.IOException, code 502, retryable=true) - all on `GET /api/tasks/x`. See evidence #1.

## Slow operations

- `backend.GET /api/cli/quota` p95 = 812ms (n=2). Slowest sample at evidence #2.

## Noisy events

- `queue.tick` accounts for 9 of 17 events (53%). Likely Trace/Debug noise that escaped to Info. See evidence #3.

## Suspicious sequences

- `order.shipped` for `correlationId=corr-3` was emitted without a preceding `order.placed`. Known invariant violation. See evidence #4.

## Tests-passed-with-runtime-errors

- Spec `frontend/e2e/orders.spec.ts` reported green for `correlationId=corr-2`, but the same correlationId emitted three `http.request.failed` events with `level=Error`. See evidence #1 and the test attachment in evidence #5.

## Notes

The `queue.tick` burst is concentrated in a single second; it is more likely a noisy emitter than a real load spike. No data-loss-class events observed.

## Evidence

1. Repeated http.request.failed cluster - `sample-job:1:3-5`
2. Slow GET /api/cli/quota - `sample-job:1:16`
3. queue.tick burst - `sample-job:1:7-15`
4. Suspicious order.shipped without order.placed - `sample-job:1:6`
5. Playwright spec attachment - `sample-job/results/runtime/orders.spec.jsonl`

## Follow-up suggestions

1. Investigate the 502 cluster against `api.example.com` and add a regression probe.
2. Demote `queue.tick` to Trace at the producer.
3. Either flip the orders test to assert no runtime errors in the bound correlationId, or attach the runtime stream to the test failure path so the suite cannot pass while errors fire.
