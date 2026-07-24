# Runtime prompt registry contract

Runtime prompts are versioned Markdown assets, but their effective content,
review state, and operational use come from several durable layers. This
contract keeps those layers observable without moving pipeline decisions into
the prompt files.

## Sources and precedence

`RuntimePromptService` is the mandatory load and use boundary for templates in
`prompts/runtime/`. A caller selects a prompt identifier and owns the decision
about when it runs. The service resolves content in this order:

1. An application-wide override under
   `<TaskRepository>/.metadata/prompt-overrides/<name>.md`, or the configured
   `PromptTemplates:OverridePath`.
2. The shipped `prompts/runtime/<name>.md` default.
3. For a configured pipeline step, the project-specific
   `project-settings.json` value at `pipelineSteps[<step>].prompt` replaces the
   effective content for that use.

Project overrides remain project configuration. They do not modify the shipped
Markdown or the application-wide override. Callers that use a project override
must pass it through `RuntimePromptService.UseProjectOverride` so the use is
recorded against the bound prompt identifier.

## Change provenance

The prompt catalogue obtains `last change` from the latest git commit that
changed the shipped Markdown file. The human-readable date is the primary UI
value. The commit SHA remains available as provenance detail, not as the
freshness label.

## Review companion

Each shipped prompt may have an adjacent
`prompts/runtime/<name>.md.meta.json` companion:

```json
{
  "lastReviewedAt": "2026-07-23T20:16:54.4656277+00:00",
  "reviewedBy": "runtime-prompt-audit",
  "status": "current",
  "findings": []
}
```

`status` is one of `current`, `needs-review`, or `stale`. Each finding contains
`code`, `severity`, and `message`, plus optional `projectName` and `stepId`
provenance. `PromptReviewService` writes companions atomically. A companion
that cannot be parsed is surfaced as `needs-review`; it is not silently treated
as current.

A single-prompt or all-prompts review checks:

- reachability registered in `PromptUsageCatalog`, including explicitly
  unreachable prompts;
- repository paths named by prompt content;
- every configured project pipeline prompt override, including its project and
  step provenance and its line-level difference count from the shipped prompt;
- overrides whose configured step has no live prompt binding.

An error finding makes the prompt `stale`; a warning makes it `needs-review`.
Informational override differences do not make a prompt stale. Orphaned
overrides are returned by the all-prompts review even when no shipped prompt
can own them.

The API surface is:

- `GET /api/admin/prompts`
- `GET /api/admin/prompts/{name}`
- `POST /api/admin/prompts/{name}/review`
- `POST /api/admin/prompts/review-all`

Both review mutations accept an optional `reviewedBy` value and persist their
result in the companion files.

## Append-only call ledger

Every successful `RuntimePromptService.Render` and
`RuntimePromptService.UseProjectOverride` records one JSON object at
`<TaskRepository>/logs/prompt-calls.jsonl`, or at the configured
`PromptTelemetry:Path`.

Each row carries:

| Field | Meaning |
|---|---|
| `timestamp` | UTC use time and historical pricing time. |
| `promptId` | Runtime prompt filename identifier. |
| `version` | SHA-256 content hash of the effective template or override. |
| `source` | `file`, `application-override`, or `project-override`. |
| `inputTokens` | Estimated rendered-input tokens. |
| `tokensEstimated` | Marks the token value as an estimate. |
| `model` | Model when the caller can provide it. |
| `project` | Project context when available. |
| `step` | Pipeline or runtime step context when available. |

The ledger is append-only and best effort. A telemetry write failure must not
fail prompt rendering. Readers skip malformed rows so a torn final append does
not hide older history.

The catalogue aggregates total and seven-day calls, last call, 14 daily
buckets, and content-hash version history. A prompt is operationally dead when
it has no calls or no call in 30 days. This signal prioritizes review; it does
not replace the static reachability check.

## Cost boundary

Prompt cost is a historical, theoretical API-equivalent input estimate. Each
ledger row is priced at its own timestamp through `TokenPricing`, with zero
output and cache tokens because the prompt loader does not observe a completed
model response. Unknown models stay unpriced and contribute to the explicit
unpriced-call count rather than a false `$0.00`.

The prompt UI must keep the same API-price and CLI-subscription reservation as
other cost surfaces. Call count and estimated cost do not measure result
quality. Comparing prompt versions with result quality is a deferred benchmark
capability; the versioned ledger is only its data foundation.

## Ownership and verification

- `backend/Features/Prompts/RuntimePromptService.cs` owns resolution, rendering,
  content hashes, and the mandatory telemetry seam.
- `backend/Features/Prompts/PromptCallTelemetryService.cs` owns the ledger and
  call, version, activity, and cost aggregation.
- `backend/Features/Prompts/PromptReviewService.cs` owns git provenance,
  companions, reachability checks, and project override comparison.
- `backend/Features/Prompts/PromptAdminService.cs` owns the catalogue and detail
  read models.
- `frontend/src/app/features/orchestrator/components/prompt-admin-landing/` and
  `prompt-admin-panel/` render the overview and prompt detail.

Backend tests pin persistence, malformed-row tolerance, version grouping,
historical pricing, review findings, and override provenance. Frontend
component tests pin the table and detail projections. The Playwright prompt
registry spec proves the live review, dead-prompt finding, project override
origin, and both-theme overview and detail rendering.
