---
name: runtime-log-analysis
description: Read product runtime observability artifacts (structured JSONL events plus the raw fallback log) and produce a reviewable analysis report covering repeated errors, slow operations, noisy events, missing correlation ids, suspicious domain sequences, and tests that passed while runtime errors still occurred.
trigger: user
mutates_code: false
mutates_queue: false
sentinel: TASKBOARD-SKILL-RUNTIME-LOG-ANALYSIS
---

# Skill: Runtime Log Analysis

> Sentinel: `TASKBOARD-SKILL-RUNTIME-LOG-ANALYSIS`. Echo this string in your first reply when the orchestrator probes whether the skill loaded.

Turn the **product runtime observability** stream into a reviewable narrative
about how the *built software* actually behaved during a run. This skill is
the analysis side of the contract documented in
[`docs/product-runtime-observability.md`](../../../docs/product-runtime-observability.md)
and the capture layout in
[`docs/product-runtime-log-capture.md`](../../../docs/product-runtime-log-capture.md).

## When to invoke

User-triggered only. Typical entry points:

- A user clicks a project-level "Runtime log analysis" button (or runs the
  skill directly from a CLI session).
- A reviewer wants to know what the app did during the just-finished run, not
  what the agent said about it.
- A regression hunt: pair a green Playwright run with the runtime stream from
  the same job and check whether tests passed while the app silently logged
  errors.

Do **not** run this skill automatically from a hook, watcher, or auto-mode
loop. The runtime stream is read-only output; analysis is a deliberate user
action.

## Hard constraints

These are policy, not preference. Violating any is a defect.

1. **Read-only.** Do not modify any source file, prompt, schema, or job
   folder. Do not move jobs between lanes. Do not call task-creation
   endpoints.
2. **No silent follow-ups.** A finding may be paired with a
   `followUpTaskSuggestion` in the JSON sidecar, but the suggestion is a
   draft. The user (or the existing task-creation entry point with explicit
   confirmation) turns it into a queued job.
3. **Two streams stay separate.** Runtime events live under
   `<job>/logs/runtime/` and `<workspace>/logs/runtime/<project>/`; bus
   messages live under `<workspace>/logs/bus/<project>/`. Do not mix them in
   a single output file.
4. **No PII or secrets in the report.** If a payload contains a user id,
   token, or other sensitive value, refer to it by length / hash / shape; do
   not copy it verbatim.
5. **Markdown is durable, JSON is best-effort.** If you cannot produce valid
   JSON for the sidecar, omit the fenced block entirely. An unstructured
   report is still a useful report (`parseStatus = "Unstructured"`).
6. **Parse failures are surfaced, not swallowed.** When a JSONL file fails to
   parse, render the raw report with a top-level warning; do not pretend the
   file was empty. See "Parse-failure behaviour" below.

## Inputs

The skill reads, in order of preference:

1. **Structured product runtime event JSONL** (the primary input).
   - Job-scoped: `<job>/logs/runtime/<yyyy-mm-dd>.jsonl`.
   - Project-scoped: `<workspace>/logs/runtime/<project>/<yyyy-mm-dd>.jsonl`.
   - Workspace-scoped: `<workspace>/logs/runtime/_workspace/<yyyy-mm-dd>.jsonl`.
   - Schema: [`docs/schemas/product-runtime-event.schema.json`](../../../docs/schemas/product-runtime-event.schema.json).
   - Companion warnings sidecar (`<file>.jsonl.warnings.jsonl`) is a parse
     diagnostic; surface its presence in the report but do not treat
     warnings as runtime events.
2. **Raw fallback log** (`<job>/logs/cli-output.log`). The agent's prose plus
   any plain text the producer printed alongside structured events. Use it
   when the structured stream is empty, malformed, or thin; quote sparingly.
3. **Optional test results** (`<job>/results/`, Playwright's
   `frontend/e2e/test-results/runtime/<spec>.jsonl`, xUnit / Vitest output).
   Required when the report needs to assert "tests passed while runtime
   errors still occurred".
4. **Optional screenshots, traces, or attached artefacts** under
   `<job>/results/`. Cited via the `Screenshot` reference kind; never
   embedded inline.

If the **only** input is the raw `cli-output.log` (no structured stream
present), produce an Unstructured report and recommend that the project
adopt the runtime envelope for the next run. Do not invent structured
findings from prose.

## Findings

The skill produces typed findings against the canonical
[`AnalysisReport`](../../../docs/analysis-reports.md) shape with
`topic = "runtime-observability"`. Six finding categories are required;
report only the ones that have evidence in the input. If a category has zero
evidence, omit it; do not pad.

| Category | `findings[].topic` | What it surfaces |
|----------|--------------------|------------------|
| Repeated errors | `repeated-error` | Events with `level: Error` or `Fatal` (or `status: Failed` / `Timeout`) grouped by `(event, error.type, error.code)`. Report count, first/last seen, sample `error.message`, and whether `error.retryable` was set. Cite a `LogSlice` ref to the JSONL line range. |
| Slow operations | `slow-operation` | Events with `duration.ms` set, grouped by `(subsystem, operation)`. Report p50, p95, p99 over the input window plus the slowest single observation. Flag any group whose p95 exceeds the producer-stated budget; if no budget is stated, flag the top three by p95. |
| Noisy events | `noisy-event` | Single `event` names whose count dominates the stream (top-1 share > 40 % or any one event > 1 / second sustained over a minute). Useful for catching debug `Trace`/`Debug` left in production paths. |
| Missing correlation ids | `missing-correlation-id` | Streaks of `Info+` events whose `subsystem` normally carries a `correlationId` but where the field is null. Report count, the dominant `event`, and whether the same `runId`/`jobId` was set on neighbouring events. |
| Suspicious domain sequences | `suspicious-sequence` | Domain-event ordering that violates a known invariant: e.g. `payment.declined` after `order.shipped`, `auth.session-rotated` without a preceding `auth.session-issued`, `render.first-paint` emitted twice for one `correlationId`. The skill does not invent invariants; it reports patterns the user (or a project-specific extension) flagged. When in doubt, classify as `unverified-sequence` and ask the user. |
| Tests-passed-with-runtime-errors | `tests-passed-with-runtime-errors` | Cross-check optional test results against the runtime stream for the same wall-clock window. If a test run reports green but the runtime stream contains `level: Error` / `Fatal` (or `status: Failed`) inside the test's `correlationId` / `traceId` / `runId`, surface every such pair. This is the single highest-value finding kind; never demote it to Info. |

Findings are **typed**, not free-form prose. Each one carries a short
`message`, an explicit `severity`, and `evidenceRefs` that point at
references in the report. The Markdown body explains; the JSON sidecar is
machine-filterable.

## Output

One report per invocation: a Markdown file plus an optional JSON sidecar.
Storage matches the shared analysis-report contract.

- **Markdown** (durable human artifact): `<workspace>/logs/analysis/<project>/<reportId>.md`.
- **JSON sidecar** (additive app contract): `<workspace>/logs/analysis/<project>/<reportId>.json`.
- For workspace-scoped runs, the project key is `_workspace` per
  [`docs/analysis-reports.md` §7](../../../docs/analysis-reports.md#71-locations).

The JSON sidecar validates against
[`docs/schemas/analysis-report.schema.json`](../../../docs/schemas/analysis-report.schema.json)
with these fixed fields:

- `topic`: `"runtime-observability"`.
- `producer.kind`: `"Manual"` (user-triggered) or `"SupportingAgent"` (when
  invoked from a CLI agent run).
- `trigger`: matches the producer kind; never `"Scheduled"` for this skill
  (see "Hard constraints" §1).
- `scope.kind`: `"Project"` (default), `"Task"` (when bound to one job
  folder), `"Run"` (when bound to a single CLI invocation), or
  `"TimeWindow"` (when the input is filtered by wall-clock).
- `severity`: highest finding severity in the report; `Info` if the only
  findings are diagnostic.
- `parseStatus`: `Structured` when this skill writes both files.

The full output contract (Markdown skeleton, fenced JSON example, reference
shapes) lives in
[`references/report-contract.md`](references/report-contract.md). Read it
before producing your first report.

## Process

1. **Resolve scope.** From the orchestrator-supplied `JOB_RESULTS_DIR`,
   workspace root, project name, and optional time window, build the list of
   JSONL files to read. Job-scoped is the default when a job is in scope;
   project-scoped when the user asked for a project-wide picture.
2. **Read the structured stream.** For each JSONL file, parse line by line.
   Discard malformed lines into a local `parseWarnings` buffer; preserve the
   raw line, the file path, and the line number.
3. **Read the warnings sidecar(s).** Each `<file>.jsonl.warnings.jsonl` is a
   pre-existing parse diagnostic from the capture layer. Add its rows to the
   same `parseWarnings` buffer; they are not runtime events.
4. **Aggregate.** Compute the six finding categories above. The reference
   helper script `scripts/aggregate-runtime-events.mjs` does this in one
   pass; you may run it directly or replicate its logic inline.
5. **Cross-check tests** (if test artefacts are available). Match
   `correlationId` or `traceId` / `runId` between the test run and the
   runtime stream; record any green-test / runtime-error pairings.
6. **Read the raw log if needed.** Tail `cli-output.log` only when the
   structured stream is empty or thin, and only to provide one or two
   verbatim quotes that contextualise an existing finding.
7. **Render the Markdown body** following the skeleton in
   `references/report-contract.md`.
8. **Append the fenced JSON sidecar** at the end of the Markdown reply if
   you can produce a valid object; otherwise omit it. The orchestrator
   stores both files via `AnalysisReportStore.AppendAsync`; the skill itself
   does not write the index.
9. **Cite references explicitly.** Every finding's `evidenceRefs` resolves
   to a `references[]` entry of the appropriate kind (`LogSlice`,
   `RuntimeEvent`, `Job`, `Run`, `Screenshot`, `Doc`).
10. **Stop.** Do not queue a follow-up task. Do not edit any file.

## Parse-failure behaviour

If the structured stream cannot be parsed at all (file missing, empty,
malformed beyond the per-line warnings buffer):

- Set `parseStatus = "Unstructured"` and produce a Markdown-only report.
- Open the report with a `> Warning:` blockquote that names the failed file
  and the reason.
- Surface the raw `cli-output.log` tail (last 50 lines) verbatim so the
  reviewer has *something* to read.
- Recommend, in the follow-up suggestions, that the producer adopt the
  runtime envelope or fix the malformed sink. Do not silently invent
  structured findings.

## Helper script

`scripts/aggregate-runtime-events.mjs` is a small Node script that reads one
or more JSONL files and prints a JSON aggregate matching the finding
categories above. It exists so the skill stays deterministic across CLIs:
the agent runs the script, reads its output, and turns the numbers into
narrative. The script never writes to disk and never calls the
task-creation API.

```bash
node .agents/skills/runtime-log-analysis/scripts/aggregate-runtime-events.mjs \
  <path-to-runtime-jsonl> [more-files...]
```

The script's own contract and a small fixture-driven test live in
[`scripts/README.md`](scripts/README.md).

## Anti-patterns

- Mixing the agent message bus and the product runtime stream in the same
  finding. They are joined by reference; cite the bus message id, do not
  copy the payload.
- Reporting "no errors" when the input was empty. The right answer is
  "stream empty / malformed / out of window"; an empty input is not the
  same as a clean run.
- Inventing a domain invariant ("payments must precede shipment") without
  user confirmation. Classify unverified ordering as `unverified-sequence`
  and ask.
- Quoting payload bytes into the Markdown when an attachment ref would do.
  The JSONL line is already on disk; cite it via a `LogSlice` ref.
- Auto-creating follow-up tasks. The skill suggests; the user creates.
- Running this skill on every commit, every Playwright run, or from a hook.
  Runtime-log analysis is user-triggered; automation belongs to the capture
  layer, not the analysis layer.
