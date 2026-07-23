# Incident: gate churn and the wedged night — 22./23.07.2026

**Impact:** The 4-auto-review lane grew from ~55 to ~65 cards overnight while
net drain was ~1 card. Roughly a dozen cards were falsely escalated; one card
burned 46 lane transitions. Root causes were fully diagnosed by the morning;
throughput recovered the same day (remote gates: ~20 cards/h).

## Timeline (local time)

| When | What |
|---|---|
| 22.07. evening | Review queue builds up; gates serialized on the local machine lock (15–27 min each). |
| ~23:00 | Post-processing admission widened (3 → derived 8) and LLM budget raised (30 → 120/h) — first structural relief (AGT-2222 tranche 1). |
| 00:00–03:00 | **Flake churn:** gates test card branches whose old test suites lack the MachineBound taggings; identical SHAs fail with *different* fingerprints (load flakiness). Reissues rebuild on the same stale base — AGT-2135 reaches 46 transitions. |
| ~03:15 | Containment: remaining flaky tests tagged; **build-profile filter** carries all poison-class excludes (branch-independent). A real defect surfaces: the lane move reports Success while the source folder survives (→ AGT-2255). |
| 06:37 | **The wedge:** gate `7bbed536` acquires the machine lock, prepares its workspace — and never spawns a process. No watchdog exists for the post-acquisition phase. |
| 06:37–09:15 | The lock stays held in-process for 2.5 h; ~12 queued gates die at the 62-min queue SLA (`testedSha=n/a`) and are **counted as card failures**, consuming anti-churn budgets and escalating cards. |
| 09:15 | Backend restart clears the lock. Requeued victims are instantly re-escalated: the stale-verdict sweep backfills pre-requeue verdicts — **stale artifacts outrank the operator** (requeue ping-pong). |
| 10:00–10:30 | Quality Studio found starved end-to-end: dead host claims (9 h, no process), a pinned `MaxParallelism=3` override beating the derived admission, FIFO without project fairness, and `waitsOn` chains behind the unprocessed review cards. |
| 10:30 | Host slots raised 5 → 20 (host was at load 0.34). |
| 10:54 | **SSH-gate bridge live:** first two remote gates run in parallel on the host; full suite in 2 min 11 s. |
| 11:04 | First green remote gate. Drain rate jumps to ~20 cards/h. |
| ~12:30 | **Epoch fix live:** operator requeues open a fresh review cycle; the escalated pile (26) is dispositioned to zero. |

## Root causes (one family)

Derived state without a feedback loop to reality:

1. **Stale-branch trap** — gates test branch tips with outdated test suites; develop-side fixes never reach in-flight branches. → AGT-2258 (gate tests subject merged onto current develop).
2. **Post-acquisition wedge** — no watchdog between lock acquisition and process spawn; queue-SLA victims counted as card failures. → AGT-2257.
3. **Stale artifacts outrank the operator** — verdict backfill ignored that a human had deliberately moved the card back. → AGT-2260 (epoch core delivered 23.07.).
4. **Zombie claims** — no heartbeat leases; dead claims hold slots for hours. → AGT-2182/2185/2186.
5. **Silent starvation** — a config override, FIFO ordering and zero sensors; every signal was visible in existing data, nothing reported it. → AGT-2259 (health sensors), AGT-2222 remainder (fairness).

## What worked

- Teardown protection (AGT-2147) prevented data loss across daemon restarts.
- The queue-wait budget decoupling (AGT-2182 line) kept legitimate waits alive.
- Build-profile filter as branch-independent containment.
- The ssh-gate bridge converted the bottleneck within hours (deliberate shortcut
  with a tracked cleanup card — "bridge first, clean solution after, no residue").

## Verification hook

AGT-2259 requires: replaying this night's logs against the new sensors must
raise all three alarms (lock held without completion, cross-card fingerprint
repetition, drain-rate collapse) within minutes.
