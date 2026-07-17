# Epic decomposition: {{title}}

You are running an **epic planning step**, not a coding step. Do not write code, do not edit files in the repository, and do not commit anything. Your only job is to decompose the epic's overarching goal into a concrete, ordered list of actionable sub-tasks.

## The epic's goal

{{prompt_text}}

---

Context for this planning run:

- Working directory (for read-only inspection only): `{{working_directory}}`
- Git repository for status/diff: `{{repository_path}}`
- Job folder for task metadata and evidence: `{{job_folder}}`
- Epic prompt path on disk: `{{prompt_path}}`

## What to produce

Read the epic goal above. Inspect the repository read-only if it helps you scope the work. Then break the goal into the smallest set of independently shippable sub-tasks that, taken together, fully deliver the epic. For each sub-task write:

- A short, imperative `title` (what the sub-task delivers).
- A self-contained `prompt`: everything an agent needs to do that one sub-task without re-reading the epic. State the goal, the relevant files/areas, and the acceptance criteria.

Guidance:

- Prefer 2-8 sub-tasks. Merge trivia; split anything that is really two deliverables.
- Order them so earlier sub-tasks unblock later ones.
- Each sub-task should be doable in a single focused agent run.
- Do not invent work the epic did not ask for.

## Required output format

End your reply with a single fenced JSON block (and nothing after it) of exactly this shape:

```json
{
  "subTasks": [
    { "title": "First deliverable", "prompt": "Full self-contained instructions for sub-task 1." },
    { "title": "Second deliverable", "prompt": "Full self-contained instructions for sub-task 2." }
  ]
}
```

The orchestrator parses that JSON block and creates one card per entry under this epic; an entry with a blank title is skipped. After the JSON block, end your reply with `[[TASK_DONE]]` on its own line (use `[[TASK_BLOCKED:goal-too-unclear-to-decompose]]` only if the goal is too unclear to decompose, replacing the example reason with the actual short reason).
