# Metadata

This folder contains document-level metadata used by the Wiki to reason about
documentation drift, implementation timing, and whether a page is current
documentation or future-facing vision.

Document-level metadata lives beside the document it describes:

```text
docs/architecture/bus/agent-message-bus.md
docs/architecture/bus/agent-message-bus.md.meta.json
docs/architecture/bus/agent-message-bus.md.report.html
```

The adjacent `.meta.json` companion is the structured source of truth. The
adjacent `.report.html` file is generated from that JSON and explains the
findings in a readable form. The Wiki tree attaches this metadata to the source
document row instead of showing every companion file as a separate navigation
item.

## Structure

| Folder | Purpose |
|---|---|
| [reports/](reports/README.md) | Aggregate metadata reports and audit pages. |
| [usage/](usage/README.md) | Proposed per-run Wiki document usage statistics and schema. |

## Generation

Run [`../../scripts/wiki/generate-companion-metadata.mjs`](../../scripts/wiki/generate-companion-metadata.mjs)
to regenerate adjacent companion JSON and HTML report files. The generator
reads existing companions and can migrate older central metadata records into
the adjacent layout.

## Metadata Axes

| Axis | Meaning |
|---|---|
| `drift.grade` | A to D grade for how far the document has drifted from current implementation and concept state. |
| `temporalState` | Stored JSON field for the UI's Direction signal: current, future, past, or mixed. |
| `documentMode` | Whether the page is operational documentation, concept documentation, vision, or a mixed design record. |
| `implementationState` | Whether the described behavior is implemented, partially implemented, planned, or intentionally aspirational. |
| `review.sourceFingerprint` | Hash, size, line count, and capture time used to detect whether the source document changed since the review. |
| `findings[]` | Structured explanation entries that feed the adjacent HTML report. |
