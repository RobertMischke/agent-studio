# Activity Log Edge-Case Taxonomy

This document anchors the next-generation chat mockup in real Activity Logs, so implementation can reduce design drift instead of inventing cases during coding.

## Audit Scope

Local Activity Log sweep on 2026-05-05:

| Metric | Count |
|--------|-------|
| `cli-output.log` files scanned | 136 |
| Total lines sampled | 27,634 |
| Highest line-count job | `runbook/6-archive/integrationstest-auf-co-pilot-cli-umstellen`, 817 lines |
| Highest tool-density job | `agent-taskboard/6-archive/chat-read--grep-wiederholunge-mit-weight-darstellen`, 364 tool-ish matches |
| Highest image-density job | `agent-taskboard/4-review/visual-evidence-priority-screenshots-clickable`, 100 image mentions |
| Highest orchestrator/watchdog density | `agent-taskboard/6-archive/images-and-protocol`, 94 control events |

The sample covers ready, review, completed, and archived jobs across the `agent-taskboard` and `runbook` watched projects.

## Event Families

| Event family | Real symptom in logs | Default chat rendering | Expanded rendering | Verbose Debug rendering |
|--------------|----------------------|------------------------|--------------------|-------------------------|
| Tool burst | Long read, search, edit, shell, test sequences dominate the transcript. | One compact row with total count, families, failures, duration, changed files, tests, and artifacts. | Table of individual calls with target, result, duration, and raw line range. | Tool-density heatmap, failure bands, retry chain, and raw trace links. |
| Watchdog quiet loop | `[watchdog] Agent has been quiet`, `Still silent`, `Agent resumed streaming`. | Slim supervisor row, low urgency when the agent resumes. | Quiet duration, last output line, resumed timestamp, and session continuity. | Time band showing silent gaps and recovery. |
| Watchdog kill | `Killed after ... of silence`, then a stopped or failed run. | Strong supervisor row with stopped status. | Kill threshold, last output, retry options, and whether continuation should reuse or rebuild context. | Timeline marker plus cause explanation. |
| Orchestrator reissue | Fast `Done` or `NoOp` after a follow-up, then `[reissue]`. | One decision row with reason and retry budget. | Evidence, sentinel state, previous run, action, and token budget. | Causality chain from user input to reissue. |
| Heuristic outcome | `Could not classify the agent's reply` when no hard sentinel matched. | Warning-level orchestrator row. | Raw log slice, matched heuristic, missing sentinel, and policy action. | Parser confidence and line-range filter. |
| Needs-input loop | `[[TASK_NEEDS_INPUT:...]]` followed by orchestrator loop decisions. | One decision row with loop counter and short answer. | Question, answer source, budget, and human handoff option. | Loop counter, repeated questions, and circuit-breaker threshold. |
| Capture fail | `[capture-fail] No claude session id from this run; next follow-up will rebuild from disk`. | Low-noise system warning. | Missing session id, fallback plan, target files, and run context. | Session continuity panel. |
| Duplicate sentinel | Multiple `[[TASK_DONE]]` or similar terminal markers in one log. | No duplicate message, only a small parse note if needed. | First match, duplicates ignored, and policy result. | Sentinel parse table. |
| Image evidence | Attachments, Playwright scratch images, copied result screenshots, and protocol image rows. | Inline evidence strip only when visuals affect review. | Lightbox with scratch path, durable `results/` path, caption, source tool, and task link. | Artifact timeline and retention status. |
| Test fail and retry | Failing Playwright or backend tests followed by a passing retry. | Tool row shows `1 failed` plus final status. | Failed command, exit code, artifact links, retry command, and final result. | Test timeline and flaky-risk tag. |
| Token spike | Job, supporting job, and orchestrator usage appears as quota or cost pressure. | Small context chip, never a dashboard inline. | Per-run and per-actor token split. | Token heatmap and cost rollup. |
| Schema drift | Structured Markdown or JSON report cannot be parsed cleanly. | Human-friendly system row: `Report is unstructured`. | Expected schema, actual parse issue, raw Markdown, and recovery action. | Contract score and drift history. |
| User intervention | User steering lands while a run is active or between continuations. | Right-aligned user turn plus target chip. | Interrupt, continue, stop, or create-task target. | Run boundary and override reason. |
| Cross-task project steering | Side-sheet chat references active tasks, queue status, docs, or roadmap. | Project chat turn in side sheet, not the task evidence stream. | Linked tasks, generated jobs, and doc changes. | Project-level causality view. |

## Fixture Jobs To Keep

Use these jobs as fixture sources for the implementation tasks. Do not hardcode their current paths in app code; copy representative log fragments into unit-test fixtures.

| Fixture purpose | Representative job |
|-----------------|--------------------|
| Tool-heavy transcript | `chat-read--grep-wiederholunge-mit-weight-darstellen` |
| Project-switch and long review chat | `chat-wechsel-zwischen-projekten-nicht-gut-moeglich` |
| Watchdog and image protocol | `images-and-protocol` |
| Visual evidence and screenshots | `visual-evidence-priority-screenshots-clickable` |
| Multi-lane orchestrator review | `orchestrator-review-lane-and-bubble-up` |
| Human review lane | `lanes-with-explicit-human-review-step` |
| Sorting regression with repeated tool work | `das-sortieren-ist-buggy` |
| Copilot CLI integration run | `integrationstest-auf-co-pilot-cli-umstellen` |
| Context usage orphan | `context-usage-orphan-2026-05-05` |

## Projection Contract

The first implementation slice should create a pure `ConversationEvent` projection before touching layout. It must classify these event kinds:

| Kind | Required fields |
|------|-----------------|
| `toolBurst` | `runId`, `count`, `families`, `failures`, `duration`, `files`, `tests`, `artifacts`, `rawRange` |
| `supervisor.wait` | `runId`, `severity`, `quietSeconds`, `resumed`, `killed`, `lastOutputRange` |
| `decision.orchestrator` | `decisionType`, `reason`, `evidence`, `action`, `retryBudget`, `tokenUsage`, `rawRange` |
| `agent.needsInput` | `question`, `loopIndex`, `loopLimit`, `answerSource`, `nextAction` |
| `system.captureFail` | `cliType`, `sessionName`, `fallback`, `rawRange` |
| `system.parserWarning` | `expectedKind`, `message`, `rawRange`, `dedupeKey` |
| `artifact.image` | `caption`, `sourcePath`, `durablePath`, `sourceTool`, `taskLink`, `runId` |
| `metric.token` | `scope`, `inputTokens`, `outputTokens`, `reasoningTokens`, `cost`, `window` |
| `taskMarker` | `jobId`, `lane`, `title`, `runId`, `duration`, `tokens`, `evidenceLinks` |
| `message.user` | `body`, `target`, `createdAt`, `attachments` |
| `message.agent` | `actor`, `body`, `markdown`, `collapsed`, `rawRange` |

Every event must keep a link back to raw log evidence. The compact chat can hide technical detail, but it cannot delete traceability.

## Acceptance Notes

- Conversation mode is a projection, not a replacement for Trace.
- Tool and image rows must expose failures without requiring expansion.
- Watchdog and orchestrator rows must be visually separate from task-agent prose.
- Side-sheet project chat may summarize cross-task patterns, but task-specific raw evidence remains attached to the task.
- The mockup is allowed to be more interactive than the first production slice. The job queue should implement the contract incrementally.
