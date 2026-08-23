# Rebase, merge, and integration attribution invariants

Status: mostly current-state (implemented, with code anchors), one section
still decision-pending. Extracted from the
[rebase-merge-and-steering dossier](../operations/rebase-merge-and-steering/index.html)
(`AGT-W37`, sourceTaskKey `AGT-2662`) so the settled invariants are
discoverable without the incident narrative and open-options debate that
dossier also carries. The dossier remains the system-of-record for the still
undecided "platform rule" automation scenario (below).

## Attribution invariants

Four objects govern correctness, independent of branch topology:

- **acceptance evidence** — active, non-superseded `task.json commits[]`, or
  the current immutable review result if attribution is empty;
- **review subject** — one fenced result ref plus one expected head SHA that
  must still resolve when fetched for integration;
- **integration truth** — every active attributed commit must be an ancestor
  of the integration branch; pipeline receipts and badges cannot substitute
  for this;
- **promotion truth** — the exact tested candidate SHA is what publishes to
  `main`; any merge, squash, or rebase performed after the gate creates an
  untested subject.

## Stage-specific Git policy

The platform intentionally applies different Git operations at different
integration stages, not one policy applied uniformly:

| Stage | Policy |
|---|---|
| Fresh/requeued agent delivery | Rebase onto current `origin/<integration-branch>`, resolve conservatively, re-fence with new attribution. Valid because a new attempt fence records the replacement. |
| Canonical integration | Try `merge --no-ff` first, then a mechanical three-way `ort`/`rerere` merge, and only as a last resort a disposable-worktree rebase gated on an exact one-to-one commit-count mapping. |
| Acceptance | Never performs integration itself — `TaskTransitionService.ValidateIntegratedAcceptance` requires Git-derived `integrated` status from `TaskIntegrationStatusService`; a failed attempt projects as `conflict-skipped`. |
| `develop` → `main` promotion | Fast-forward only the exact pinned, gated candidate SHA. |

This order — preserve first, map only as fallback, refuse on ambiguity — was
set by the 11 August (`AGT-2632`) correction, reversing an earlier
rebase-first approach that made attribution fragile.

## SHA identity mechanics

Merge and rebase are not symmetric for attribution. A no-fast-forward merge
keeps original delivery SHAs as ancestors of the integration branch, so
acceptance attribution is direct. A rebase (or rebase fallback) creates new
SHAs and is only valid if the server persists an exact, one-to-one
old-to-new replacement map. If that mapping cannot be proven, integration
must fail closed — rolling back before publication and returning
`AgentRoundRequired` rather than reporting success. Prove preservation, then
optionally map, never silently rewrite.

## Integration-stage decision rules

- No-fast-forward merge is the first-attempt default because it preserves
  attributed SHAs.
- Squash merge is rejected outright under the current evidence model: the
  original SHAs would no longer be ancestors, breaking per-commit
  attribution. Adopting it would require a new evidence contract.
- Rebase-then-fast-forward is a bounded, guarded fallback only, valid solely
  with unchanged commit cardinality, a persisted one-to-one map, and rollback
  on recording failure.
- Once a `develop` candidate is green, promotion to `main` may only
  fast-forward that exact SHA — never re-merge, squash, or rebase between the
  gate and the release.

## Bounce recovery mechanism (already implemented)

Several delivered platform seams compose into the current recovery path:

- `POST /api/tasks/{jobId}/integration/rebase` validates a recoverable
  `conflict-skipped` state, saves a focused steer intent, appends a
  continuation note, supersedes the current delivery, promotes the card to
  Ready, and emits a timeline record (operator-triggered today).
- `IntegrationAgentRoundService` already runs one automatic pre-Human-Review
  steer round, bounded per operator review epoch, specifically for
  attribution-ambiguous integration (`AgentRoundRequired`).
- Precedent patterns deployed elsewhere use the same shape: one automatic
  retry then a typed escalation (envelope policy), and a bounded
  classifier with one evidence-driven retry then Human Review with durable
  evidence (the visual QA guardian, `AGT-2654`).

### Still open: a deterministic backend bounce handler

The dossier's Scenario 2 — generalizing the bounce-and-retry pattern above
into a deterministic, always-on backend rule rather than a per-feature
pattern — is the primary proposal under decision, not a shipped mechanism.
Its source page visually marks it with the same "done"-style badge used for
genuinely implemented rows nearby; treat that badge as a recommendation
marker only. The dossier's own migration slices (shadow-mode, then a
deterministic rail) have not started.

## Model/thinking routing for recovery rounds

Per the canonical
[model routing policy](../operations/model-routing-policy/index.html): a
stale-base or environmental reissue must not be promoted to a stronger
model; thinking may be raised to `high` for a routine mechanical bounce only
when the task carries no hard floor; explicit operator pins always win;
quota never lowers a hard floor. Action receipts record the old route, the
selected route, the reason, the policy version, and whether an explicit pin
applied.

## Living knowledge log

- 2026-08-18 (AGT-2671): extracted from the rebase-merge-and-steering
  dossier as part of an operator-mandated dossier curation pass. Most
  content here is already-implemented current state; the deterministic
  backend bounce handler (Scenario 2) is still decision-pending.
