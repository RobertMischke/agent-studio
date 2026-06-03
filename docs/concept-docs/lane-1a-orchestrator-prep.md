# Orchestrator Prep

The `1a-orchestrator-prep` lane is the orchestrator's own workspace for refining a task before it becomes runnable. When intake takes a rough card and rewrites it into a sharp, self-contained prompt — clarifying scope, adding acceptance criteria, pulling in context — that work happens here, distinct from the human-driven `1-preparation` lane.

## What to expect

Cards here are mid-refinement by an automated step, not waiting on you. A card normally leaves on its own once intake finishes: forward to `2-ready` when the task is well-formed, or escalated to `5-human-review` when the refinement hits a decision that needs a person. If a card lingers here, the intake step is still working or has stalled — check the activity log rather than moving the card by hand, which would fight the orchestrator.
