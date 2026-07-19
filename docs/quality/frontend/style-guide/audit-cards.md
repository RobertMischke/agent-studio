# Audit — Cards

Inventory of card-like surfaces in `frontend/src/app/`. No Angular canonical exists today; every card is per-feature SCSS. The goal of this page is to make the recurring shape visible so we can decide whether to extract `<app-card>` (decision in [cards.md](./cards.md)).

## What a "card" means in this codebase

- A bounded surface with a 1px border (or shadow + no border), readable on both Mocha (dark) and the F19 light shell.
- Padding ranges from 8px (job-card title row) up to 16px (panel section card).
- Hover lift varies: cards in a list (job-card, run-card) lift, cards in a hub-section (sidebar-card, ev-card) stay flat.
- Status-bearing cards use a full-surface tint, badge, or dot. Coloured left accent stripes are prohibited by [the hard rules](../../design/style-guide-hard-rules.md).

## Inventory

| Class                     | File                                                                 | Background                   | Border                  | Padding   | Notes                                |
| ------------------------- | -------------------------------------------------------------------- | ---------------------------- | ----------------------- | --------- | ------------------------------------ |
| `.task-card`              | `frontend/src/app/features/board/.../task-card`                      | `--studio-bg-elevated`       | `--studio-border-strong`| 8-12px    | Kanban card (canonical for kanban)   |
| `.job-card`               | legacy `job-card.component.scss`                                     | `--studio-bg-elevated`       | `--studio-border-strong`| 8-12px    | Pre-rename alias, same shape         |
| `.run-card`               | `run-timeline.component.scss`                                        | `--studio-bg-elevated`       | `--studio-border`       | 10-14px   | Per-run card on the protocol pane    |
| `.task-status-card`       | `components/task-status-card/`                                       | semantic tint                | `--studio-border`       | 12px      | Reusable status card (shared)        |
| `.sidebar-card`           | various features                                                     | `--studio-bg-sidebar`        | `--studio-border`       | 10px      | Hub sidebar card                     |
| `.context-card`           | various                                                              | `--studio-bg-elevated`       | `--studio-border`       | varies    | Context-bar inline card              |
| `.drift-card`             | drift section / overview                                             | semantic tint                | `--studio-border`       | 12px      | Drift card with status treatment     |
| `.steer-card`             | orchestrator side sheet                                              | semantic tint                | `--studio-border`       | 12px      | "Steer" message card                 |
| `.ev-card`                | evidence panel                                                       | `--studio-bg-elevated`       | `--studio-border`       | 8px       | Inline evidence row card             |
| `.cli-card`               | CLI sessions panel                                                   | `--studio-bg-elevated`       | `--studio-border-strong`| 10-12px   | CLI status card                      |
| `.file-card`              | files-pane                                                           | `--studio-bg-elevated`       | `--studio-border`       | 8px       | File-listing card                    |
| `.source-card`            | analysis-report-drilldown                                            | `--studio-bg-elevated`       | `--studio-border`       | 10px      | Source-of-evidence card              |
| `.cli-usage-modal__headroom-card` | cli-usage-detail-modal                                       | `--studio-bg-elevated`       | accent border           | 12px      | Per-modal one-off                    |
| `.pdov__report-card`      | project-drift-overview-section                                       | `--studio-bg-elevated`       | `--studio-border`       | 12px      | Per-feature                          |
| `.ux-panel__ref-card`     | uxui-panel                                                           | `--studio-bg-elevated`       | `--studio-border`       | 10px      | Per-feature                          |

## Findings

Twelve+ card classes are built on `--studio-bg-elevated`, a border, and 8-12px padding. The recipe is identical; the deltas are the **status treatment** (none / full-surface tint / badge / dot) and the **hover behaviour** (lift / flat).

Three variants emerge:

1. **`<app-card variant="default">`** — `--studio-bg-elevated`, 1px border, no accent, no hover lift. Covers 7-8 of the 12 cases.
2. **`<app-card variant="elevated">`** — same surface but with `--elevation-card` shadow and a hover-lift. Covers job-card / run-card / task-card.
3. **`<app-card variant="tinted">`** - semantic full-surface tint with a per-prop status token. Covers task-status-card, drift-card, steer-card.

These overlap, but the variants are stable.

## Migration consideration

Extracting `<app-card>` is **lower priority than `<app-button>`** because card surfaces tend to be feature-coupled (the `.task-card` carries kanban-specific drag handles; the `.run-card` carries per-run state inputs). Promoting to a shared component without absorbing the feature-specific slots produces churn for no convergence.

The realistic next step is the **canonical-token / canonical-shape pattern**: every card SCSS reads `--studio-bg-elevated` + the new spacing tokens + `--elevation-card` (when elevated) so the *shape* converges even when the component does not. See [cards.md](./cards.md) for the proposal.
