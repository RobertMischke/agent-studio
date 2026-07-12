<<<<<<< HEAD
# Out-of-band task completion — making external work first-class

**Status:** concept 2026-07-08, triggered by the AGT-1917 post-mortem.
Related: [`remote-execution-product-integration.md`](remote-execution-product-integration.md)
(work arrives from everywhere), [`multichat-orchestrator.md`](multichat-orchestrator.md).

## 1. What happened with AGT-1917 (the post-mortem)

1. The runner's attempt died mid-run (2026-07-07 session limit). The
   escalation path parked the card in 5e with "no agent-written summary" —
   **although the dying run had already written partial deliverables into
   `results/`**. Escalation never surfaces results.
2. The operator ordered out-of-band completion ("macht das mal außerhalb").
   The work was done and committed to `docs/concepts/` — correctly — but the
   card was only *moved* by lane. Nobody owned the card's **story**:
   `status.md` still said "escalated / no summary", `lifecycle.json` was
   stuck in `post-processing-running` (spamming scanner warnings every 30 s),
   and the run history ended in a corpse.
3. Result: a reviewed-looking lane with an abandoned-looking card. "Lost."

**The gap:** the product treats *runner runs* as the only source of task
evidence. Work increasingly happens elsewhere — operator chats, external
agents, soon remote hosts (Mode C). There is no ingest path for that.

## 2. The rule (effective immediately — convention)

Whoever completes a task outside the runner MUST reconcile the card, not
just move it:

1. `results/deliverables.md` — what was delivered, where (repo paths +
   commits), by whom/which channel.
2. `status.md` — replace the stale text with a result summary; state
   explicitly "executed out-of-band" + date.
3. `lifecycle.json` — terminalize the phase (`awaiting-review` for
   5-human-review); mark running checks `skipped` with a note.
4. Provenance: if a dead run left drafts in `results/`, label which files
   are canonical and which are the dead run's drafts.
5. Lane move LAST, then commit the workspace.

This list is the checklist version of what fixing AGT-1917 required.

## 3. The product fix (to build — small)

**`POST /api/tasks/{id}/external-completion`** — one endpoint that does §2
atomically: accepts `{ summary, deliverables: [{path|url, note}], source,
targetState? }`, writes `status.md` + `results/deliverables.md`,
terminalizes `lifecycle.json`, appends an **`external` entry to the run
history/timeline** (so the card's history shows *"completed externally by
<source>"* instead of ending in a corpse), moves the lane, commits evidence.

**UI:** cards completed this way get a small **"extern erledigt"-badge**
next to the CLI badge; the timeline renders the external entry. No other
UI change.

**Escalation improvement (same family):** when a run dies *with* files in
`results/`, the escalation text must say so ("partial results present")
instead of "no agent-written summary" — that alone would have made
AGT-1917 look half as lost.

## 4. Why this matters beyond one card

Mode C makes "work arrives from outside the local runner" the *normal
case* (remote arms, operator chats, multichat contexts). The task server
can only be the single source of truth if externally produced results have
a first-class ingest path. This endpoint is the minimal version of that —
same spirit as the planned artifact upload (RM-4/G6), which it should
share plumbing with.
=======
# Out-of-band Task Completion (concept)

Status: partially implemented. §3 (the reconciliation endpoint), the "extern
erledigt" card badge, the `external_completion` timeline entry, and the
partial-results escalation hint (§3, last paragraph) are shipped. Written to
retire the AGT-1917 corpse — a task whose work was done and committed outside
the runner but whose card stayed stuck in a running phase with an empty summary.

## 1. The problem — work happens, the card stays a corpse

Not every task is finished by a local runner run. Sometimes the work is done
out-of-band:

- an **operator** does it by hand (or in the orchestrator chat) and commits it,
- an **external agent** or a **remote host** produces the result, or
- a run dies mid-flight but the deliverables are already on disk.

When that happens today the card is left looking abandoned even though the work
is real. AGT-1917 was the motivating case: the change was implemented and
committed, but

- `status.md` still said *"escalated / no summary"*,
- `lifecycle.json` was stuck in `post-processing-running` (spamming the scanner
  warning that a card was mid-phase with no live run), and
- the run history ended in a corpse — no event said *"this was finished, just
  not here"*.

The lane could be dragged by hand, but the card's **story** stayed wrong. The
board could not explain what happened to it.

## 2. Model — an out-of-band completion is a first-class ingest, not a hack

The fix treats "work that arrived from outside the local runner" as a first-class
ingest path with its own provenance, not a manual folder edit:

- **Actor `external`.** Timeline events, the decision ledger, and the card badge
  all attribute the completion to `external` (or the concrete operator/agent
  named in the request), distinct from `agent` / `orchestrator` / `system`, so
  the Timeline filter chips can tell externally produced results from runner
  activity.
- **Canonical narrative on disk.** `results/deliverables.md` is the human record
  of *what was delivered and where*; the `external_completion` timeline row and
  the card badge are small renderable summaries that point at it.
- **The lane move is a consequence, not the act.** Moving the card is the last
  step, after the evidence is reconciled — never a substitute for it.

## 3. The reconciliation endpoint — `POST /api/tasks/{id}/external-completion`

One atomic call reconciles a task finished outside the runner. It is a thin
validation + status-to-HTTP shell over `ExternalCompletionService`, matching the
neighbor task endpoints.

**Request** (`ExternalCompletionRequest`):

| Field | Required | Meaning |
|-------|----------|---------|
| `summary` | **yes** | Result summary that replaces the stale `status.md` text. |
| `deliverables[]` | no | What was delivered and where — each has `path` (repo-relative, optional `@sha`), `url`, and/or `note`. |
| `source` | no | Who / which channel did the work (operator name, agent id, `"chat"`, …). Defaults to `external`. |
| `targetState` | no | Destination lane. Defaults to `5-human-review` (the card still gets a quick operator confirmation). Must be a valid lane. |
| `gateItems[]` | no | Open operator checklist items written to `orchestrator-follow-up.md`, for example a remote `worktree-blocked` salvage failure. |

Attribution is split deliberately: the **caller** (`X-Client-Id`) is the operator
who *relayed* the result and drives the `lane_changed` ledger row; the completion
**source** (who actually *did* the work) is the `source` field on the body.

**What it does, in order** (`ExternalCompletionService.CompleteAsync`). Every
write targets the task's *current* folder first; the lane move is last so all the
evidence lands together before the folder is renamed:

1. **`results/deliverables.md`** — the canonical narrative: date, source, the
   summary, a bullet list of delivered artifacts (paths/URLs + notes), and a
   provenance block. Files under `results/` that predate this reconciliation are
   flagged as the dead run's drafts.
2. **`status.md`** — deliberately *overwritten* (unlike the escalation stub,
   which never clobbers a real summary): a `- Result: Completed out-of-band
   (<source>)` line, the summary, the date, and a pointer to `deliverables.md`.
   Retiring the *"escalated / no summary"* corpse is the whole point.
3. **Optional gate checklist** - non-empty `gateItems[]` rows are appended as
   open items in `orchestrator-follow-up.md`, which makes remote recovery work
   visible in the card's escalation summary.
4. **`task.json` provenance** - `ExternalCompletionInfo { source, summary,
   completedAt }` is recorded on the card, mirrored to the frontend
   `TaskInfo.externalCompletion` and driving the badge (§4).
5. **`lifecycle.json` terminalized** - the phase is set to `awaiting-review` and
   every still-`running`/`pending` intake or post-processing check is flipped to
   `skipped` with a *"Superseded by out-of-band completion"* note, so the card
   stops spamming the `post-processing-running` scanner warning. The mirrored
   `task.json` phase is cleared to match.
6. **`external_completion` timeline row** - actor `external`, summary *"Completed
   externally by <source>"*, `payloadRef` → `results/deliverables.md`, details
   carry `source` and `targetState`. Appended *before* the move so it lands in
   the same folder as the rest of the evidence; the `lane_changed` row is emitted
   by the state machine.
7. **Lane move** - `MoveAsync` to `targetState` (default `5-human-review`). A move
   out of `3-progress` runs `EnterPostProcessingPhase`, which would otherwise
   reset `lifecycle.json` back to `post-processing-running` — the exact stuck
   state this endpoint exists to retire — so the terminal lifecycle is
   *re-asserted* into the moved folder afterwards (idempotent for every other
   source lane).
8. **Evidence commit** - `WorkspaceArtifactCommitService.TryCommitExternalCompletion`
   commits the workspace snapshot (before + after folder) so the reconciliation
   is durable, not just local. A failed commit is logged, not fatal.

**Status → HTTP mapping**: `Success` → `200` with `{ jobId, targetState, source,
evidenceCommitSha }`; `NotFound` → `404`; `InvalidRequest` (missing summary /
unknown `targetState`) → `400`; `MoveConflict` (target folder exists / directory
locked) → `409`; everything else → `500`. The canonical writes (status /
deliverables / `task.json`) are treated as fatal on hard failure so the caller is
never told a half-reconciled card is done; the ancillary writes (lifecycle,
timeline, commit) are best-effort-logged.

**Partial results present.** The same "the work is more real than the card looks"
insight applies to the *escalation* path this endpoint complements. When the
orchestrator runtime routes a card to `5e-escalated` without an automated quality
review, its `status.md` stub used to say only *"no agent-written summary"* — which
made AGT-1917 look twice as lost as it was. When a dying run has left files in
`results/`, the stub instead states that **partial results are present in
`results/`, review them before deciding**, so a reviewer inspects the
deliverables (and can then reconcile them through this endpoint) rather than
treating the card as empty. The probe is best-effort and fails closed: a wrong
*"partial results present"* line is worse than a missing one.

## 4. Frontend — badge + timeline rendering

- **"extern erledigt" badge.** `buildExternalDoneBadge` renders a badge next to
  the CLI/model chip on any card whose `externalCompletion` is set, so a card
  whose work happened outside the runner reads as intentionally done, not
  abandoned. The tooltip names the source and date and points at
  `results/deliverables.md`.
- **Timeline row.** The `external_completion` event renders in the task timeline
  under the `external` actor glyph/filter chip, with the summary line and the
  `results/deliverables.md` payload expandable inline — so the card's history
  shows the external hand-off instead of ending in a corpse.
>>>>>>> origin/develop
