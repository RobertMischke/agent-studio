# Agent eval set for measuring context splits

Status: plan / skeleton.
Card: `ASS-1676`.

This document defines a small internal eval set for measuring whether a broad
monolithic agent context or a domain-split context helps local coding agents do
typical taskboard work. The goal is not a benchmark suite for public claims. It
is a repeatable local measurement loop for deciding whether context splits make
agents faster, cheaper, or more reliable on this repository.

## 1. Question

Measure two context variants against the same small task set:

| Variant | Context shape | Intended comparison |
|---|---|---|
| `monolith` | Root guidance plus broad project docs as one large handoff. | Baseline for "give the agent everything". |
| `domain-split` | Root guidance plus the smallest relevant domain map: runner, pipeline, tasks, frontend, or CLI. | Candidate for "give the agent a routed slice". |

The eval is useful only when both variants run against the same commit, CLI,
model, autonomy mode, task prompt, and verification budget.

## 2. Eval task catalog

Keep the set small enough to run by hand during a refactor review. Each task
should be concrete, use real repository concepts, and have file anchors that can
be checked without reading the whole transcript.

| Id | Task shape | Prompt skeleton | Expected anchor evidence |
|---|---|---|---|
| `AEV-001-add-field` | Add field X to a backend task DTO and expose it to the frontend type. | "Add a nullable `<field>` to the task summary payload, thread it through the mapper, render it as a small read-only chip where task metadata is already shown, and run the matching backend/frontend tests." | DTO/type file hit, mapper hit, one UI consumer hit, focused tests run. |
| `AEV-002-change-mapper` | Change mapper Y without touching unrelated domain logic. | "Change `<mapper>` so `<source>` maps to `<target>` under `<condition>`. Keep persisted task JSON unchanged and add or update the narrow mapper test." | Mapper file hit, existing test file hit, no broad persistence rewrite. |
| `AEV-003-find-callers` | Find all callers of Z and propose the smallest safe edit. | "Find every caller of `<method>` and update only the call sites that need the new argument. Report any deliberately skipped caller with a reason." | Complete caller list, changed call sites, skipped caller rationale, compile or targeted tests. |
| `AEV-004-adjust-validation` | Adjust validation behavior around task creation or movement. | "Tighten validation for `<request field>` so invalid values return a typed error visible to the UI. Preserve API-owned mutation boundaries." | Request model or validator hit, endpoint/service hit, UI error path if user-visible, API tests. |
| `AEV-005-run-right-tests` | Choose and run the right tests for a narrow change. | "Given this diff, identify and run the smallest defensible test set. Explain why each command is relevant and what risk remains." | Test command list, exit codes, changed-file-to-test mapping, residual risk. |

The concrete placeholders should be bound per run in the eval harness or run
sheet. Do not hard-code transient line numbers into the task prompts; keep the
expected anchors as path and symbol checks.

## 3. Per-run log record

Log one JSON object per eval task and context variant. Store run records as
JSONL so they can be appended from scripts or copied from task artifacts.

```json
{
  "schemaVersion": 1,
  "evalRunId": "2026-06-09-local-split-check",
  "evalTaskId": "AEV-001-add-field",
  "variant": "domain-split",
  "repoCommit": "<git-sha>",
  "agentCli": "codex",
  "model": "<model>",
  "startedAt": "2026-06-09T10:00:00Z",
  "finishedAt": "2026-06-09T10:12:30Z",
  "durationMs": 750000,
  "taskSuccess": "pass",
  "successReason": "field threaded through DTO, mapper, UI, and targeted tests",
  "expectedFileHits": [
    {
      "path": "backend/Services/Tasks/<file>.cs",
      "symbol": "<symbol>",
      "hit": true
    }
  ],
  "missedExpectedHits": [],
  "unexpectedBroadReads": [],
  "toolCalls": {
    "total": 42,
    "byTool": {
      "shell": 35,
      "apply_patch": 2,
      "update_plan": 1
    }
  },
  "tests": [
    {
      "command": "dotnet test backend.Tests --filter <filter>",
      "exitCode": 0,
      "reason": "covers mapper behavior"
    }
  ],
  "tokenUsage": {
    "inputTokens": 0,
    "cachedInputTokens": 0,
    "outputTokens": 0,
    "totalTokens": 0,
    "estimatedCostUsd": 0.0,
    "source": "task-token-aggregate"
  },
  "reviewNotes": []
}
```

## 4. Scoring

Use a simple rubric first. Add finer math only after the manual review feels too
subjective.

| Metric | Type | Pass signal |
|---|---|---|
| Task success | Primary | Requested change is correct, scoped, and verified. |
| Anchor recall | Primary | Agent touched every expected path or symbol. |
| Anchor precision | Primary | Agent avoided unrelated rewrites and speculative broad edits. |
| Test fit | Primary | Tests match the changed surface and have recorded results. |
| Tool calls | Secondary | Lower is better when success and quality are equal. |
| Runtime | Secondary | Lower is better when success and quality are equal. |
| Cost | Secondary | Lower is better when success and quality are equal. |

Represent task success as `pass`, `partial`, or `fail`. Do not let low cost hide
a failed task. Cost and runtime only decide between variants that both succeeded
with comparable quality.

## 5. Run procedure

1. Choose one repository commit and create disposable worktrees or restore points
   for each variant.
2. Bind the placeholders in the task catalog to current code symbols.
3. Run `monolith` and `domain-split` with the same CLI, model, autonomy,
   timeout, and verification budget.
4. Collect the run log from task artifacts, CLI transcript events, git diff,
   test output, and token aggregates.
5. Review each run against the expected anchors before comparing totals.
6. Summarize by task and by variant: success count, partial count, failures,
   median tool calls, median runtime, and estimated cost.

## 6. Minimal artifact layout

Use this layout under a task `results/` folder or a project-local experiment
folder. Keep generated eval evidence out of the source tree unless it becomes a
reviewed report.

```text
agent-eval-split-measurement/
  run-config.json
  eval-runs.jsonl
  summary.md
  variants/
    monolith/
      AEV-001-add-field/
      AEV-002-change-mapper/
    domain-split/
      AEV-001-add-field/
      AEV-002-change-mapper/
```

## 7. Open decisions

| Decision | Recommendation |
|---|---|
| Where to run destructive eval tasks | Use disposable worktrees or throwaway task branches. |
| Where to get cost | Prefer the task token aggregate already persisted by the platform; fall back to CLI transcript usage only when the aggregate is missing. |
| Who scores success | Human review first; automate only the anchor checks and totals. |
| How many tasks per pass | Start with five. Expand only when the first results are ambiguous. |
| When to promote to product feature | After two or three manual runs show the same signal and the log fields stop changing. |

