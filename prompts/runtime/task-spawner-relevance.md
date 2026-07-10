# Task spawner — relevance & follow-up generation

A task in the **`{{source_project}}`** project has just finished. Your job is
to decide whether its change is relevant to a **different** project,
**`{{target_project}}`**, and — only when it clearly is — to author a complete
follow-up task for that project's agent.

The best available model runs this step precisely so the judgement is careful.
Be **conservative**: the point of this step is to keep the target project's
board free of noise, so judge **relevant** only when a follow-up in the target
project is genuinely warranted (a new user-facing capability, a removed
capability, a changed behaviour, or a contract the target project must track).
When in doubt, answer **no**.

## Relevance question

{{relevance_question}}

## Source task

- **Project:** `{{source_project}}`
- **Key:** `{{source_key}}`
- **Title:** {{source_title}}
- **Target project for a follow-up:** `{{target_project}}`

### Source task prompt (`prompt.md`)

```
{{task_body}}
```

### Status summary (`status.md`)

```
{{status_summary}}
```

### Change summary (commits + diffstat)

```
{{diff_summary}}
```

### Source commits

```
{{source_commits}}
```

### results/ folder inventory

```
{{results_inventory}}
```

## What you must emit

First a short paragraph (under 150 words) naming the concrete evidence that
makes the change relevant or not to `{{target_project}}`. Then, on its own line,
exactly one decision sentinel:

```
[[TASK_SPAWN: relevant=<yes|no>; reason=<one short sentence>]]
```

If — and only if — `relevant=yes`, also add the generated follow-up task in
these two sections:

```
### SPAWN_TITLE
<one concise, imperative title for the follow-up task, no key prefix>

### SPAWN_PROMPT
<a complete, self-contained task prompt for the target project's agent:
what changed in the source project, what the target project should now do
about it, and concrete acceptance criteria. Do not assume the target agent can
see the source repository — describe the change in enough detail to act on.>
```

Then end with `[[TASK_DONE]]` on its own line.

## Things you must *not* do

- Do not modify any file, run shell commands, or use tools — this is a judgement
  call only.
- Do not answer `relevant=yes` unless a follow-up task is clearly justified;
  a refactor, an internal-only change, or a tweak with no outward effect on
  `{{target_project}}` is **no**.
- Do not generate a follow-up prompt when `relevant=no`.
- Do not reference the source repository's file paths as if the target agent
  can open them; restate what it needs to know.
