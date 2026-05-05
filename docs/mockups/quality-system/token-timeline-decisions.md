# Workspace token timeline - layout decisions

The integrated mockup at `docs/mockups/quality-system/` calls for a
workspace-wide token aggregation surface ("Token Usage" project surface
in `README.md`). It does not pin down a layout for the cross-project
*timeline*. This note captures what landed in the first implementation
slice so future iterations can iterate against a written baseline.

## Surface

- Route: `#/workspace/tokens` (deep-link via the URL hash). Opens an
  overlay over the kanban board, dismissable by backdrop click. The
  same overlay is reachable through the "Timeline" link in the
  status-bar usage hover panel.
- Component: `<app-workspace-token-timeline>`.
- Backend: `GET /api/workspace/tokens/timeline?windowHours=N&bucketMinutes=M`,
  cells defined by `docs/schemas/token-timeline-bucket.schema.json`,
  service `WorkspaceTokensTimelineService` reading the same orchestrator
  log (`logs/.orchestrator/orchestrator.jsonl`) the per-card token bubble
  uses. No recompute from raw CLI output.

## Chart shape

A *stacked bar* per time bucket. The x axis is wall-clock time across
the selected window, the y axis is total tokens
(input + output + cache read + cache write). Each bar is split
vertically into one segment per project, coloured from a stable hash of
the project name so a project keeps the same colour across reloads and
across windows.

Why stacked bars over a stacked area chart:

- Bars make zero-activity buckets visually distinct (a gap, not a flat
  line crossing through the origin). Activity in this product is
  bursty; a flat area chart would lie about how active a quiet hour was.
- Hover targets are obvious - one rectangle = one (project, bucket)
  cell - and translate one-to-one to the popover's data model.
- An SVG bar chart fits in a few hundred lines without a dependency.
  Adding `chart.js` or `d3` would push the bundle by ~200kB for one
  view. The bundle-size constraint in the prompt rules that out.

## Window toggle

Four options: `1h`, `6h`, `24h`, `7d`. The component picks a default
bucket size for each (5 / 15 / 60 / 60 minutes); the backend supports
overriding bucket size via the query string but the UI does not surface
that knob today. If finer drill-down is requested later, the natural
place is a popover-driven inspector on a single bar, not a second
control in the header.

The selected window is persisted to `localStorage` so a reload returns
to the same view.

## Project legend

Pill-style chips below the chart, one per project, ordered by total
tokens descending. Click toggles the project off; total disappears
from the chart bands and the chip dims. Disabled projects are saved in
`localStorage`.

The per-project summary table sits below the legend with: total tokens,
total dollars (theoretical, with `(partial)` annotation when at least
one call used an unpriced model), peak bucket time + total, and
last-active time.

## Hover popover

When the user hovers any chart segment, a single popover (top-right of
the chart) shows the full cell record: project, bucket time range,
input / output / cache read / cache write tokens, total, calls,
theoretical dollars. Same shape as the per-card token bubble's popover
to keep one mental model.

## Non-goals (deliberate)

- No per-job drill-down from a chart segment in this slice. The token
  bubble on the kanban card already does that; the timeline is about
  *cross-project, time-resolved* spend.
- No CSV / PNG export. The page is a live read; if export becomes
  needed, the existing endpoint already returns JSON.
- No supervisor / orchestrator / supporting-job split inside a bar.
  The orchestrator log carries enough metadata to do it later, but the
  first slice keeps the segment-per-project model simple.
- No CLI subscription quota overlay. That data is in the status-bar
  usage hover panel; doubling it on the timeline would crowd the
  dollars story (subscription windows do not align with token counts).

## What this writes back to the integrated mockup

The mockup's `README.md` lists a "Token Usage" project surface plus a
workspace-wide aggregate. The timeline filed here is the *workspace*
half of that. The per-project Token Usage surface remains as in the
mockup (the existing per-project `<app-token-summary-block>` covers the
totals; a per-project timeline view can reuse the same backend service
later by passing one project to it).
