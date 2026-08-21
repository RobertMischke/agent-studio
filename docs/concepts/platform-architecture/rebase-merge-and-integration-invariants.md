# Rebase, merge, and promotion: the attribution-lens invariants

**Status.** Concept, extracted from a decision-pending dossier. The mechanics
described below are **already live in code today**; what remains pending
operator sign-off is *blessing the hybrid as intentional policy* and the
forward-looking automation slices (see
[What this page leaves open](#what-this-page-leaves-open)). This page
documents only the parts that are both decided (already the running
behavior) and implemented.

**Source dossier.**
[`docs/operations/rebase-merge-and-steering/index.html`](../../operations/rebase-merge-and-steering/index.html)
(AGT-W37, source card AGT-2662, status `decision-pending`). That dossier's
framing device, "one attribution lens," is the organizing idea reused here.

**Date.** 2026-08-21.

## Why this exists

Git history for a task moves through three different stages, and each stage
makes a different, *correct* choice about whether to preserve or rewrite
commit SHAs:

- A fresh or requeued agent recovery round **rewrites** the delivery (rebases
  onto current `origin/<integration-branch>`).
- Canonical integration into the integration branch **preserves** the
  reviewed delivery's SHAs wherever possible.
- Promotion from the integration branch to `main` **publishes an exact,
  unmutated SHA**.

Read superficially, that looks like the platform applying three different Git
policies inconsistently. It is not. All three answers fall out of one
governing fact: **acceptance evidence, the review subject, and the promoted
release are all identity claims about specific commit SHAs**, and the
correctness of each stage depends on whether an identity claim has already
been made about the SHAs it is touching.

## The attribution lens

Three objects carry identity through the pipeline, and each stage's Git
operation is judged by whether it keeps that object's claim true:

- **Acceptance evidence** — the active, non-superseded `task.json commits[]`
  entries (see [`docs/system/domains/tasks.md`](../../system/domains/tasks.md)).
  No identity claim has been made yet before review.
- **Review subject** — one fenced result ref and one expected head SHA; the
  branch must still resolve to that SHA when fetched for integration. The
  claim starts here.
- **Promoted release** — the exact SHA published to `main`; a merge, squash,
  or rebase after the gate would test one object and release another.

Because a **fresh recovery round has not yet made a review claim**, rewriting
its SHAs (rebase) is safe and in fact the simplest linear candidate for the
next review. Because **canonical integration operates on a delivery whose
SHAs are already named by review/acceptance evidence**, it must prefer
operations that keep those SHAs reachable (merge), and may only rewrite them
(rebase) when it can atomically persist an exact one-to-one replacement map,
otherwise it must refuse and hand the case back for a fresh round rather than
silently break the evidence chain. Because **promotion is publishing a gate
result**, it must be the literal tested SHA, never a new object.

This is the same lens the
[commit/push doctrine](../../operations/git/commit-push-doctrine.md) applies
one layer down ("the recorded SHA must be the one stamped on the job"); that
doctrine owns *who* is allowed to create a commit (the platform, never the
worker CLI or the orchestrator); this page owns *what happens to a commit's
identity* once it exists, across recovery, integration, and promotion.

## The stage-specific policy, stage by stage

| Stage | Operation | Why it's correct here | Where it's implemented |
|---|---|---|---|
| **Fresh/requeued agent recovery** | Rebase the delivery onto current `origin/<integration-branch>`, resolve, rerun gates, publish a *new* delivery under a new attempt fence. | No review claim exists yet on this attempt; a fresh fence legitimizes rewritten identity and gives review the simplest possible linear candidate. | Recovery instruction pattern; `POST /api/tasks/{jobId}/integration/rebase` (see below). |
| **Canonical integration** | Try `git merge --no-ff` (preserves every delivery SHA) -> on conflict, one mechanical three-way `ort`/`rerere` merge -> only then a disposable-worktree rebase, permitted **only** with an exact, persisted one-to-one old-to-new SHA map -> otherwise refuse and request a fresh agent round. | The delivery's SHAs are already named by the review subject and `commits[]`. Preservation is attempted first; rewriting is a bounded, evidence-checked fallback, not a routine path. | `GitService.MergeBranchIntoIntegration` / `MergeRefIntoIntegration` (`backend/Features/Git/GitService.cs`; the doc comment above the method spells out the exact ladder: behind-target -> direct merge -> three-way/rerere -> mapped rebase -> `AgentRoundRequired`). Full mechanical detail: [Task Integration and the Worktree/Merge Workflow](../task-integration-and-merge-workflow.md). |
| **Acceptance** | Does not perform integration itself. Requires Git-derived `integrated` status: every active attributed commit must be an ancestor of the configured integration branch. | Badges, pipeline receipts, and lane state cannot manufacture integration truth, only target-branch ancestry can. | `TaskTransitionService.ValidateIntegratedAcceptance`, `TaskIntegrationStatusService` (`backend/Features/Tasks/`). Domain contract: [`docs/system/domains/tasks.md`](../../system/domains/tasks.md) ("Accepted-card integration.status is a read-time projection of attributed commit membership"). |
| **Develop to main promotion** | Fast-forward publish the exact gated candidate SHA. A moved `develop` tip during the gate is informational only; the *tested* SHA is the one published. Never force-pushed. | The release receipt is about the object that was actually tested. A merge/squash/rebase between gate and publish would test one object and release another. | [`docs/operations/develop-main-promotion.md`](../../operations/develop-main-promotion.md) (safety contract: candidate fixed before the gate, remains a descendant through the push, atomic non-force push). |

## The SHA-attribution asymmetry (why merge and rebase are not interchangeable here)

A merge that succeeds keeps the original delivery commits as ancestors of the
integration branch — acceptance's ancestry check stays trivially true, and
every per-commit finding, revert, and evidence link still points at a real,
reachable object.

A rebase creates *new* commit objects. That is only safe when the system can
prove, atomically, that every original SHA has an exact one-to-one
replacement:

- `docs/system/domains/tasks.md` states the invariant directly: a
  platform-owned mechanical rebase retains each original `commits[]` entry,
  marks it with `supersededBySha`, and appends its replacement object with the
  same producer attribution. If that attribution write fails, the integration
  merge is rolled back before any push is released (`TaskCommit.cs`'s
  `supersededBySha` field is the persisted mechanism).
- The rebase fallback in `GitService.MergeRefIntoIntegration` is bounded by
  the same rule: cardinality must be preserved and the mapping must be
  one-to-one, or the outcome is `AgentRoundRequired` (hand back to a fresh
  agent round) rather than a silent rewrite.

A squash is rejected outright at canonical integration for the same reason:
it collapses many attributed SHAs into one unattributed object, which the
current `commits[]`-ancestry acceptance model cannot represent.

## Deterministic conflict recovery: what exists today (bounded, not general-purpose)

Two narrow, already-shipped mechanisms handle Git recovery without waiting on
a live orchestrator session:

- **Operator-triggered rebase recovery.** `POST
  /api/tasks/{jobId}/integration/rebase` (`TaskIntegrationRecoveryEndpoints.cs`)
  validates a recoverable `conflict-skipped` state, saves a focused Steer
  intent, appends a continuation note, supersedes the current delivery,
  promotes the card to Ready, and emits a timeline record. This is an
  operator action, not an automatic rule.
- **Bounded automatic agent round for attribution-ambiguous integration.**
  `IntegrationAgentRoundService` / `RemoteIntegrationContinuationPolicy`
  starts exactly one automatic pre-Human-Review Steer round when the
  merge-first integrator returns `AgentRoundRequired` (i.e., when the rebase
  fallback could not produce an exact replacement map). It is capped at
  `MaxAutomaticAgentRounds = 1` per operator review epoch; a repeat leaves the
  card for Human Review instead of looping. This addresses
  *attribution-ambiguous integration specifically*, it does not own every
  reviewed `conflict-skipped` card.

Both of these are real, tested, restart-safe mechanisms today. What they are
**not**: a general deterministic rule that claims and processes the *first*
bounce of any reviewed `conflict-skipped` card without a live orchestrator
session watching the board. That broader rule is a documented recommendation
in the source dossier, not yet built (see below).

## What this page leaves open

The source dossier's own status is `decision-pending`, and it explicitly
flags several pieces as recommendations awaiting operator sign-off and
further build-out, not current behavior. They are intentionally **not**
documented here as invariants:

- **A general deterministic backend rule for every first conflict bounce**
  (dossier scenario: a migration slice covering every reviewed
  `conflict-skipped` card, not just attribution-ambiguous integration). Today
  this coverage is narrower, see the previous section. No
  `IntegrationBounceObligation`-shaped type exists in code yet.
- **A strong-model "guardian" escalation tier** for repeated, complex, or
  contradictory conflict evidence. Precedent exists elsewhere (AGT-2654's
  visual guardian, referenced from
  [Global Orchestrator Watcher](../../operations/orchestrator-waechter/index.html)),
  but no guardian evidence-pack mechanism exists for this bounce-steering
  flow yet.
- **Batch Gate interaction.** Batch Gate itself (AGT-2648, AGT-W36) is a
  separate, still-open dossier; its effect on this attribution lens,
  particularly whether member assembly uses mapped-rebase replay or prebuilt
  no-FF merges, is explicitly unresolved pending a shadow-mode comparison.
  See [Batch Gate](batch-gate-mechanics.md).

If a future task implements any of the above, the durable result belongs in
this page (or a successor), not only in the dossier.

## See also

- [Task Integration and the Worktree/Merge Workflow](../task-integration-and-merge-workflow.md) —
  the full mechanical system-of-record: worktree/branch model, pipeline step
  wiring, commit/push timing, the complete integrate-before-review policy,
  conflict-recovery UI action, and incident history. This page assumes that
  one as the "how"; do not duplicate its detail here.
- [Commit / Push Doctrine](../../operations/git/commit-push-doctrine.md) — who
  is allowed to create a commit and when (the platform, never the worker CLI
  or the orchestrator). This page assumes that boundary and focuses on what
  happens to a commit's identity afterward.
- [Develop to main promotion](../../operations/develop-main-promotion.md) —
  the operator command and safety contract behind the exact candidate-SHA
  promotion row above.
- [`docs/system/domains/tasks.md`](../../system/domains/tasks.md) —
  `commits[]` attribution model, SHA/attempt supersession, and the
  `integration.status` projection.
- [`docs/system/domains/pipeline.md`](../../system/domains/pipeline.md) — how
  `post-merge-into-develop` and related git steps fit the pipeline step model.
- [Rebase, merge, and bounce steering (source dossier)](../../operations/rebase-merge-and-steering/index.html) —
  AGT-W37, the full decision analysis this page was extracted from, including
  the still-open automation scenarios above.

## Living knowledge log

Append new findings here, newest on top. Keep each entry short: date, what
was learned, pointer to code/commit/task.

- **2026-08-21.** Initial extraction from the AGT-W37 dossier (AGT-2662,
  `decision-pending`). Verified against code that the merge-first ->
  three-way/rerere -> mapped-rebase-fallback ladder, the `commits[]`
  supersession invariant, the exact candidate-SHA promotion train, and the
  two bounded recovery mechanisms are already live; the general deterministic
  first-bounce rule, the guardian escalation tier, and the Batch Gate
  interaction remain dossier recommendations, not yet implemented.
