# Frontend test IDs — lane / state mapping

Playwright + visual-probe automation needs to find a board lane by its
job state id (`2-ready`, `3-progress`, `4-auto-review`, `5-human-review`,
etc.), but the board groups several lanes together under a higher-level
"lane group". This document is the single source of truth for that
mapping.

## Lane groups (top level)

The board renders three lane groups, each containing one or more lanes.
The group's DOM element carries:

- `data-testid="lane-group-{group-id}"`
- `data-states="{comma-separated list of contained state ids}"`

| Group id  | Visible label   | States it contains                                                                  |
|-----------|-----------------|-------------------------------------------------------------------------------------|
| `backlog` | Backlog         | `2-ready`, `2-ready-intake`?, `1b-needs-human-review`?, `1a-orchestrator-prep`, `1-preparation`, `0-backlog` |
| `active`  | Active          | `3-progress`, `3a-failed-pickup`?, `4-auto-review`                                  |
| `decide`  | Done & Decide   | `5-human-review`, `6-completed`, `7-archive`                                        |

States marked `?` only render when the lane has at least one job.

## Individual lanes

Each lane inside a group is an `<app-job-column>` instance. Its outer
element carries:

- `data-testid="lane-{state-id}"`     (e.g. `lane-2-ready`)
- `data-state="{state-id}"`
- a rail element at `[data-testid="lane-rail-{state-id}"]` with the
  lane head + counters.

## Job cards

Cards inside a lane carry:

- `data-testid="job-card-{job-id}"`
- `data-job-id`, `data-job-key`, `data-state`

## Recipes

Find the lane containing a given state:

```ts
page.locator('[data-states*="5-human-review"]') // → lane-group-decide
page.locator('[data-testid="lane-5-human-review"]') // → the lane itself
```

Find a specific card by title across all lanes:

```ts
page.locator('[data-testid^="job-card-"]')
  .filter({ hasText: 'Playwright probe' })
  .first()
```

Wait for a job to reach a target state:

```ts
await page.locator(
  `[data-testid="lane-${targetState}"] [data-job-id="${jobId}"]`
).waitFor({ timeout: 5 * 60_000 });
```

## Why this layer exists

The lane-group ids (`backlog | active | decide`) are stable user-facing
labels chosen by ADR-0026. The state ids (`0-backlog` … `7-archive`) are
the runtime tags on disk and in the API. The two are intentionally
decoupled: lane groups can move between layouts without renaming the
underlying state machine. Automation needs both views — the `data-states`
attribute exposes the bridge in the DOM so probes don't have to hardcode
the mapping.
