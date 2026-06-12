# Wiki Document Classification

The Wiki tree should not show every metadata axis. Its job is fast scanning.
The default row signal should stay small, monochrome, and limited to the two
questions an operator usually asks first:

1. Has this document drifted?
2. Which time direction does it describe?

Detailed evidence stays in the document Report tab.

## Primary Signals

| Signal | Meaning | Tree display |
|---|---|---|
| Drift | Whether the document still matches the current concept and implementation. | Structured slot: `Drift A-D` |
| Direction | Whether the document points to current, future, past, or mixed behavior. | Structured slot: `Current`, `Future`, `Past`, `Mixed` |

The tree row should render these as two quiet fields, not as colorful badges:
`Drift B | Direction Future`. The first field is the reliability grade. The
second field is the time direction of the document.

## Drift

Drift is a judgment about mismatch.

| Grade | Meaning |
|---|---|
| A | The document is a reliable current reference. |
| B | The document is mostly useful but needs minor reconciliation. |
| C | The document is useful as context, but should not be trusted without checking current code or product decisions. |
| D | The document is stale, misleading, obsolete, or superseded. |

Drift can come from implementation changes, concept changes, naming changes,
newer documents taking ownership, or a document mixing current and future state
without saying so.

## Direction

Direction replaces the vague phrase "temporal state" in the UI.

| Direction | Meaning |
|---|---|
| Current | The page describes behavior that is expected to be true now. |
| Future | The page describes planned, target, proposed, or vision behavior. |
| Past | The page describes old behavior, migration history, or a superseded approach. |
| Mixed | The page combines current and planned/past material and should be split or clearly labeled. |

## Health Score

Do not call this a confidence score.

The current number is a derived document health score:

```text
health = 100 - (drift.score * 100)
```

That means it is a convenience rollup of the drift estimate, not a statistical
confidence value and not a model-certainty value. It can be useful in a report,
but it should not be a primary Wiki tree chip until the scoring model is more
formal.

Recommended handling:

- Keep the tree to Drift + Direction.
- Keep health score in reports, where the derivation can be explained.
- Rename any visible "confidence score" label to "health score".
- Only promote the score back into the tree if it gets a stable rubric and
  users find it actionable.

## Secondary Signals

These signals are still useful, but they should live in the report or a details
panel:

| Signal | Why secondary |
|---|---|
| Implementation state | Often overlaps with Direction and can make the tree noisy. |
| Quality | Needs a clearer rubric before it is actionable in one tiny chip. |
| Duplicate | Important, but better shown in the report until duplicate ownership is formally defined. |

## Report Contract

When a user clicks a classification chip, the Wiki should open the Report tab
and jump to the matching section:

| Chip | Report anchor |
|---|---|
| Drift | `#why-drift` |
| Direction | `#temporal-reasoning` |
