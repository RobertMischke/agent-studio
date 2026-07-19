# Quota snapshot events at run start / end (AGT-2100)

Status: Data-collection contract. Pure capture, no analysis, no UI.

This is the vorlauf datapoint for the cap-forecast theme (TE-4 owns the science
plan). Forecasts need history, so we start collecting **now**: every coding run
writes the CLI's cached quota snapshot to the run metadata at run-start and at
run-end. Every day of data counts.

## What is emitted

Two bus events per run, one at each boundary:

- **Run start** - emitted right after the CLI process spawns
  (`ProjectRunner.EmitQuotaSnapshotToBus(..., QuotaSnapshotPhases.Start)`), next
  to the existing `RunStarted` lifecycle mirror.
- **Run end** - emitted when the CLI process exits
  (`... QuotaSnapshotPhases.End`), next to the `RunFinished` mirror.

Each event is one `AgentMessage` with `kind: "observation"`,
`topic: "quota-snapshot"`, `role: "evidence"`, participant `runtime:taskboard`,
tags `["quota-snapshot", "phase:<start|end>", "cli:<type>"]`. The compact
`QuotaSnapshotEvent` payload (`backend/Shared/Models/QuotaSnapshotEvent.cs`)
carries the stable-named fields.

## Where it lands

The bus is workspace-scoped, one line of JSON per event (JSONL):

```
<TaskRepository>/logs/bus/<project>/<yyyy-MM-dd>.jsonl
```

Locate a run's snapshots by filtering that project's day files on
`jobId` / `runId` (the same `runId = "<jobId>:<startedAtTicks>"` that ties the
run-start and run-end pair together, and ties token-usage turns to the run).
There is no per-job-folder copy; the stream is the workspace `logs/bus/` tree,
the same place `token-usage` events live.

## Payload fields

Envelope: `createdAt`, `participantId`, `kind`, `topic`, `project`, `jobId`,
`runId`, `summary`, `tags`. The `payload` object:

| Field | Meaning |
|---|---|
| `phase` | `start` or `end` |
| `cliType` | CLI whose quota this is (`claude`, `codex`, ...) |
| `model` | model the run used (start: planned; end: `CliExecution.Model`) |
| `thinkingLevel` | effective thinking level for the run |
| `plan` | subscription plan when known (`Max`, `Pro`, ...) |
| `source` | how the snapshot was sourced (`/usage`, `/status`, `footer`) |
| `fetchedAt` | when the cached snapshot was probed (UTC) |
| `snapshotAgeSec` | age of the cached snapshot at emit time |
| `ttlSeconds` | cache TTL the runner uses (default 600) |
| `stale` | `true` when `snapshotAgeSec > ttlSeconds` (fetchedAt-Alter) |
| `missing` | `true` when no snapshot was cached for the CLI at all |
| `suspicious` | AGT-2064 trust flag copied through |
| `suspiciousReason` | why the snapshot was flagged suspicious |
| `error` | last probe error, when partial data still survived |
| `windows[]` | all quota windows: `label`, `usedPct`, `resetAt`, `resetLabel`, `used`, `limit`, `unit` |

Optional fields are omitted when null to keep each line compact; `phase`,
`cliType`, `ttlSeconds`, `stale`, `missing`, `suspicious`, and `windows` are
always present.

## Cached-only, no forced probe

The event uses `QuotaService.GetCachedFor(cliType)` - the in-memory snapshot,
**never** a fresh probe. Probing spawns a separate CLI process for several
seconds; forcing one on every run would add an extra CLI call per run, which
this feature explicitly avoids. Instead the snapshot's honest age
(`snapshotAgeSec` + `stale`) is written so a reader knows how fresh the reading
was. The cache is kept warm by the existing paths (the `/api/cli/quota`
stale-while-revalidate poll and pre-launch admission), so most start events see
a fresh snapshot. A run-end event reflects the same cached value the run started
with unless one of those paths refreshed it meanwhile; it is not a post-run
re-measurement.

## Known distortions

Keep these in mind before reading the numbers as ground truth:

- **Parser glitches.** A probe can misparse a CLI's `/usage` output; the
  AGT-2064 plausibility gate flags an implausible downward jump as
  `suspicious` (also carried on the event). Treat `suspicious: true` rows as
  low-trust.
- **Parallel runs share the window.** Several concurrent runs on the same CLI
  subscription draw from one quota window. A per-run start/end delta is **not**
  that run's own consumption - other runs (and out-of-band `/usage` calls) move
  the same counters.
- **Staleness.** A `stale: true` (or `missing: true`) row means the cached
  reading was older than the TTL (or absent); `snapshotAgeSec` quantifies it.
- **Coarse `resetAt`.** `resetAt` is derived from the CLI's reset label and can
  be approximate; `resetLabel` preserves the original string.
- **End is not a re-measurement.** As above, run-end mirrors the cached value,
  not a fresh post-run probe.

## Code + tests

- Payload + pure builder: `backend/Shared/Models/QuotaSnapshotEvent.cs`
- Emit: `AgentMessageBusBridge.EmitQuotaSnapshotAsync`
- Wiring: `ProjectRunner.EmitQuotaSnapshotToBus` (run-start + run-end call sites)
- Tests: `backend.Tests/QuotaSnapshotBusEmitTests.cs` (builder age/stale/missing
  projection + bus emission shape + one-JSON-line-per-event)
