# Decision history — review pipeline health

Newest first. Every entry: date · decision · reasoning · card/commit reference.

## 2026-07-23 — Operator requeue opens a fresh review cycle (epoch semantics)
The stale-verdict guards anchored on the card's whole decision history; an
operator move out of `5e-escalated` was instantly re-escalated from pre-requeue
artifacts (requeue ping-pong). Decision: the card's most recent recorded
transition INTO `4-auto-review` (provenance, single MoveAsync hook) is the
**review-cycle epoch** — older verdicts are a closed cycle: never backfilled,
never counted as this cycle's verdict. Automatic moves never bump the epoch, so
loop protection stays intact. → AGT-2260 (full scope: artifact rotation,
attempt-epoch UI), delivered core in `fix: operator requeue opens a fresh review cycle`.

## 2026-07-23 — SSH-gate bridge: gates run on the remote host (shortcut)
Serial local gates (15–27 min each, repo contention, load flakes) were the
throughput floor. Decision: bridge now, clean solution after — the gate locates
the subject SHA in the host's per-project repos, runs the build-profile commands
over ssh in a disposable worktree (2 in parallel, remote-side timeout), falls
back to local on any remote infrastructure problem. Full suite: **2 min 11 s**
remote vs 15–27 min local. Explicitly a self-contained bridge: superseded by
claimable remote gate steps (AGT-2229), then removed wholesale (cleanup card
`ssh-gate-shortcut-cleanup`). → AGT-2222 tranche 2.

## 2026-07-23 — Host slots 5 → 20 (operator-directed)
The remote host idled at load 0.34 with 12 CPUs/62 GB while 5 slots capped the
fleet; runs are LLM-bound. Raised `RUNNER_MAX_PARALLELISM` to 20; watch host
load, trim to 14–16 if simultaneous build phases thrash.

## 2026-07-23 — Build-profile filter carries the poison-class excludes
MachineBound taggings only reach branches that contain them (stale-branch trap);
the build-profile test filter applies to EVERY gate regardless of branch age.
Decision: known flaky/self-blocking test classes are excluded in the profile
filter until gates test the subject merged onto current develop (AGT-2258),
then the filter shrinks back. Trade-off consciously accepted: profile-level
excludes are a blunt instrument, tracked for rollback.

## 2026-07-23 — Post-processing admission is derived, not pinned
A forgotten `PostProcessing:MaxParallelism = 3` override silently beat the
capacity derivation (cores/2, clamped 3–12) and starved whole projects out of
the queue. Decision: convention over settings — the derivation is the default,
the key remains only as a conscious override. → commit
`fix: drop fixed post-processing parallelism override`.

## 2026-07-22 — Two-pool admission (build vs api) is the target design
Pipeline steps declare a resource class: build (coding runs + gates, build
slots), api (LLM steps, own pool), mini (free). Admission by lane age with
project fairness. → AGT-2222 remainder, AGT-2229 host orchestrator.

## 2026-07-22 — MachineBound is the quarantine convention for load-sensitive tests
Tests that fail under machine load but pass at idle get
`[Trait("Category","MachineBound")]` and are excluded from gate runs
(`Category!=MachineBound`). Reference commits: `227b7041`, night-flake tail
additions of 22./23.07.
