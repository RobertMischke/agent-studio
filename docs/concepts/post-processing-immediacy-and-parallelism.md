# Post-processing: immediate re-sorting, real parallelism

**Status:** concept 2026-07-08 (operator directive: *"der Orchestrator soll im
Post-Processing sofort zuschlagen — boom, los geht's"*). Grounded in three
same-day exhibits. Related:
[`multichat-orchestrator.md`](multichat-orchestrator.md) (the session
registry is the enabler), [`out-of-band-task-completion.md`](out-of-band-task-completion.md),
board cards AGT-1942/1943.

## 1. Observed behavior (2026-07-08, all in one evening)

| Exhibit | What happened | Why it's wrong |
|---|---|---|
| A (AGT-1937) | Build gate hit an *environmental* lock (MSB3021: running dev backend locks the exe in the shared checkout), failed twice → **parked in 5e** | An environmental signature is retryable; escalation treated it like a code defect. Card sat until a human/agent noticed |
| B (AGT-1917) | Run died; escalation said "no summary" although `results/` held deliverables | Escalation doesn't look at what exists — the card looks lost |
| C (log) | `auto-review-postprocessing-enqueued → started → finished`, strictly sequential per project (0.7–75 s each); decisions run through the single per-project orchestrator session; a supervisor AutoIntervention **paused the whole runner** and never self-released | Post-processing throughput is serialized; interventions punish the project instead of the card |

## 2. Target principles

1. **Every terminal event routes immediately.** A finished run or failed
   gate produces a routing decision *in the same breath* — reissue, retry,
   ready, human-review, escalate. No parking without classification.
2. **Outcome taxonomy** (the decision table):
   - `success` → auto-review → 5-human-review.
   - `code-defect` (gate red with real errors) → reissue, bounded (n=2),
     then human-review *with the gate output attached*.
   - `environmental` (lock/`MSB302x`, quota/rate-limit, network, invalid
     model 400) → **retry with backoff, never escalate**; after n retries →
     human-review flagged "environment", not 5e.
   - `inconclusive-with-results` (dead run, `results/` non-empty) →
     human-review with results surfaced (fixes Exhibit B).
   - `inconclusive-empty` → 5e-escalated (the only true 5e case).
3. **Post-processing is parallel per task.** Each task's post-processing
   runs in its **own orchestrator context** — exactly the MC-1a/1b session
   registry (context key `task:<id>`), bounded by the shared active-process
   cap. The per-project singleton queue disappears.
4. **Post-processing never holds an execution slot.** The runner slot frees
   on CLI exit; routing/review runs concurrently with the next pickup.
5. **Interventions target cards, not the runner.** A supervisor inspection
   pins the *card* (blocked-pending-inspection), not the project mode; and
   it self-releases when its trigger resolves (today's pause survived its
   own cause by an hour).

## 3. Implementation cut

- **Quick win (independent, do first):** outcome taxonomy + environmental
  retry in the existing gate/escalation path (Exhibits A+B; overlaps
  AGT-1942/1943 — same code sites).
- **Parallelism (after MC-1a/1b):** move post-processing decisions onto
  per-task orchestrator sessions; delete the per-project queue.
- **Intervention scoping:** card-level pin + self-release (separate small
  change in the supervisor).
