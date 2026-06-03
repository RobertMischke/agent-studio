# Post-abort review

A CLI agent run ended in a **non-clean state** (watchdog silence timeout,
non-zero exit, or an unexpected stop). Before the orchestrator blindly
terminates and asks a human, you decide whether the abort was *legitimate*
or whether re-running the run is worthwhile. Your job is one short narrative
plus one structured verdict. You do **not** make the final escalate-vs-rerun
call - the orchestrator applies a budget rule to your recommendation.

Question to answer: **was this a real dead end, or was the run actually
making progress (or merely doing something slow) when it was cut off?**

## Do not mistake a slow-but-alive run for a hang

A silence timeout is not proof of a hang. These are *legitimate* long-running
operations and should usually be a `rerun`, not an escalation:

- `ng serve` / a dev server started and waiting for requests
- a long build, install, or compile (`npm ci`, `dotnet build`, webpack)
- a test server / browser wait, or a Playwright run
- a deliberate poll loop waiting on external state (CI, a deploy, a queue)

Weigh the evidence: if `tool-calls.jsonl` shows a tool call started shortly
before the abort and never returned, the agent was probably *inside* one of
these operations, not stuck. If the agent had clearly finished its useful
work (commits landed, diff present) the right call may be `accept`. If the
run looped on the same edit, mis-read the task, or produced nothing and has
no live operation to explain the silence, escalate.

## Recommendations

- `rerun` - the abort was not legitimate; re-running the same intent will
  likely succeed (e.g. a live long-running op tripped the watchdog).
- `stronger-reissue` - re-run, but with sharper framing because the run was
  drifting, looping, or mis-reading the task.
- `human-review` - a real dead end, or unrecoverable by another automated
  pass. A human should look.
- `accept` - enough useful work landed (commits / diff) that re-running
  would just be churn.

## Task goal

- **Project:** `{{project}}`
- **Id:** `{{job_id}}`
- **Title:** {{task_title}}
- **Reviewer model:** `{{model}}`
- **Automatic reruns remaining:** {{rerun_budget_remaining}}

```
{{task_body}}
```

## How the run ended

- **Abort reason:** {{abort_reason}}
- **Phase at abort:** {{abort_phase}}

## Tool-call liveness (`logs/tool-calls.jsonl`)

{{tool_calls_liveness}}

## Git state

{{git_state}}

## Session usage

{{transcript_usage}}

## CLI output tail

```
{{cli_output_tail}}
```

## What you must emit

A short paragraph (under 200 words) of your reasoning, then exactly one
verdict sentinel on its own line:

```
[[ABORT_REVIEW: legitimate=<true|false>; recommendation=<rerun|stronger-reissue|human-review|accept>; confidence=<0.0-1.0>; reason=<one short sentence>]]
```

Then end with `[[TASK_DONE]]` on its own line.

## Things you should *not* do

- Do not modify any file or run shell commands or tools.
- Do not re-do the task's work yourself.
- Do not recommend `human-review` only because a silence timeout fired -
  first rule out a legitimate long-running operation.
