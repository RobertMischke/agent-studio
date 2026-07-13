---
styleGuideId: angular-components
title: Angular component guide
version: 1
summary: Rendering, identity, and token rules for Angular UI work.
promptSummary: Use standalone OnPush components with signal-backed state; track repeated rows by stable identity; read semantic color and spacing tokens in both themes; never add decorative left accent lines; render comparable numeric metrics with tabular-nums.
appliesTo: {"projects":["*"],"technologies":["angular"],"taskAreas":["frontend"]}
---

# Angular component guide

Use this page for Angular component and SCSS changes. The product-wide
[style-guide hard rules](../design/style-guide-hard-rules.md) are mandatory and
win over a local component convention. The detailed component inventory remains
in the [frontend style guide](../frontend/style-guide/README.md).

## Required component shape

| Concern | Rule | Evidence in this repository |
|---|---|---|
| Change detection | New components use `ChangeDetectionStrategy.OnPush`. Shared mutable state reaches the template through signals. | [Frontend performance playbook](../frontend/performance.md#state-and-rendering) and existing standalone components under `frontend/src/app/features/` |
| Repeated rows | Track by stable identity. Prefer `@for (...; track item.id)`; legacy `*ngFor` code supplies a `trackBy` function. Never track a mutable display label or list index when a durable id exists. | The board's recurring snapshots make untracked list replacement a measured rendering cost in the [performance playbook](../frontend/performance.md#state-and-rendering). |
| Component boundaries | Components are standalone, live one component per folder, and expose cross-feature symbols through the feature barrel. | [frontend/AGENTS.md](../../frontend/AGENTS.md) |

## Required visual shape

1. Read semantic `--studio-*` tokens for color, spacing, and state. Do not add a
   hex, RGB, or HSL literal to component SCSS. Use the canonical component or
   mixin before creating another local primitive.
2. Do not add a colored `border-left`, `border-inline-start`, or left inset
   shadow to encode card, panel, row, banner, or pill state. Use a full-surface
   tint, badge, or dot. The navigation selection indicator is the documented
   exception, not a reusable status pattern.
3. Verify both light and dark themes. Motion must collapse under
   `prefers-reduced-motion: reduce`.
4. Use `font-variant-numeric: tabular-nums` for counters, durations, token
   totals, percentages, and metrics that are compared vertically or change in
   place. Text that only happens to contain a number does not need it.

The first three items are anchored by the
[hard-rule page](../design/style-guide-hard-rules.md),
[token guide](../frontend/style-guide/tokens.md), and
[canonical component index](../frontend/style-guide/README.md). The tabular
number pattern is already used by project, task, and usage metrics throughout
the shell and prevents digits from making live rows jitter.

## Review evidence

- Add a focused component test for state and identity behavior.
- Run focused ESLint, Stylelint, and component-structure checks.
- For layout, styling, or interaction changes, exercise the real rendered
  surface in Playwright and review light and dark screenshots.
- Treat an existing hard-rule violation revealed by the change as a regression,
  not as a precedent to copy.
