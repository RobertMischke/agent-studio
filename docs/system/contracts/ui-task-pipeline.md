# UI Task Iteration Pipeline Contract

Status: implemented UI iteration evidence, automated visual QA, and the durable
Human Gate hand-off. The Human Gate UI and its finish/feedback controls belong
to Part 2.

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

The UI pipeline has a normal pre bracket and core agent run, followed by four
mandatory dependent steps:

```text
core-agent-run
  -> post-ui-iteration-artifact
  -> post-ui-visual-capture
  -> post-ui-visual-verdict
  -> post-ui-human-review-gate
```

The four post steps cannot be disabled. This keeps visual evidence, automated
inspection, and human review as invariants even when other project steps are
reordered or overridden.

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

## Automated visual QA

After the iteration evidence gate passes, AGT cards whose authoritative change
set touches `frontend/` run the visual QA pair. If changed-file provenance is
unavailable on an already selected UI pipeline card, the pair runs fail-closed.
Other card key families and authoritative backend-only change sets do not enter
this first slice.

`post-ui-visual-capture` starts the Angular app from the exact delivered
checkout with production configuration and a proxy to the current authority
backend. This is the stable-equivalent runtime shape without starting a second
backend. Routes come from explicit URLs in the card, known changed-component
mappings, and the stable task-detail fallback. At most four 1440 by 1000
headless Playwright captures are written under:

```text
results/ui-iteration-NNN/visual-qa/round-RRR/
  capture.json
  capture.log
  <route>--real.png
  verdict.json
  verdict.md
  model-response.txt
```

`post-ui-visual-verdict` attaches those screenshots to a Codex multimodal
one-shot. Its policy default is `gpt-5.4-mini` with `high` reasoning, the
bounded supporting-call tier in the model routing policy. Projects may use the
normal per-step override, but may not disable the gate. The reviewer returns
strict JSON with `acceptable` or `clear-defect` plus named visible defects from
the bounded categories `truncation`, `misalignment`, `placeholder-noise`,
`design-token-violation`, `overlap`, `overflow`, `unreadable`, and
`broken-layout`. Invalid JSON, missing screenshots, or an unavailable reviewer
is not interpreted as acceptable and is escalated with its receipt.

A first `clear-defect` result appends the exact defect list to the card prompt
through `TaskMutationService.AppendContinuationNote` and starts one Continue
round before the Human Gate is written. The binding action is pure code in
`VisualQaPolicy`; the model cannot grant itself another attempt. Prior use is
counted from durable `verdict.json` receipts, so a backend restart does not
reset the budget. A repeated clear defect after that one steer enters Human
Review with the screenshots, verdict, and named defects attached.

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
    "contractVersion": 2,
    "pipelineId": "ui-iteration-task-pipeline",
    "iteration": 2,
    "maxIterations": 4,
    "capReached": false,
    "artifactPaths": [
      "ui-iteration-002/after.png",
      "ui-iteration-002/visual-qa/round-002/01-task-detail--real.png"
    ],
    "changeDescriptionPath": "ui-iteration-002/changes.md",
    "visualQaStatus": "acceptable",
    "visualQaVerdictPath": "ui-iteration-002/visual-qa/round-002/verdict.json",
    "visualQaDefects": [],
    "visualQaAutoRetryUsed": true
  }
}
```

All paths in `uiIterationReview` are relative to `results/`. Part 2 must:

- treat `kind == "ui-iteration-review"` and `contractVersion == 2` as the
  consumer discriminator;
- present the listed artifacts and change description;
- complete the task when the human chooses finish;
- submit feedback through the existing Continue flow for another iteration;
- never submit another Continue when `capReached` is true. The runner enforces
  this boundary and escalates such an attempt to `5e-escalated`.

`completion-marker.json` temporarily protects the move to Human Review from a
backend crash. It is cleared after the move; `steer-pending.json` remains until
Part 2 resolves the human decision or a new accepted continuation starts.
