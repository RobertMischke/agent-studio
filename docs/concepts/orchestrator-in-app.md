# Orchestrator in-app — the operator moves inside

**Status:** concept v1, 2026-07-10 — operator-directed ("in der nächsten
Version möchte ich, dass der Chat der Orchestrator ist, der das Board im
Blick hat — die ganze Anwendung — und sie am Laufen hält, auch wenn er
innerhalb der Anwendung lebt"). Related:
[`multichat-orchestrator.md`](multichat-orchestrator.md) (context model),
[`run-liveness-and-slot-semantics.md`](run-liveness-and-slot-semantics.md),
[`post-processing-immediacy-and-parallelism.md`](post-processing-immediacy-and-parallelism.md),
[`publishing-workflows.md`](publishing-workflows.md).

## 1. Today vs. target

**Today** an external operator session (Claude beside the app) keeps the
system healthy: reconciles gate false-positives, bounces zombies, parks and
promotes dependency chains, publishes packages, watches quotas. The app is
the patient; the operator lives outside it.

**Target (v-next):** the in-app orchestrator chat *is* that operator. It
sees everything the human sees (board, runs, quotas, health) and holds the
operational levers to keep the application running — from within.

## 2. Three pillars

### 2.1 Sight — the orchestrator sees the whole application
Full read access as chat context: lanes and transitions, run/lifecycle
state, quota snapshots, publish targets, health endpoints, decision
journal. Most of this exists as APIs today; the work is wiring it into the
orchestrator's context (multichat context keys already point the chat at
board/task/project scopes).

### 2.2 Hands — journaled operational tools
The new part: the orchestrator chat gets **tools** for exactly the actions
the external operator performs today, each one written to the decision
journal (exists) and visible in the feed:

- reconcile a card (lane move + status note), requeue/bounce, park/promote
- restart post-processing for a finished-but-orphaned run
- trigger a publish (tag path, PUB-2) or a website deploy
- adjust parallelism, pause/resume a project's auto mode
- switch model/level per the quota-fallback policy (AGT-2040)

Structural fixes (1944 outcome taxonomy, 2000 run-liveness, 2021 aspect
retry, 2028 spawner, 2029 waits-on) shrink how often these hands are
needed; the orchestrator handles the remainder — and unknown-unknowns.

### 2.3 Anchor — self-preservation despite living inside
**Deprioritized by the operator (2026-07-10):** the dead-host problem is
deliberately ignored for now — the operator mostly works in other
applications anyway and restarts a dead host himself; the existing
watchdog/start scripts stay as they are. The split below remains the
target picture for later, not a near-term slice.

The honest paradox: a component inside the app cannot restart its own dead
host. Split responsibilities:

- **In-app brain** handles everything *except* host death: lane hygiene,
  chains, publishes, quota policy.
- **Minimal outside anchor** handles host death only: the existing
  watchdog/service path (`watchdog-stable.sh`, detached start scripts,
  later systemd on Linux hosts) restarts an unhealthy backend. The anchor
  is dumb on purpose — all judgment lives in the brain.
- On boot, the brain runs the adoption scan (AGT-2000) and resumes its
  standing orders.

## 3. Standing orders become policy

The night-shift playbook (triage categories: infrastructure cut vs. quota
vs. gate false-positive vs. genuine dependency; reconciliation notes;
publish duties; "pause until quota reset when both CLIs are dry") is
captured as the orchestrator's **standing instructions** — versioned in the
repo, editable by the operator, loaded as the orchestrator chat's system
context. The human stops being the runbook.

## 4. Slices

| Slice | Scope | Gate |
|---|---|---|
| ORCH-1 | sight: complete read context (lanes, runs, quota, health, journal) in the orchestrator chat | multichat MC-2/3 |
| ORCH-2 | hands: journaled tools (reconcile, requeue, park/promote, restart post-processing, parallelism) | ORCH-1; 2028/2029 deployed |
| ORCH-3 | policy + anchor: standing-orders document, quota policy wiring (2040), watchdog contract | ORCH-2 |
