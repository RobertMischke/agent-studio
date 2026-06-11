# Runtime Log Analysis Report Contract

This is the per-report sub-contract for the
[`runtime-log-analysis`](../SKILL.md) skill. It pins the Markdown skeleton,
the JSON sidecar shape, and the reference patterns the report uses, so that
two invocations of the skill on the same input produce comparable artifacts.

The generic shape this contract specialises is documented in
[`docs/reports/analysis-reports.md`](../../../../docs/reports/analysis-reports.md) and
validated by
[`docs/schemas/analysis-report.schema.json`](../../../../docs/schemas/analysis-report.schema.json).
This document does **not** introduce a new schema. It pins one valid
configuration of the existing schema (`topic = "runtime-observability"`).

## 1. Filename and storage

| Artifact | Path | Notes |
|----------|------|-------|
| Markdown body | `<workspace>/logs/analysis/<project>/<reportId>.md` | Durable. The reviewer reads this. |
| JSON sidecar | `<workspace>/logs/analysis/<project>/<reportId>.json` | Best-effort. Same stem as the Markdown file. |
| Workspace scope | `<workspace>/logs/analysis/_workspace/<reportId>.{md,json}` | Used when `scope.kind = "Workspace"`. |

`reportId` is a ULID or UUID v7 so lexical sort matches creation order. The
skill never writes the on-disk files directly; it returns the Markdown body
and (optionally) the fenced JSON block, and the orchestrator stores both
through `AnalysisReportStore.AppendAsync`.

## 2. Markdown skeleton

Use this skeleton verbatim, in this order. Section headings are stable so
downstream consumers can extract them with a regex.

```markdown
# Runtime log analysis: <project> [/ <jobId> / run <runIndex>]

> **Verdict:** <one sentence: clean / noisy / failing / inconclusive>, with the load-bearing reason.
> **Window:** <UTC from> .. <UTC to>
> **Inputs:** <N structured events> from <M files>; <K parse warnings>; <test artefacts present? yes/no>

## Repeated errors
<one bullet per finding, citing references by short label>

## Slow operations
<one bullet per (subsystem, operation) group with p50/p95/p99 ms>

## Noisy events
<one bullet per dominant event, with count and share>

## Missing correlation ids
<one bullet per affected subsystem>

## Suspicious sequences
<one bullet per pattern; mark unverified ones explicitly>

## Tests-passed-with-runtime-errors
<one bullet per (test id, runtime error) pair, or "no test artefacts">

## Notes
<one or two sentences on data quality, parse warnings, or limitations>

## Evidence
<numbered list of references, matching the JSON `references[]` array>

## Follow-up suggestions
<numbered list of suggested follow-up tasks; the user creates them>

```

The order matters: a reviewer scrolling top-to-bottom must see the verdict
first and the typed findings before the prose. If a section has no
findings, omit the entire section, do not leave an empty heading. The
Notes, Evidence, and Follow-up sections are always present (they may be
short).

## 3. Fenced JSON sidecar

Append exactly one fenced JSON block at the very end of the Markdown reply.
The orchestrator strips the fences and persists the object as
`<reportId>.json`. If you cannot produce a valid object, omit the fence
entirely; the report is then `parseStatus = "Unstructured"` and the
Markdown body remains the contract.

```json
{
  "schemaVersion": 1,
  "reportId": "<ULID or UUID v7>",
  "createdAt": "<UTC ISO-8601 with Z>",
  "scope": {
    "kind": "Project",
    "project": "<project>",
    "jobId": null,
    "runIndex": null,
    "timeWindow": null
  },
  "producer": {
    "kind": "Manual",
    "agent": "claude",
    "participantId": null
  },
  "trigger": "Manual",
  "topic": "runtime-observability",
  "summary": "<one sentence repeating the verdict>",
  "severity": "Info|Warn|High|Critical",
  "parseStatus": "Structured",
  "tags": ["runtime-log-analysis"],
  "references": [
    { "kind": "Job",          "ref": "<project>/<lane>/<jobId>", "label": null },
    { "kind": "Run",          "ref": "<jobId>:<runIndex>",       "label": null },
    { "kind": "LogSlice",     "ref": "<jobId>:<runIndex>:42-58", "label": "first repeated http.request.failed" },
    { "kind": "RuntimeEvent", "ref": "http.request.failed:01HXYZ...", "label": null },
    { "kind": "Screenshot",   "ref": "<jobId>/results/before.png",     "label": null },
    { "kind": "Doc",          "ref": "docs/operations/runtime/observability.md", "label": null }
  ],
  "findings": [
    {
      "topic": "repeated-error",
      "severity": "High",
      "message": "<one-line summary; e.g. http.request.failed x42 grouped by host:status>",
      "evidenceRefs": ["<jobId>:<runIndex>:42-58"]
    },
    {
      "topic": "slow-operation",
      "severity": "Warn",
      "message": "<subsystem>.<operation> p95 = 812ms over 23 samples; budget unspecified",
      "evidenceRefs": ["<jobId>:<runIndex>"]
    },
    {
      "topic": "tests-passed-with-runtime-errors",
      "severity": "Critical",
      "message": "Spec X passed but the same correlationId emitted level=Error twice in the runtime stream.",
      "evidenceRefs": ["<jobId>/results/runtime/spec-x.jsonl", "<jobId>:<runIndex>:99-104"]
    }
  ],
  "followUpTaskSuggestions": [
    {
      "title": "Investigate http.request.failed cluster",
      "summary": "42 failures share host=api.example.com and status=502. The agent should reproduce locally and add a regression probe.",
      "priority": "High",
      "relatedTopic": "RuntimeObservability",
      "targetState": "1-preparation",
      "createdJobId": null
    }
  ]
}
```

`createdJobId` always starts as `null`. The user (or the existing
task-creation entry point with explicit confirmation) fills it in when the
suggestion becomes a real queued job. The skill never sets it.

## 4. Reference patterns

Every `evidenceRefs` entry must resolve to a `references[]` entry of the
correct `kind`. The shapes mirror
[`docs/reports/analysis-reports.md` §6](../../../../docs/reports/analysis-reports.md#6-references).

| Finding category | Preferred reference kind(s) |
|------------------|------------------------------|
| `repeated-error` | `LogSlice` for the JSONL line range, optionally `RuntimeEvent` for the canonical event id. |
| `slow-operation` | `Run` (the CLI invocation that produced the timings) plus one `LogSlice` for the slowest sample. |
| `noisy-event` | `LogSlice` covering one example burst; `Run` if the burst spans the whole run. |
| `missing-correlation-id` | `LogSlice` plus a `Doc` ref to the producer's expected schema. |
| `suspicious-sequence` | One `LogSlice` per ordered pair; `RuntimeEvent` ids when the producer emits canonical ids. |
| `tests-passed-with-runtime-errors` | The test attachment as `Screenshot` (when an image) or as a `Doc`-style path under `<job>/results/`, plus a `LogSlice` for the runtime error. |

`LogSlice` ids are `<jobId>:<runIndex>:<startLine>-<endLine>` against
`<job>/logs/runtime/<yyyy-mm-dd>.jsonl`. The slice is a pointer; the bytes
stay in the JSONL file.

## 5. Severity rubric

| Severity | When to choose |
|----------|----------------|
| `Info` | Diagnostic findings only (data-quality notes, low-volume noise). |
| `Warn` | Performance regressions, missing correlation ids, single-digit error clusters. |
| `High` | Repeated errors at scale (≥ 10 in window), suspicious-sequence violations of a known invariant, slow-operation p95 above stated budget. |
| `Critical` | Any `tests-passed-with-runtime-errors` pairing, any `level: Fatal`, any data-loss or auth-related error cluster. |

The report's top-level `severity` is the maximum of its findings'
severities. A report with no findings is `Info`.

## 6. Stable section headings (regex contract)

Downstream consumers may extract sections with these regexes. Keep them
exact:

- `^# Runtime log analysis: ` for the title.
- `^## Repeated errors$`
- `^## Slow operations$`
- `^## Noisy events$`
- `^## Missing correlation ids$`
- `^## Suspicious sequences$`
- `^## Tests-passed-with-runtime-errors$`
- `^## Notes$`
- `^## Evidence$`
- `^## Follow-up suggestions$`

If a section has no findings, omit the heading. Do not rename or merge
sections; downstream extraction breaks silently.

## 7. Worked example

A complete report covering one job with 142 events, three repeated-error
clusters, one slow-operation finding, and one tests-passed-with-errors
pairing lives in
[`fixtures/sample-report.md`](fixtures/sample-report.md) (paired with
[`fixtures/sample-report.json`](fixtures/sample-report.json)). The fixtures
are also used by the structural test in
`backend.Tests/RuntimeLogAnalysisSkillTests.cs`.

## 8. What this contract does NOT specify

- The exact wording of finding messages (free-form prose with a length
  limit per the schema).
- The list of project-specific domain invariants for
  `suspicious-sequence`. Each project may extend the list in its own
  README; this contract only specifies the finding shape.
- Retention. Reports follow the workspace's analysis-report retention rule
  (`<workspace>/logs/analysis/`); see
  [`docs/reports/analysis-reports.md` §7.3](../../../../docs/reports/analysis-reports.md#73-retention).
