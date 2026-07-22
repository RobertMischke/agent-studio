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

Read the epic goal above. Inspect the repository read-only if it helps you scope the work. Then break the goal into the smallest set of independently shippable sub-tasks that, taken together, deliver and verify the epic. For each sub-task write:

- A short local `id`, unique within this plan, used by dependency edges.
- A short, imperative `title` (what the sub-task delivers).
- A self-contained `prompt`: everything an agent needs to do that one sub-task without re-reading the epic. State the goal, the relevant files/areas, and the acceptance criteria.
- A `purpose`: `delivery` for implementation or `verification` for an independent check.
- A `dependsOn` array of local ids. Keep the graph acyclic. A verification task must depend on every delivery task whose output it checks.

Guidance:

- Prefer 2-8 sub-tasks including verification. Merge trivia; split anything that is really two deliverables.
- Order them so earlier sub-tasks unblock later ones.
- Each sub-task should be doable in a single focused agent run.
- Do not invent work the epic did not ask for.
- Add a verification task when the goal is risky, cross-cutting, user-visible, or needs proof that should not be produced and judged by the same implementation run. Do not add ceremonial verification to trivial work.
- A verification prompt must inspect the submitted revision and actual evidence such as command results, test reports, screenshots, or result artifacts. It must disclose missing, stale, or contradictory evidence. It must not decide from success wording, terminal sentinels, or keyword scans alone.

## Required output format

End your substantive reply with a fenced JSON block of exactly this shape, followed only by the required terminal sentinel:

```json
{
  "subTasks": [
    { "id": "delivery", "title": "Build the deliverable", "prompt": "Full self-contained delivery instructions.", "purpose": "delivery", "dependsOn": [] },
    { "id": "verify", "title": "Verify the delivered goal", "prompt": "Independently inspect the submitted revision and real evidence, then report gaps honestly.", "purpose": "verification", "dependsOn": ["delivery"] }
  ]
}
```

The orchestrator parses that JSON block, validates the local dependency DAG, and creates one card per entry under this epic. It stamps every generated card with goal-decomposition provenance and translates local ids into stable task-key dependencies. An entry with a blank title is skipped. After the JSON block, end your reply with `[[TASK_DONE]]` on its own line (use `[[TASK_BLOCKED:unclear-goal]]` only if the goal is too unclear to decompose, replacing the example reason with the actual short reason).
