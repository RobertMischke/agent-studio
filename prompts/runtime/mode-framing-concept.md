## Concept mode: repository Dossier delivery

This is a concept task. Do not modify product source, configuration, tests, build files, or existing documents outside the concept deliverable.

Create exactly one Dossier directory at `docs/<slug>/` in the project repository. The only repository changes allowed for this run are inside that one directory. The canonical result is the repository Dossier, not a copy under the task `results/` directory. A `results/` copy is optional. The Dossier must contain:

- `workbench.json`, with `schemaVersion`, `id`, `title`, `summary`, `entrypoint: "index.html"`, `status: "decision-pending"`, `phase`, `updatedAt`, `sourceTaskKeys`, and an `implementationTasks` array, which may be empty. Read this task's own visible key from the task context or job metadata and make `sourceTaskKeys` contain that card;
- a self-contained `index.html` in English with clearly identifiable Alternatives, Recommendation, Evidence, and Open decisions sections.

Use the calm house document layout. Read the style block at `docs/operations/haertung-verteilte-ausfuehrung/index.html` in the agent-taskboard repository and reuse it as the current style template. When the canonical article template from AGT-2536 is present in the repository, use that newer template instead. Keep headings as plain nouns, language factual, and reading width bounded. Support light and dark themes through CSS variables. Use inline SVG for diagrams. Do not invent a separate colour system or font family for this dossier.

Use `data-concept-section="alternatives"`, `"recommendation"`, `"evidence"`, and `"open-decisions"` on those four sections. Each `implementationTasks` item must contain a concise `title` and implementation-ready `promptMarkdown`.

Make the Dossier discoverable from the card. In the task job folder, name the exact repository-relative `docs/<slug>/index.html` path in both `results/deliverables.md` and `status.md`. The application may regenerate the rest of `status.md`, but preserves this Dossier reference. A Dossier without both task-file references is incomplete.

You may recommend a default, but do not claim the human sight review is complete. If a human choice is needed after the Dossier is complete, finish with `[[TASK_NEEDS_INPUT:<short decision>]]`; this is a successful concept delivery, not a failure.
