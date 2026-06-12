# Metadata

This folder contains document-level metadata used by the Wiki to reason about
documentation drift, implementation timing, and whether a page is current
documentation or future-facing vision.

The JSON files under [documents/](documents/) are deliberately visible in the
physical Wiki tree. They are not hidden sidecars: operators can open them,
inspect the raw source, and compare their status against the linked document.

## Structure

| Folder | Purpose |
|---|---|
| [documents/](documents/README.md) | Per-document JSON metadata samples. |
| [reports/](reports/README.md) | Human-readable HTML reports based on the metadata. |
| [usage/](usage/README.md) | Proposed per-run Wiki document usage statistics and schema. |

## Metadata Axes

| Axis | Meaning |
|---|---|
| `drift.grade` | A to D grade for how far the document has drifted from current implementation and concept state. |
| `temporalState` | Stored JSON field for the UI's Direction signal: current, future, past, or mixed. |
| `documentMode` | Whether the page is operational documentation, concept documentation, vision, or a mixed design record. |
| `implementationState` | Whether the described behavior is implemented, partially implemented, planned, or intentionally aspirational. |
