# Code review step

You are running a **user-triggered code review** against the task's full
change set - the combined diff of **every commit attributed to this
task**, not just its most recent commit. This is **not** the multi-aspect
auto-review pass; it is a single, focused review chosen by the user with a
model they picked. Your job is to produce one short narrative + one
verdict.

Question to answer: **does the change set do what the task asks for and
not introduce regressions, dead code, broken types, redundant work, or
obviously bad code visible in the change?** The diff below may span several
commits (e.g. a feature commit plus a later test/doc commit); judge the work
as a whole. A later commit being test- or doc-only does **not** mean the
feature is missing - look across the entire diff for the implementation.
Focus on what is in the diff, not on the rest of the codebase.

Verdicts:

- `pass` - the diff is clean and the change does what the prompt asks.
- `concerns` - something is worth flagging but the work is shippable
  (mild duplication, missed reuse opportunity, minor scope creep, comment
  that no longer matches the code).
- `block` - a regression, broken type, dropped error path, clear task-goal
  miss, redundant reimplementation of already-present behavior, or obvious
  half-finished/stubbed work visible in the diff. Block is for things that
  should not ship until fixed.

Do not accept a change merely because it compiles or touches plausible files.
Block when the submitted work clearly does not solve the task, blindly redoes
work the evidence says already exists, or leaves the product on the old path
because the new implementation is not wired.

## Project / Job

- **Project:** `{{project}}`
- **Id:** `{{job_id}}`
- **Title:** {{job_title}}
- **Commits under review:** `{{commit}}`
- **Reviewer model:** `{{model}}`

## Task body (`prompt.md`)

```
{{task_body}}
```

## Diff under review

```
{{diff}}
```

## What you must emit

A short paragraph (under 200 words) of your code-review reasoning, then
exactly one verdict sentinel on its own line, using the same grammar
the auto-review aspect runners use:

```
[[ASPECT_VERDICT: status=<pass|concerns|block>; summary=<one short sentence>]]
```

Then end with `[[TASK_DONE]]` on its own line.

## Things you should *not* do

- Do not modify any file.
- Do not run shell commands or tools.
- Do not block on style / naming preferences without a concrete bug or
  regression behind them.
- Do not attempt to re-do the work yourself or propose unrelated
  refactors.
