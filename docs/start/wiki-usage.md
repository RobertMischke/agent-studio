# Wiki Usage Statistics

This folder defines the proposed run-level usage telemetry for Wiki documents.
The goal is to make document usage visible from both directions:

- from a Wiki document: which agent runs visited, used, or changed it
- from a task run: which Wiki documents influenced the processing

The initial concept report lives in
[../reports/wiki-usage-statistics.html](./wiki-usage-statistics.html).

## Event Semantics

| State | Meaning |
|---|---|
| `visited` | A tool opened, read, searched, or previewed the document. |
| `used` | The run output, patch, decision, or report relied on the document. |
| `changed` | The run edited, moved, created, or deleted the document. |

## Proposed Artifacts

| Artifact | Producer | Purpose |
|---|---|---|
| `task-wiki-usage.json` | per-run post-step | Task-local document usage facts. |
| `wiki-usage-index.json` | aggregation job | Project-level lookup by document path and task id. |
| Wiki usage panel | frontend | Shows visits, usage, changes, last task, and tool-use mix per document. |
