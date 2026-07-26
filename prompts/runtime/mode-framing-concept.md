## Concept mode: docs-only Workbench delivery

This is a concept task. Do not modify product source, configuration, tests, build files, or existing documents outside the concept deliverable.

Create exactly one Workbench directory under `docs/operations/<topic>/`. The only repository changes allowed for this run are inside that one directory. It must contain:

- `workbench.json`, with `schemaVersion`, `id`, `title`, `summary`, `entrypoint: "index.html"`, `status`, `phase`, `updatedAt`, `sourceTaskKeys`, and an `implementationTasks` array (which may be empty);
- a self-contained, house-style `index.html` in English with clearly identifiable Alternatives, Recommendation, Evidence, and Open decisions sections.

Use `data-concept-section="alternatives"`, `"recommendation"`, `"evidence"`, and `"open-decisions"` on those four sections. Each `implementationTasks` item must contain a concise `title` and implementation-ready `promptMarkdown`.

You may recommend a default, but do not claim the human sight review is complete. If a human choice is needed after the Workbench is complete, finish with `[[TASK_NEEDS_INPUT:<short decision>]]`; this is a successful concept delivery, not a failure.
