# Review Pipeline Health — folder index

This folder is the **home for the review-pipeline-health problem class**: every
design decision, incident experience and open issue of this class lands here,
coupled to the graphical concept page one level up
([review-pipeline-health.html](../review-pipeline-health.html) — behavior, target
and current state, with diagrams).

| Document | What it holds |
|---|---|
| [decision-history.md](decision-history.md) | Every design decision with date, reasoning and card reference — newest first. |
| [incident-2026-07-22-gate-churn.md](incident-2026-07-22-gate-churn.md) | The night incident of 22./23.07.2026: timeline, diagnosis, remediation, lessons. |
| [issues.md](issues.md) | Open issues of this class, each linked to its board card. New problems of this kind start here. |

## Conventions

- **New incident?** Add `incident-YYYY-MM-DD-<slug>.md` here, link it from
  `issues.md` and from the concept page's §3 failure classes if it adds a new class.
- **New decision?** Prepend it to `decision-history.md` with date + card key.
- The concept page is the canonical entry point; the documentation pipeline step
  should resolve review-pipeline / gate / lease / admission topics to it
  (step productization tracked separately).

## Consolidated detail views

These pre-existing documents remain valid and are subordinated to the concept page:
[run-liveness-and-slot-semantics.md](../run-liveness-and-slot-semantics.md) ·
[token-budget-load-management.md](../token-budget-load-management.md) ·
[auto-review-evidence-gate-analysis.html](../auto-review-evidence-gate-analysis.html) ·
[completion-review-and-remote-runner-stability.html](../completion-review-and-remote-runner-stability.html) ·
[task-processing-pipeline/](../task-processing-pipeline/README.md) ·
[zielarchitektur-diagramm.html](../zielarchitektur-diagramm.html)
