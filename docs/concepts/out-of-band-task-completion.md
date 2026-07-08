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
