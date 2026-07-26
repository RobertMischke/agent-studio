# UI Task Iteration Pipeline Contract

Status: implemented mechanics for UI Task Pipeline Part 1. The Human Gate UI
and its finish/feedback controls belong to Part 2.

## Routing

Coding tasks use `ui-iteration-task-pipeline` when all applicable conditions
hold:

1. `EvidenceGate.MatchesUiHeuristic(taskType, tags, title)` matches.
2. When an attributed change set is available, it contains a rendered UI file
   according to `EvidenceGate.ChangeSetTouchesUi(changedFiles)`.
3. The project has not disabled `pre-ui-pipeline-routing` through
   `ProjectSettings.PipelineSteps`.

Read-only planning and research tasks always keep the read-only pipeline. A
project can set `PipelineSteps["pre-ui-pipeline-routing"].maxIterations` from 1
through 10. The default is 4.

The UI pipeline has a normal pre bracket and core agent run, followed by two
mandatory dependent steps:

```text
core-agent-run
  -> post-ui-iteration-artifact
  -> post-ui-human-review-gate
```

The two post steps cannot be disabled. This keeps visual evidence and human
review as invariants even when other project steps are reordered or overridden.

## Per-iteration result

Iteration `N` must write both of these under the task folder:

```text
results/ui-iteration-NNN/
  changes.md
  <one or more non-empty PNG, JPG, JPEG, WEBP, or GIF files>
```

`changes.md` briefly states what changed in that iteration. A screenshot or
Playwright capture from an earlier iteration does not satisfy the current gate.
The runner appends this contract, the iteration number, and the absolute result
directory to the agent prompt. At run admission it creates the current
`ui-iteration-NNN` directory as a durable checkpoint. Retries keep using that
directory; only feedback from a persisted Human Review marker advances to
`N + 1`.

If either item is missing, `post-ui-iteration-artifact` fails and the task does
not enter review. The runner issues one bounded evidence-only continuation for
the same iteration. A second failure escalates through the normal system funnel
to `5e-escalated` with category `ui-iteration-cap`.

## Part 2 Human Gate hand-off

After a complete iteration, the runner briefly stamps phase
`awaiting-review`, moves the card to `5-human-review`, and leaves
`steer-pending.json` in the moved task folder. This marker is not subject to the
generic steer timeout.

The stable marker shape is:

```json
{
  "waitStartedAt": "2026-07-22T12:00:00Z",
  "kind": "ui-iteration-review",
  "question": "Review the visual result and choose finish or provide feedback for the next iteration.",
  "cliType": "codex",
  "uiIterationReview": {
    "contractVersion": 1,
    "pipelineId": "ui-iteration-task-pipeline",
    "iteration": 2,
    "maxIterations": 4,
    "capReached": false,
    "artifactPaths": ["ui-iteration-002/after.png"],
    "changeDescriptionPath": "ui-iteration-002/changes.md"
  }
}
```

All paths in `uiIterationReview` are relative to `results/`. Part 2 must:

- treat `kind == "ui-iteration-review"` and `contractVersion == 1` as the
  consumer discriminator;
- present the listed artifacts and change description;
- complete the task when the human chooses finish;
- submit feedback through the existing Continue flow for another iteration;
- never submit another Continue when `capReached` is true. The runner enforces
  this boundary and escalates such an attempt to `5e-escalated`.

`completion-marker.json` temporarily protects the move to Human Review from a
backend crash. It is cleared after the move; `steer-pending.json` remains until
Part 2 resolves the human decision or a new accepted continuation starts.
