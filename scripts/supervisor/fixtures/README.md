# Layer 3 system-review fixtures

JSONL fixtures shaped like Agent Message Bus day-files. Each line is one
`AgentMessage` (see [`docs/system/schemas/agent-message.schema.json`](../../../docs/system/schemas/agent-message.schema.json)).

## `sample-bus.jsonl`

A single day's worth of mixed traffic for a fictional `agent-taskboard`
project. The fixture is hand-crafted to exercise every health check in
`system-health-check.mjs`:

| Health check | Triggering messages |
|---|---|
| Stuck loops | `01HXYZ00000000000000000004`, `...05`, `...06`, `...07` (four orchestrator reissue / heuristic-fallback decisions in a row on `job-alpha`) |
| Token spikes | `01HXYZ00000000000000000008` (`tokens.output=42000`, `dollars=12.7`) |
| Repeated failed/cancelled runs | `...09` (Failed) and `...11` (Cancelled), both on `job-alpha` |
| Repeated interventions | `...12`, `...13`, `...14` (cancel-run, pause-pickup, force-fail) |
| Many supporting jobs without accepted review | `...16`-`...19` on `job-bravo` (four `support:*` decisions, no `JobStateMoved` to `6-completed`) |
| Jobs reached review with weak evidence | `...22` (`job-charlie` moved to `4-auto-review` with zero artifacts in the run) |
| Long silent periods | gap from `...22` (10:30:30Z) to `...23` (13:30:00Z) on `agent-taskboard` (~3h with no activity while a job sat in `4-auto-review`) |
| Backend crash markers | `...24` (`kind=error` from `runtime:taskboard`) |

`job-delta` (`...25`-`...28`) is the negative control: it reaches review
with screenshot artifacts attached so the "weak evidence" check stays off
that run.

## Usage

```sh
node scripts/supervisor/system-health-check.mjs --fixture scripts/supervisor/fixtures/sample-bus.jsonl
```

Or against a real workspace bus directory:

```sh
node scripts/supervisor/system-health-check.mjs --workspace C:/Projects/agent-taskboard-workspace
```

Both forms accept `--json` for machine-readable output and `--out PATH`
to write the Markdown report to disk.
