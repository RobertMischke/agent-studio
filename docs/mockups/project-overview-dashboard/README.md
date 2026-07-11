# Project Overview operator dashboard

Status: Interactive design reference for AGT-2105, 2026-07-11.

Open the [interactive mockup](ui.html) directly in a browser. It is a
self-contained click-dummy with light and dark themes and no backend dependency.

## Purpose

Project Overview is the operator's project-status page. It answers what moved,
what needs attention, what is running, what is waiting to deploy, and what the
operator should inspect next. Machine setup belongs in Project Settings and is
deliberately absent here.

The mockup covers five dashboard blocks:

1. Throughput and token usage for the last 24 hours and 7 days.
2. A Visual Evidence review queue.
3. The project's small set of important URLs, including status and the existing
   start-in-place interaction.
4. A deliberately large deployment block with the develop-to-deployed delta
   and the latest deploy.
5. A Wiki and planning entry point for new concepts and active planning tasks.

Every block is compact at rest and opens a detail sheet. The dashboard remains
full width so the deployment decision can carry appropriate visual weight.

## Fixture contract

The click-dummy renders one in-file fixture whose fields follow current product
shapes instead of decorative placeholder numbers:

- `metrics.completed` represents archive-inclusive transitions into Completed.
  The 24-hour value is a subset of the 7-day value.
- `metrics.tokens` represents project token totals for the same windows. The
  24-hour value matches the existing project summary shape; the 7-day value is
  the corresponding seven-day aggregation.
- `urls[]` follows the registry Project URL record: `id`, `label`, `url`,
  `sortOrder`, and optional `startRule`. Status comes from the shared probe
  vocabulary: `running`, `offline`, or `unknown`. Starting the offline URL
  simulates the existing start endpoint and moves it through `building` to
  `running`.
- `evidence[]` follows the task screenshot projection: task identity, filename,
  result-relative path, caption, test status, source, timestamp, and local
  review state. The review state is mockup-only until the Evidence Queue
  follow-up lands.
- `deployment` is a compact projection of the shared DEP-1 read model: last run,
  deployed revision, develop delta, included commits, and elapsed time. It does
  not parse logs or git as a second source and has no deploy action.
- `wiki` follows the Wiki Pulse vocabulary for recent concepts and adds a
  compact projection of active planning tasks.

## Interaction map

- Use the theme button in the top-right corner to switch between light and dark.
- Select any dashboard action to open its detail sheet. Close it with the back
  button, the backdrop, or Escape.
- Select `Start` on the offline Storybook URL to see the shared URL status
  sequence. No new start mechanism is implied.
- Open Visual Evidence, move between screenshots, and mark an item reviewed.
  The visible queue count updates immediately.
- Open the deployment detail to inspect the exact commits represented by the
  summary delta.
- Open a Wiki concept or planning task from the detail sheet to see the intended
  handoff target.

## Production boundary

The first production slice is read-only for metrics, URL status, last deploy,
and Wiki links. URL `Start` reuses the already shipped Project URL action.

The following remain named follow-ups:

- Visual Evidence Queue persistence and review semantics, after the proposal
  system direction is accepted.
- The full deployment workflow, target configuration, and run actions from
  [Deployment as a first-class citizen](../../concepts/deployment-first-class.md).

The dashboard must not absorb the detailed homes. Token Usage, Project URLs,
Deployment, Wiki, and task detail remain the inspectable sources behind the
summary.

## Visual rules

- No colored left accent bars. Status uses a surface tint, badge, or dot.
- The top-level page has no artificial width cap.
- Aggregate values reconcile to the visible rows behind them.
- Settled history is quiet. Acute treatment is reserved for current states.
- Both themes use the same hierarchy and token vocabulary.
- Numeric values use tabular figures.
- Motion collapses under `prefers-reduced-motion: reduce`.

See the [style-guide hard rules](../../design/style-guide-hard-rules.md), the
[Project URLs mockup contract](../project-urls/README.md), and the
[Wiki Pulse concept](../../concepts/wiki-pulse-dashboard.md).
