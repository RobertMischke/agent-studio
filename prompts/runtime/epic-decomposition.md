# Epic decomposition: {{title}}

You are running an **epic planning step**, not a coding step. Do not write code, do not edit files in the repository, and do not commit anything. Your only job is to decompose the epic's overarching goal into a concrete, ordered list of actionable sub-tasks.

Consult `docs/system/domains/model-routing-policy.md` as the authoritative source whenever you select, recommend, override, or explain a model and thinking level. Never let quota or cost cross its correctness-risk floors.

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

- A short, imperative `title` (what the sub-task delivers).
- A self-contained `prompt`: everything an agent needs to do that one sub-task without re-reading the epic. State the goal, the relevant files/areas, and the acceptance criteria.
- Inside each prompt, add a short `Plan position` section with `Role: delivery` or `Role: verification` and `Runs after:` followed by the exact titles of prerequisite sub-tasks, or `none`. Keep this plan acyclic. The global orchestrator maps these declared prerequisites to the existing task `references.dependsOn` API after the child cards have stable keys.

Guidance:

- Prefer 2-8 sub-tasks including verification. Merge trivia; split anything that is really two deliverables.
- Order them so earlier sub-tasks unblock later ones.
- Each sub-task should be doable in a single focused agent run.
- Do not invent work the epic did not ask for.
- The current Epic parser persists `title` and `prompt`; do not invent additional JSON fields. The existing lifecycle and sub-task factory create the child cards, set their `epicId`, and record the planning-spawn ledger.
- Add a verification task when the goal is risky, cross-cutting, user-visible, or needs proof that should not be produced and judged by the same implementation run. Do not add ceremonial verification to trivial work.
- Place each verification task after the delivery tasks it checks and name all of them in `Runs after:`. Its prompt must order the checks as a concrete checklist, name the expected evidence for every check, inspect the submitted revision and actual evidence such as diffs, command results, test reports, screenshots, or result artifacts, and record the result of each check.
- Verification must disclose missing, stale, or contradictory evidence. A planned or configured check is not evidence that it ran, and success wording, terminal sentinels, or keyword scans are not substitutes for observed output.

## Required output format

End your substantive reply with a fenced JSON block of exactly this shape, followed only by the required terminal sentinel:

```json
{
  "subTasks": [
    { "title": "Build the deliverable", "prompt": "Full self-contained delivery instructions.\n\n## Plan position\nRole: delivery\nRuns after: none" },
    { "title": "Verify the delivered goal", "prompt": "Independently inspect the submitted revision and real evidence, then report gaps honestly.\n\n## Plan position\nRole: verification\nRuns after: Build the deliverable" }
  ]
}
```

The existing Epic planning lifecycle parses that JSON block and creates one card per entry under this epic; an entry with a blank title is skipped. The Epic link and planning-spawn ledger make the decomposition origin traceable. The global orchestrator then translates the declared plan order into the existing stable-key dependency graph. After the JSON block, end your reply with `[[TASK_DONE]]` on its own line (use `[[TASK_BLOCKED:unclear-goal]]` only if the goal is too unclear to decompose, replacing the example reason with the actual short reason).
