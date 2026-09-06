# Product Runtime Log Capture

Storage layout, capture paths, and retention rules for the build-time slice
of [Product Runtime Observability](../../../ROADMAP.md#product-runtime-observability).

The schema for one event is
[`docs/app/schemas/product-runtime-event.schema.json`](../../app/schemas/product-runtime-event.schema.json).
This document covers **where captured events live on disk and how long they
stay there**. The contract document
(`docs/operations/runtime/observability.md`, queued in
`product-runtime-observability-contract`) describes the event envelope and
the relationship to the Agent Message Bus.

> **Language:** English. See [AGENTS.md](../../../AGENTS.md#documentation-language).

## 1. Two streams, never mixed

The product produces two append-only streams. They share file conventions
(JSONL, one event per line, UTF-8) but the schemas and the audiences differ:

| Stream | Source | Layout root | Schema |
|--------|--------|-------------|--------|
| Agent Message Bus | Orchestrator, supervisor, agents, skills | `<workspace>/logs/bus/` | [agent-message.schema.json](../../app/schemas/agent-message.schema.json) |
| Product Runtime Events | The software the agents are building | `<job>/logs/runtime/` and `<workspace>/logs/runtime/` | [product-runtime-event.schema.json](../../app/schemas/product-runtime-event.schema.json) |

The Agent Message Bus may carry an artifact reference of kind `runtime-event`
that points at one of these records, but it never embeds them. Cross-stream
writes (a runtime event into `logs/bus/`, or a bus message into
`logs/runtime/`) are bugs; the validators on each side reject the wrong shape.

## 2. File layout

### 2.1 Job-scoped (default)

When the runtime event is bound to one orchestrator job (the agent CLI is
running, a Playwright spec is exercising a feature inside one job folder),
events go next to the rest of the job's evidence:

```
<job>/
  job.json
  prompt.md
  logs/
    cli-output.log              # raw agent CLI stdout/stderr (existing)
    runtime/
      <yyyy-mm-dd>.jsonl        # validated runtime events for that day
      <yyyy-mm-dd>.jsonl.warnings.jsonl   # parse warnings, raw lines preserved
  results/
    runtime/                    # Playwright per-spec capture, harvested
      <spec-slug>.jsonl
      <spec-slug>.jsonl.warnings.jsonl
```

The day file is in **UTC** to avoid timezone-driven splits within one run.

### 2.2 Workspace- or project-scoped

When the event belongs to a whole project (a backend that watches multiple
jobs over time, a long-running dev process) the workspace's `logs/runtime/`
tree is the home:

```
<workspace>/
  logs/
    runtime/
      <project>/<yyyy-mm-dd>.jsonl
      <project>/<yyyy-mm-dd>.jsonl.warnings.jsonl
      _workspace/<yyyy-mm-dd>.jsonl       # workspace-scoped events
```

The workspace root is the watched workspace (the parent of `projects/`),
not this source repository. Runtime events are evidence about the built
software's behaviour during a run; they do not belong in the app's git
history.

## 3. Capture paths (adapter-style)

Producers in the built software emit events through whatever logging
library they already use. Capture is library-agnostic; the orchestrator
and Playwright add **sniffers**, never required dependencies.

### 3.1 Backend stdout / stderr

Adapter: [`RuntimeEventStdoutAdapter`](../../../backend/Services/Runtime/RuntimeEventStdoutAdapter.cs).

A line that starts with `{` is parsed as a `ProductRuntimeEvent`. A valid
event is appended to the per-job or per-project day file via
[`RuntimeEventWriter`](../../../backend/Services/Runtime/RuntimeEventWriter.cs);
a JSON-shaped line that fails to parse or validate becomes a
[`RuntimeEventParseWarning`](../../../backend/Models/ProductRuntimeEvent.cs) and
is appended to the sidecar `.warnings.jsonl`. Plain log text (no leading
`{`) flows through to `cli-output.log` untouched and never enters the
runtime stream.

### 3.2 Backend log files

Same adapter, different driver: a file-tail loop reads a producer's log
file line by line and feeds each line to `RuntimeEventStdoutAdapter.Ingest`.
Producers that already write JSONL events directly (winston, structlog,
serilog with `JsonFormatter`) need no transformation - the file is the
runtime stream.

### 3.3 Playwright / browser console

Helper: [`startRuntimeCapture`](../../../frontend/e2e/helpers/runtime-capture.ts).

Attaches to `page.on('console')`, `page.on('pageerror')`, and
`page.on('requestfailed')`. Producer-emitted console JSON that matches the
event envelope flows through verbatim. Plain `console.log("...")` is
wrapped as a `frontend.console` event so the audit trail still has the raw
text. Page errors become `frontend.pageerror`. Failed network requests
become `frontend.request.failed`.

Output path resolution:

- `JOB_RESULTS_DIR` is set by the agent task orchestrator before spawning a
  CLI (see [`CliExecutionServiceBase`](../../../backend/Services/Cli/CliExecutionServiceBase.cs)).
  When set, the helper writes to `<JOB_RESULTS_DIR>/runtime/<spec>.jsonl`
  so the events end up under the job folder alongside Playwright
  screenshots.
- Local dev defaults to `frontend/e2e/test-results/runtime/<spec>.jsonl`
  which is gitignored and treated as scratch.

### 3.4 Test-run attachments and result files

Playwright reporters can attach arbitrary files to a test result. The
[`JobArtifactReporter`](../../../frontend/e2e/helpers/job-artifact-reporter.ts)
already harvests screenshots/videos/traces into the job's `results/`
folder when `JOB_RESULTS_DIR` is set; runtime JSONL files written under
`<JOB_RESULTS_DIR>/runtime/` are picked up alongside them.

For non-Playwright test frameworks, the same convention applies: write
JSONL files into `<JOB_RESULTS_DIR>/runtime/<test-name>.jsonl` and the
runtime stream picks them up.

## 4. Parse warnings

The "preserve raw logs and expose parse warnings" rule from the task
contract is load-bearing: a producer that emits a slightly-broken event
must remain debuggable without re-running the failing scenario. The
mechanism is uniform across all capture paths:

- The structured stream is written to `<file>.jsonl`.
- Malformed input is written as a JSON record to `<file>.jsonl.warnings.jsonl`
  with `sourcePath`, `lineNumber`, `reason`, `rawLine`, and `recordedAt`.

The warnings sidecar is **not** itself a runtime event stream; consumers
that aggregate runtime events skip it. The
[`RuntimeEventReader`](../../../backend/Services/Runtime/RuntimeEventReader.cs)
returns warnings inline with successfully-parsed events for the same
purpose: a parser that swallows malformed input silently is worse than no
parser at all.

## 5. Retention

| Location | Lifetime | Why |
|----------|----------|-----|
| `<job>/logs/runtime/*.jsonl` | Persists with the job folder | Same retention as `cli-output.log`: kept for review until the workspace's own retention policy archives the job. |
| `<job>/logs/runtime/*.warnings.jsonl` | Persists with the job folder | Same. Producers fix on the next pass; the warning record is the diagnostic input. |
| `<job>/results/runtime/*.jsonl` | Harvested into the job folder when `JOB_RESULTS_DIR` is set | Test-derived evidence behaves like screenshots. See [docs/system/contracts/protocol-style.md §4](../../system/contracts/protocol-style.md#image-flow). |
| `<workspace>/logs/runtime/<project>/*.jsonl` | Project-level audit trail | Lives next to `<workspace>/logs/bus/<project>/*.jsonl`; trimmed by the workspace's own log-rotation tooling, not by this repository. |
| `frontend/e2e/test-results/runtime/*.jsonl` | Wiped on the next Playwright run | Local dev scratch only. The path is gitignored under `test-results/` (see this repository's `.gitignore`). |
| `<source-repo>/logs/runtime/...` | Never created | The source repository's `logs/` folder is gitignored, but production runtime events do not flow into it. The source repo holds the app, not its runtime evidence. |

## 6. Source-commit hygiene

The task contract requires that captured runtime events do **not** pollute
source commits. Three layers protect this:

1. **Task folders live in the central `TaskRepository`**, not in this app's
   source repository or the product checkout
   (`docs/system/contracts/filesystem.md`). Anything under
   `<job>/logs/runtime/` or `<job>/results/runtime/` is therefore outside
   product-source Git history by default.
2. **The task-store evidence checkout owns its ignore policy.** The lifecycle
   worker keeps workspace-level `logs/bus/` local and ignored because it is a
   rotating runtime projection. Job-scoped `logs/runtime/` remains task
   evidence. See [workspace repository lifecycle](../workspace-repository-lifecycle.md).
3. **`test-results/` is gitignored in this repository** (`.gitignore` line
   for `test-results/`), so any local-dev runtime capture written under
   `frontend/e2e/test-results/runtime/` cannot be committed.

If a producer writes a heavy artifact (a stack dump, a frame buffer) the
event should reference an attachment under `<job>/results/` rather than
inlining bytes into the JSONL line.

## 7. Sample event

```json
{
  "schemaVersion": 1,
  "timestamp": "2026-05-06T12:00:00.000Z",
  "level": "Info",
  "event": "render.first-paint",
  "subsystem": "frontend",
  "operation": "bootstrap",
  "duration": { "ms": 142.7 },
  "status": "Ok",
  "payload": { "route": "/" }
}
```

Captured live by the Playwright spec
[`frontend/e2e/runtime-console-capture.spec.ts`](../../../frontend/e2e/runtime-console-capture.spec.ts).
The companion warning record produced by the same spec for a malformed
JSON-like line:

```json
{
  "sourcePath": "/.../runtime-console-capture.jsonl",
  "lineNumber": 4,
  "reason": "json parse: Unexpected token n in JSON at position 1",
  "rawLine": "{not really json",
  "recordedAt": "2026-05-06T12:00:00.155Z"
}
```

## 8. Tests

| Layer | Test | Asserts |
|-------|------|---------|
| Backend | `backend.Tests/RuntimeEventCaptureTests.cs::Writer_AppendsToJobDayFile_AndReaderRoundTripsEvent` | round-trip: writer + reader on a job day file |
| Backend | `RuntimeEventCaptureTests::Writer_RejectsInvalidEvent_BeforeWriting` | invalid event throws and never lands on disk |
| Backend | `RuntimeEventCaptureTests::Reader_PreservesGoodEvents_AndReportsParseWarnings` | mixed file: good lines parse, malformed lines surface as warnings with raw line |
| Backend | `RuntimeEventCaptureTests::Writer_AppendWarning_WritesSidecarBesideJsonl` | warnings sidecar layout |
| Backend | `RuntimeEventCaptureTests::StdoutAdapter_KeepsPlainLogLines_OutOfRuntimeStream` | adapter is library-agnostic; plain `INFO ...` text is left untouched |
| Backend | `RuntimeEventCaptureTests::StdoutAdapter_ReportsParseWarning_ForJsonLikeButInvalid` | JSON-like-but-invalid stdout becomes a warning, not an event |
| Backend | `RuntimeEventCaptureTests::WorkspaceLayout_RoutesByProjectAndUtcDay` | path layout for project-scoped and workspace-scoped streams |
| Backend | `RuntimeEventCaptureTests::Reader_ReturnsEmpty_WhenFileMissing` | missing file is not an error |
| Frontend | `frontend/e2e/runtime-console-capture.spec.ts::captures structured events, wraps plain logs, and records parse warnings` | console + pageerror + warnings sidecar end-to-end |
| Frontend | `frontend/e2e/runtime-console-capture.spec.ts::routes output under JOB_RESULTS_DIR/runtime when env var is set` | `JOB_RESULTS_DIR` routing matches the documented path |
