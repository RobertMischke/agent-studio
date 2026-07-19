# Orchestrator in-app: the operator moves inside

Status: concept v1, 2026-07-10. ORCH-1 sight is implemented by the
context digest described below. ORCH-2 and ORCH-3 remain future work.

The target is an in-app orchestrator chat that can keep the application
running while it lives inside that application. The app is no longer only the
patient observed by an external operator. The chat receives the same compact
operational picture the operator uses to understand the board.

## Three pillars

### Sight

The orchestrator receives a current, read-only application digest with:

- lane counts and the latest lane transitions;
- active runs and their lifecycle phase;
- cached CLI quota windows;
- PUB-1 publish-target status;
- backend and filesystem-watcher health;
- recent decision-journal verdicts.

The digest follows the multichat context key. `global` includes all registered
projects, `project:<project>` includes only that project, and
`task:<project>/<task>` adds a focused task row to the same project-scoped
facts. Project and task contexts must never leak facts from other projects.

The representation is bounded and model-oriented rather than a raw API dump.
Noisy event sections have fixed row caps, long text is truncated, raw quota
samples and full decision prompts or responses are excluded, and capped event
headings state the applied limit. Lane and publish summaries retain one compact
row per in-scope project so global context does not silently omit a project.
Normal turns use cheap live facts plus cached quota data.
An explicit refresh may run the expensive quota probes before rebuilding the
same digest.

The digest is one backend service shared by the visible side-sheet chat and the
context-session turn API. This prevents two orchestrator entry points from
developing different views of application state.

### Hands

ORCH-2 will add journaled operational tools for reconciliation, requeue,
park/promote, post-processing restart, publish, and parallelism changes. It is
not part of ORCH-1. ORCH-1 does not grant mutation authority.

### Anchor and standing orders

ORCH-3 will capture standing operational policy and the minimal outside anchor
needed when the host itself is unavailable. Host-death recovery remains outside
the in-app chat for now.

## Slice boundaries

| Slice | Scope | Gate |
|---|---|---|
| ORCH-1 | Complete, scoped read context and on-demand refresh | Multichat context keys |
| ORCH-2 | Journaled intervention tools | ORCH-1 |
| ORCH-3 | Standing-orders policy and outside anchor | ORCH-2 |
