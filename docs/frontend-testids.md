# Frontend test IDs — lane / state mapping

Playwright + visual-probe automation needs to find a board lane by its
job state id (`2-ready`, `3-progress`, `4-auto-review`, `5-human-review`,
etc.). The board renders LANE GROUPS (`backlog | active | decide`) each
containing one or more lanes, so the obvious `lane-group-{name}` testid
is one level above the state. This document is the single source of
truth for that mapping.

## Quick reference

### The standard lookup — use this

```ts
// Find the lane GROUP containing a given state:
page.locator('[data-states*="5-human-review"]')

// Find the SPECIFIC lane:
page.locator('[data-testid="lane-5-human-review"]')

// Find a card by title across all lanes:
page.locator('[data-testid^="job-card-"]').filter({ hasText: 'My title' }).first()

// Wait for a job to reach a target state (API-based — see G1 below):
const url = `/api/jobs/${id}?watchPath=${encodeURIComponent(wp)}`;
const state = await page.evaluate(
  async (u) => (await (await fetch(u)).json()).state,
  url
);
```

Use the `[data-states*="..."]` selector even if you know the group id —
it survives ADR-0026 lane-group reshuffles and makes the test self-
documenting ("the lane group containing state 5-human-review").

## Lane groups (top level)

Each `<section class="lane-group">` carries:

- `data-testid="lane-group-{group-id}"`           ← human-readable label
- `data-states="{comma-separated state ids}"`     ← ← prefer this lookup

| Group id  | Visible label   | States it contains                                                                  |
|-----------|-----------------|-------------------------------------------------------------------------------------|
| `backlog` | Backlog         | `2-ready`, `2-ready-intake`?, `1b-needs-human-review`?, `1a-orchestrator-prep`, `1-preparation`, `0-backlog` |
| `active`  | Active          | `3-progress`, `3a-failed-pickup`?, `4-auto-review`                                  |
| `decide`  | Done & Decide   | `5-human-review`, `6-completed`, `7-archive`                                        |

States marked `?` render only when at least one job lives there.

## Individual lanes

Each lane is an `<app-job-column>` instance. Its outer element carries:

- `data-testid="lane-{state-id}"`            (e.g. `lane-2-ready`)
- `data-state="{state-id}"`
- a rail `[data-testid="lane-rail-{state-id}"]` for head + counters.

## Job cards

- `data-testid="job-card-{job-id}"`
- `data-job-id`, `data-job-key`, `data-state`

## Patterns to AVOID

| ❌ Anti-pattern | ✅ Why it's bad | Use instead |
|---|---|---|
| `lane-group-decide` to find a human-review card | Couples your test to the lane GROUPING (subject to ADR-0026 reshuffles). | `[data-states*="5-human-review"]` |
| Wait for a state transition by polling DOM | UI poll rate (≥ 1 s) can be slower than backend state machine; fast lifecycles slip past every tick. See G1 below. | Poll `/api/jobs/{id}` directly, react to state change |
| `getByRole({name:'Create'})` on the create dialog | i18n-fragile + breaks if label changes. | `getByTestId('create-submit')` |

## G1 — API state vs DOM scan

A probe writing a 60-line file can finish in 15 seconds. The full
lifecycle 2-ready → 3-progress → 4-auto-review → 5-human-review can
collapse into a single 20-second UI-poll window. If your test relies on
seeing a card "in the In-Progress lane" to decide what to do, you will
miss it.

Pattern: poll `/api/jobs/{id}` every 2-3 s for state changes, and only
use the DOM to act once the state is the one you want (e.g. click
Complete from `5-human-review`).

The [`todo-app-full-test`](../frontend/e2e/exploratory/todo-app-full-test.spec.ts)
probe demonstrates this pattern.

## Why this layer exists

The lane-group ids (`backlog | active | decide`) are user-facing labels
chosen by ADR-0026. The state ids (`0-backlog` … `7-archive`) are the
runtime tags on disk and in the API. The two are deliberately decoupled
so lane groups can move between layouts without renaming the underlying
state machine. Automation needs both views — `data-states` is the bridge
exposed in the DOM so probes don't hardcode the mapping.
